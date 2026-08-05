using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NPC_Plugin_Chooser_2.BackEnd;

namespace NPC_Plugin_Chooser_2.Tests.Integration.GoldenOutput;

/// <summary>
/// Tolerance for reference sets that predate the 2026-08 shared/surrogate FaceGen tint rewrite
/// (<see cref="AssetHandler.RewriteCopiedFaceTintPath"/>): a FaceGen NIF delivered under a
/// different NPC's FormKey now has its baked face-tint slot re-pointed at the tint delivered
/// beside it, so it can no longer hash-match a reference captured as a straight copy of the
/// donor's file.
///
/// <para>The tolerance is byte-verifying, not a blanket waiver: for each candidate, the REFERENCE
/// file is copied to temp, the same rewrite is applied to it (target tint derived from the
/// delivered path's own plugin + FormID segments), and the result must hash-equal the fresh
/// output exactly. Anything else the patcher changed — geometry, other texture slots, a wrong
/// tint — still fails the comparison. Remove per combo once its reference set is regenerated
/// (see <see cref="GoldenCombos.IsStaleForSharedTintRewriteFix"/>).</para>
/// </summary>
internal static class TintRewriteTolerance
{
    /// <summary>
    /// Record-mode combos (path-stable comparison): removes each HASH MISMATCH row whose fresh
    /// file is byte-identical to the reference after the tint rewrite. Returns how many rows were
    /// tolerated. Mismatch rows are formatted "&lt;rel&gt;  -&gt; ..." by <see cref="AssetComparer"/>.
    /// </summary>
    public static int ApplyPathStable(string freshRoot, string refRoot, List<string> hashMismatches)
    {
        return hashMismatches.RemoveAll(row =>
        {
            var rel = row.Split("  ->", 2)[0].Trim();
            if (!rel.Contains("facegendata/facegeom/", StringComparison.OrdinalIgnoreCase) ||
                !rel.EndsWith(".nif", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var freshPath = Path.Combine(freshRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            var refPath = Path.Combine(refRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(freshPath) || !File.Exists(refPath)) return false;

            return RewrittenReferenceMatchesFresh(refPath, freshPath, SelfTintFor(freshPath));
        });
    }

    /// <summary>
    /// SkyPatcher combos (content-hash multiset): pairs every unmatched reference FaceGen NIF with
    /// an unmatched fresh one via the rewrite-and-rehash check. Only if EVERY unmatched file on
    /// both sides pairs up are the "(facegen content ...)" rows cleared — a partial pairing means
    /// something else drifted and the combo still fails. Returns the number of pairs verified.
    /// </summary>
    public static int ApplySkyPatcher(string freshRoot, string refRoot,
        List<string> missingFromFresh, List<string> extraInFresh)
    {
        bool IsContentRow(string row) => row.StartsWith("(facegen content", StringComparison.Ordinal);
        if (!missingFromFresh.Any(IsContentRow) && !extraInFresh.Any(IsContentRow)) return 0;

        var refUnmatched = UnmatchedFaceGenNifs(refRoot, freshRoot);
        var freshUnmatched = UnmatchedFaceGenNifs(freshRoot, refRoot);

        var pairedRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var freshPath in freshUnmatched)
        {
            var selfTint = SelfTintFor(freshPath);
            var match = refUnmatched.FirstOrDefault(refPath =>
                !pairedRefs.Contains(refPath) &&
                RewrittenReferenceMatchesFresh(refPath, freshPath, selfTint));
            if (match == null) return 0; // an unpaired fresh file: not (only) the tint fix — keep the failure
            pairedRefs.Add(match);
        }
        if (pairedRefs.Count != refUnmatched.Count) return 0; // reference files the fresh run no longer produces

        missingFromFresh.RemoveAll(IsContentRow);
        extraInFresh.RemoveAll(IsContentRow);
        return pairedRefs.Count;
    }

    /// <summary>FaceGen NIFs under <paramref name="root"/> whose content hash has no counterpart
    /// under <paramref name="otherRoot"/> (multiset semantics, mirroring <see cref="AssetComparer"/>).</summary>
    private static List<string> UnmatchedFaceGenNifs(string root, string otherRoot)
    {
        var otherHashes = new Dictionary<string, int>();
        foreach (var f in EnumerateFaceGenNifs(otherRoot))
        {
            var h = Hash(f);
            otherHashes[h] = otherHashes.GetValueOrDefault(h) + 1;
        }

        var unmatched = new List<string>();
        foreach (var f in EnumerateFaceGenNifs(root))
        {
            var h = Hash(f);
            if (otherHashes.GetValueOrDefault(h) > 0) otherHashes[h]--;
            else unmatched.Add(f);
        }
        return unmatched;
    }

    private static IEnumerable<string> EnumerateFaceGenNifs(string root)
    {
        var meshes = Path.Combine(root, "meshes");
        if (!Directory.Exists(meshes)) yield break;
        foreach (var f in Directory.GetFiles(meshes, "*.nif", SearchOption.AllDirectories))
        {
            if (f.Contains(@"facegendata\facegeom\", StringComparison.OrdinalIgnoreCase))
                yield return f;
        }
    }

    /// <summary>The tint path the patcher derives for a delivered FaceGen mesh: its own
    /// plugin-folder + FormID filename, in the exact (lowercase, regularized) spelling
    /// <see cref="Auxilliary.GetFaceGenSubPathStrings"/> produces — required for byte equality.</summary>
    private static string SelfTintFor(string deliveredNifPath)
    {
        var formId = Path.GetFileNameWithoutExtension(deliveredNifPath).ToLowerInvariant();
        var plugin = Path.GetFileName(Path.GetDirectoryName(deliveredNifPath))!.ToLowerInvariant();
        return $@"textures\actors\character\facegendata\facetint\{plugin}\{formId}.dds";
    }

    private static bool RewrittenReferenceMatchesFresh(string refPath, string freshPath, string selfTint)
    {
        // The reference's embedded tint is whatever the donor baked; read it back rather than guessing.
        var refTint = NifHandler.GetTexturesByShape(refPath)
            .SelectMany(s => s.TexturePaths)
            .FirstOrDefault(p => p.Replace('/', '\\')
                .Contains(@"facegendata\facetint\", StringComparison.OrdinalIgnoreCase));
        if (refTint == null) return false;

        var temp = Path.Combine(Path.GetTempPath(), "npc2-tinttolerance-" + Guid.NewGuid().ToString("N") + ".nif");
        try
        {
            File.Copy(refPath, temp);
            if (AssetHandler.RewriteCopiedFaceTintPath(temp, refTint, selfTint) == 0) return false;
            return Hash(temp) == Hash(freshPath);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
