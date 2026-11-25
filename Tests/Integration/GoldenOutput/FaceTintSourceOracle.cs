using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Plugins;

namespace NPC_Plugin_Chooser_2.Tests.Integration.GoldenOutput;

/// <summary>
/// Checks every face tint a golden run delivers against the file it was copied FROM, read at test
/// time, instead of against a stored copy of it. A face tint is forwarded byte for byte, so
/// "output equals source" is the property; a stored reference only records what the source was on
/// the day it was captured. (The Base Game selection's tint went stale that way: the game's
/// texture archive was re-issued on 2026-08-28 and three combos failed on one file whose fresh
/// output was correct.)
///
/// <para><b>Independent of the patcher.</b> The expected source is derived from the selection —
/// the donor NPC's own tint path, looked up in the selected mod's folders or, for the synthetic
/// "Base Game" entry, in the game's own archives — never from the patcher's provenance log, which
/// would agree with whatever source the patcher picked, right or wrong. Archives are read with
/// Mutagen directly rather than through <c>BsaHandler</c> for the same reason.</para>
///
/// <para><b>Content only.</b> Whether a tint is delivered at all stays the reference comparison's
/// job (<see cref="AssetComparer"/> reports MISSING / EXTRA). A tint that is not delivered, or
/// whose source cannot be pinned to exactly one file, is reported and asserts nothing.</para>
/// </summary>
internal static class FaceTintSourceOracle
{
    public enum Outcome
    {
        /// <summary>Delivered, and byte-identical to its source.</summary>
        Verified,
        /// <summary>Delivered, and different from its source: a wrong or altered file.</summary>
        Mismatch,
        /// <summary>The run delivered no tint for this target (presence is the reference's concern).</summary>
        NotDelivered,
        /// <summary>No single source file could be identified, so nothing is asserted.</summary>
        SourceUnresolved,
    }

    public sealed record Verdict(
        FormKey Target, string Mod, FormKey Donor, string FreshRel, Outcome Outcome,
        string Detail, string? FreshHash, string? SourceHash);

    private const string BaseGameModName = "Base Game";

    // The eight selections are the same in every combo, and resolving a base-game source walks
    // archive file tables, so each (mod, donor) is resolved once per test run.
    private static readonly Dictionary<string, (string? Hash, string Description)> SourceCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly object CacheLock = new();

    /// <summary>Output-relative tint path for an NPC, in <see cref="AssetComparer"/>'s spelling
    /// (forward slashes, lower case).</summary>
    public static string TintRelPath(FormKey npc) =>
        $"textures/actors/character/facegendata/facetint/{npc.ModKey.FileName.String.ToLowerInvariant()}/{npc.ID:x8}.dds";

    /// <param name="dataFolder">The game's Data folder (base-game archives).</param>
    /// <param name="surrogateByTarget">SkyPatcher output delivers each target's FaceGen under its
    /// surrogate's FormKey: pass the fresh ini's target -&gt; surrogate map. Null for record output,
    /// where the tint sits under the target's own FormKey.</param>
    public static List<Verdict> Check(GoldenOutputConfig config, string dataFolder, string outDir,
        IReadOnlyDictionary<FormKey, FormKey>? surrogateByTarget)
    {
        var verdicts = new List<Verdict>();
        foreach (var (targetText, mod, donorText) in GoldenComboSettingsBuilder.Selections)
        {
            var target = FormKey.Factory(targetText);
            var donor = FormKey.Factory(donorText);

            var deliveredUnder = target;
            if (surrogateByTarget != null && !surrogateByTarget.TryGetValue(target, out deliveredUnder))
            {
                verdicts.Add(new Verdict(target, mod, donor, string.Empty, Outcome.NotDelivered,
                    "the fresh ini names no surrogate for this target", null, null));
                continue;
            }

            var freshRel = TintRelPath(deliveredUnder);
            var freshPath = Path.Combine(outDir, freshRel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(freshPath))
            {
                verdicts.Add(new Verdict(target, mod, donor, freshRel, Outcome.NotDelivered,
                    "no tint in the output", null, null));
                continue;
            }

            var (sourceHash, description) = ResolveSource(config, dataFolder, mod, donor);
            if (sourceHash == null)
            {
                verdicts.Add(new Verdict(target, mod, donor, freshRel, Outcome.SourceUnresolved,
                    description, null, null));
                continue;
            }

            var freshHash = HashFile(freshPath);
            verdicts.Add(new Verdict(target, mod, donor, freshRel,
                freshHash == sourceHash ? Outcome.Verified : Outcome.Mismatch,
                description, freshHash, sourceHash));
        }
        return verdicts;
    }

