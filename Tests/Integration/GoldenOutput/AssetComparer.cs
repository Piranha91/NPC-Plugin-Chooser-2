using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace NPC_Plugin_Chooser_2.Tests.Integration.GoldenOutput;

/// <summary>
/// Compares the asset trees (meshes/ + textures/) of a fresh patch run against a reference output.
/// Files are matched by relative path and compared by SHA-256 content hash.
///
/// <para>FaceGen assets are special-cased: their folder is named after the source plugin and (in SkyPatcher
/// mode) embeds a freshly-allocated FormID that legitimately differs between runs, so the path is unstable.
/// In SkyPatcher mode FaceGen files are therefore matched by content-hash multiset rather than by path.</para>
/// </summary>
internal static class AssetComparer
{
    public sealed class Result
    {
        public List<string> MissingFromFresh { get; } = new();   // present in reference, absent in fresh
        public List<string> ExtraInFresh { get; } = new();       // present in fresh, absent in reference
        public List<string> HashMismatches { get; } = new();     // same path, different content
        public int Compared { get; set; }

        public bool IsMatch => MissingFromFresh.Count == 0 && ExtraInFresh.Count == 0 && HashMismatches.Count == 0;

        public string Describe()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Assets compared: {Compared}. Missing={MissingFromFresh.Count}, Extra={ExtraInFresh.Count}, HashMismatch={HashMismatches.Count}.");
            foreach (var m in MissingFromFresh.Take(20)) sb.AppendLine("  MISSING from fresh: " + m);
            foreach (var e in ExtraInFresh.Take(20)) sb.AppendLine("  EXTRA in fresh:     " + e);
            foreach (var h in HashMismatches.Take(20)) sb.AppendLine("  HASH MISMATCH:      " + h);
            return sb.ToString();
        }
    }

    private static bool IsFaceGen(string relPath) =>
        relPath.Contains("facegendata/facegeom/", StringComparison.OrdinalIgnoreCase)
        || relPath.Contains("facegendata/facetint/", StringComparison.OrdinalIgnoreCase);

    public static Result Compare(string freshDir, string referenceDir, bool skyPatcherMode)
    {
        var fresh = Enumerate(freshDir);
        var reference = Enumerate(referenceDir);
        var result = new Result();

        // Path-stable assets (everything except FaceGen in SkyPatcher mode).
        var freshStable = fresh.Where(kv => !(skyPatcherMode && IsFaceGen(kv.Key))).ToDictionary(kv => kv.Key, kv => kv.Value);
        var refStable = reference.Where(kv => !(skyPatcherMode && IsFaceGen(kv.Key))).ToDictionary(kv => kv.Key, kv => kv.Value);

        foreach (var (rel, refPath) in refStable)
        {
            result.Compared++;
            if (!freshStable.TryGetValue(rel, out var freshPath))
            {
                result.MissingFromFresh.Add(rel);
                continue;
            }
            if (!HashEquals(freshPath, refPath))
            {
                result.HashMismatches.Add(rel +
                    "  -> content differs: (a) a patcher/test bug, or (b) an underlying test mod (e.g. USSEP, " +
                    "which version-drifts) was updated - regenerate the reference set.");
            }
        }
        foreach (var rel in freshStable.Keys.Where(k => !refStable.ContainsKey(k)))
        {
            result.ExtraInFresh.Add(rel);
        }

        // FaceGen in SkyPatcher mode: compare by content-hash multiset (paths embed allocator FormIDs).
        if (skyPatcherMode)
        {
            var freshHashes = Multiset(fresh.Where(kv => IsFaceGen(kv.Key)).Select(kv => Hash(kv.Value)));
            var refHashes = Multiset(reference.Where(kv => IsFaceGen(kv.Key)).Select(kv => Hash(kv.Value)));
            result.Compared += refHashes.Values.Sum();

            foreach (var (hash, count) in refHashes)
            {
                int freshCount = freshHashes.GetValueOrDefault(hash);
                if (freshCount < count)
                    result.MissingFromFresh.Add($"(facegen content {hash[..12]}, x{count - freshCount} missing by hash)");
            }
            foreach (var (hash, count) in freshHashes)
            {
                int refCount = refHashes.GetValueOrDefault(hash);
                if (refCount < count)
                    result.ExtraInFresh.Add($"(facegen content {hash[..12]}, x{count - refCount} extra by hash)");
            }
        }

        return result;
    }

    private static Dictionary<string, string> Enumerate(string root)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sub in new[] { "meshes", "textures" })
        {
            var dir = Path.Combine(root, sub);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/').ToLowerInvariant();
                map[rel] = file;
            }
        }
        return map;
    }

    private static Dictionary<string, int> Multiset(IEnumerable<string> items)
    {
        var d = new Dictionary<string, int>();
        foreach (var i in items) d[i] = d.GetValueOrDefault(i) + 1;
        return d;
    }

    private static bool HashEquals(string a, string b)
    {
        var fa = new FileInfo(a);
        var fb = new FileInfo(b);
        if (fa.Length != fb.Length) return false;
        return Hash(a) == Hash(b);
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