    /// <summary>The donor's tint as its mod ships it: (hash, where it was read), or (null, why not).</summary>
    private static (string? Hash, string Description) ResolveSource(
        GoldenOutputConfig config, string dataFolder, string mod, FormKey donor)
    {
        // Keyed by where the mod lives as well as its name, so two configurations of one mod name
        // (the golden environment and a unit test's temp folders) never share an answer.
        var roots = config.AppearanceMods.TryGetValue(mod, out var known) ? string.Join(";", known.Folders) : string.Empty;
        var cacheKey = string.Join("|", dataFolder, roots, mod, donor.ToString());
        lock (CacheLock)
        {
            if (SourceCache.TryGetValue(cacheKey, out var cached)) return cached;
        }

        var resolved = ResolveSourceUncached(config, dataFolder, mod, donor);
        lock (CacheLock)
        {
            SourceCache[cacheKey] = resolved;
        }
        return resolved;
    }

    private static (string? Hash, string Description) ResolveSourceUncached(
        GoldenOutputConfig config, string dataFolder, string mod, FormKey donor)
    {
        var rel = TintRelPath(donor);
        var archivePath = rel.Replace('/', '\\');

        IReadOnlyList<string> archives;
        if (string.Equals(mod, BaseGameModName, StringComparison.OrdinalIgnoreCase))
        {
            // The base game ships everything in "Skyrim - *.bsa"; a loose copy in Data would win in
            // game and would be picked up here first, as it would by the patcher.
            var loose = Path.Combine(dataFolder, rel.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(loose)) return (HashFile(loose), $"loose file {loose}");
            archives = Directory.Exists(dataFolder)
                ? Directory.GetFiles(dataFolder, "Skyrim - *.bsa").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList()
                : new List<string>();
        }
        else
        {
            if (!config.AppearanceMods.TryGetValue(mod, out var entry))
                return (null, $"mod '{mod}' is not in the environment map");

            // A mod's later folders override its earlier ones (ModSetting.CorrespondingFolderPaths).
            foreach (var folder in Enumerable.Reverse(entry.Folders))
            {
                var loose = Path.Combine(folder, rel.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(loose)) return (HashFile(loose), $"loose file {loose}");
            }
            archives = entry.Folders
                .Where(Directory.Exists)
                .SelectMany(folder => Directory.GetFiles(folder, "*.bsa"))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var hits = new List<(string Archive, string Hash)>();
        foreach (var archive in archives)
        {
            var reader = Archive.CreateReader(GameRelease.SkyrimSE, archive);
            var file = reader.Files.FirstOrDefault(f =>
                f.Path.Replace('/', '\\').Equals(archivePath, StringComparison.OrdinalIgnoreCase));
            if (file == null) continue;

            using var stream = file.AsStream();
            hits.Add((archive, Convert.ToHexString(SHA256.HashData(stream))));
        }

        return hits.Count switch
        {
            1 => (hits[0].Hash, $"{Path.GetFileName(hits[0].Archive)}|{archivePath}"),
            0 => (null, $"'{rel}' is neither loose nor in any of {archives.Count} archive(s) of '{mod}'"),
            // More than one archive carries it: which one the game uses depends on archive order,
            // and an oracle that guesses is no oracle. Identical copies are not a conflict.
            _ when hits.Select(h => h.Hash).Distinct().Count() == 1 =>
                (hits[0].Hash, $"{hits.Count} identical copies, e.g. {Path.GetFileName(hits[0].Archive)}|{archivePath}"),
            _ => (null, $"'{rel}' differs between {string.Join(", ", hits.Select(h => Path.GetFileName(h.Archive)))}"),
        };
    }

    public static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
