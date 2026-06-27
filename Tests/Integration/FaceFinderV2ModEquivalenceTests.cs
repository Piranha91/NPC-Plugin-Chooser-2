using System.Globalization;
using System.IO;
using FluentAssertions;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace NPC_Plugin_Chooser_2.Tests.Integration;

/// <summary>
/// Live diagnostic, sibling to <see cref="FaceFinderV2EquivalenceTests"/>, for the other two
/// "amalgamation hacks": does the v2 server endpoint return the same result set as the v1
/// dual-page-1 + dedup walk for
///   • <see cref="FaceFinderClient.GetAllModsAsync"/> vs <see cref="FaceFinderClient.GetAllModsV2Async"/>
///     (<c>/api/public/[v2/]mods/search</c>), and
///   • <see cref="FaceFinderClient.GetAllFacesForModAsync"/> vs
///     <see cref="FaceFinderClient.GetAllFacesForModV2Async"/>
///     (<c>/api/public/[v2/]mod/faces/search</c>).
///
/// <para>The endpoint-probe test confirmed both v2 URLs exist with the
/// <c>{ page, pageSize, hasMore, results }</c> envelope and the same per-element fields v1 reads;
/// these tests confirm the <i>content</i> matches before the batch path is switched over. If
/// green, the dual page-1 fetch + dedup can be retired for mods/mod-faces too (gated by the same
/// <c>FaceFinderSearchFallback.txt</c> fallback). If red, the per-item report names the
/// divergences.</para>
///
/// <para>Opt-in and live (same <c>NPC2_FACEFINDER_LIVE</c> gate as the NPC test). The mod-faces
/// comparison samples up to <c>NPC2_FACEFINDER_MOD_SAMPLE</c> mods (default 50) to bound the call
/// volume; the mods comparison is a single full walk on each side.</para>
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class FaceFinderV2ModEquivalenceTests
{
    private const string LiveEnvVar = "NPC2_FACEFINDER_LIVE";
    private const string ModSampleEnvVar = "NPC2_FACEFINDER_MOD_SAMPLE";
    private const int DefaultModSample = 50;

    private readonly ITestOutputHelper _output;
    public FaceFinderV2ModEquivalenceTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task V2ModsSearch_ReturnsSameModMapAsAmalgamationHack()
    {
        if (!IsLiveRunRequested(out var liveSkip)) { _output.WriteLine("SKIPPED: " + liveSkip); return; }

        using var tmp = new TempDir();
        var client = MakeClient(new Settings(), tmp.Path);

        var v1 = await client.GetAllModsAsync();
        var v2 = await client.GetAllModsV2Async();

        if (v1.Count == 0)
        {
            _output.WriteLine("SKIPPED: v1 GetAllModsAsync returned no mods (no network / bad key / empty index).");
            return;
        }

        _output.WriteLine($"Mods — v1: {v1.Count} name(s), v2: {v2.Count} name(s).");

        // Compare the distinct mod-id sets (the substantive "did we see the same mods" question)…
        var v1Ids = v1.Values.ToHashSet();
        var v2Ids = v2.Values.ToHashSet();
        var idsOnlyInV1 = v1Ids.Except(v2Ids).OrderBy(i => i).ToList();
        var idsOnlyInV2 = v2Ids.Except(v1Ids).OrderBy(i => i).ToList();

        // …and the full name→id mapping (catches a name resolving to a different id between walks).
        var mapDiffs = new List<string>();
        foreach (var kvp in v1)
        {
            if (!v2.TryGetValue(kvp.Key, out var v2Id))
                mapDiffs.Add($"  name '{kvp.Key}' -> v1 id {kvp.Value}, absent in v2");
            else if (v2Id != kvp.Value)
                mapDiffs.Add($"  name '{kvp.Key}' -> v1 id {kvp.Value}, v2 id {v2Id}");
        }
        foreach (var kvp in v2)
            if (!v1.ContainsKey(kvp.Key))
                mapDiffs.Add($"  name '{kvp.Key}' -> v2 id {kvp.Value}, absent in v1");

        if (idsOnlyInV1.Count > 0) _output.WriteLine($"  ids only in v1 ({idsOnlyInV1.Count}): {string.Join(", ", idsOnlyInV1.Take(50))}");
        if (idsOnlyInV2.Count > 0) _output.WriteLine($"  ids only in v2 ({idsOnlyInV2.Count}): {string.Join(", ", idsOnlyInV2.Take(50))}");
        if (mapDiffs.Count > 0)
        {
            _output.WriteLine($"  name→id mapping differences ({mapDiffs.Count}):");
            foreach (var d in mapDiffs.Take(50)) _output.WriteLine(d);
        }

        _output.WriteLine(idsOnlyInV1.Count == 0 && idsOnlyInV2.Count == 0 && mapDiffs.Count == 0
            ? "RESULT: v2 mods/search is equivalent to the amalgamation hack — safe to migrate."
            : "RESULT: v2 mods/search diverges from the amalgamation hack — hack still load-bearing.");

        v1Ids.SetEquals(v2Ids).Should().BeTrue("v2 mods/search must return the same mod-id set as the v1 amalgamation walk");
        mapDiffs.Should().BeEmpty("v2 mods/search must resolve the same name→id mapping as the v1 walk");
    }

    [Fact]
    public async Task V2ModFacesSearch_ReturnsSameFaceSetAsAmalgamationHack()
    {
        if (!IsLiveRunRequested(out var liveSkip)) { _output.WriteLine("SKIPPED: " + liveSkip); return; }

        using var tmp = new TempDir();
        var client = MakeClient(new Settings(), tmp.Path);

        var mods = await client.GetAllModsAsync();
        if (mods.Count == 0)
        {
            _output.WriteLine("SKIPPED: could not list mods to sample (no network / bad key / empty index).");
            return;
        }

        int sampleSize = GetModSampleSize();
        var sampledModIds = mods.Values.Distinct().OrderBy(id => id).Take(sampleSize).ToList();
        _output.WriteLine($"Sampling {sampledModIds.Count} of {mods.Values.Distinct().Count()} mod(s) for mod/faces comparison...");

        var mismatches = new List<string>();
        int matched = 0;
        long totalV1 = 0, totalV2 = 0;

        foreach (var modId in sampledModIds)
        {
            var v1 = await client.GetAllFacesForModAsync(modId);
            var v2 = await client.GetAllFacesForModV2Async(modId);

            totalV1 += v1.Count;
            totalV2 += v2.Count;

            var v1Set = ToFaceSet(v1);
            var v2Set = ToFaceSet(v2);
            if (v1Set.SetEquals(v2Set)) { matched++; continue; }

            var onlyInV1 = v1Set.Except(v2Set).OrderBy(s => s).ToList();
            var onlyInV2 = v2Set.Except(v1Set).OrderBy(s => s).ToList();
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"  modId {modId}: v1={v1Set.Count} face(s), v2={v2Set.Count} face(s)");
            foreach (var f in onlyInV1) sb.AppendLine($"      only in v1: {f}");
            foreach (var f in onlyInV2) sb.AppendLine($"      only in v2: {f}");
            mismatches.Add(sb.ToString().TrimEnd());
        }

        _output.WriteLine(
            $"Compared {sampledModIds.Count} mod(s): {matched} identical, {mismatches.Count} divergent. " +
            $"Total faces — v1: {totalV1}, v2: {totalV2}.");

        if (mismatches.Count == 0)
        {
            _output.WriteLine("RESULT: v2 mod/faces/search is equivalent to the amalgamation hack — safe to migrate.");
        }
        else
        {
            _output.WriteLine("RESULT: v2 mod/faces/search diverges — hack still load-bearing. Divergences:");
            foreach (var m in mismatches) _output.WriteLine(m);
        }

        mismatches.Should().BeEmpty(
            "the v2 mod/faces endpoint must return the same per-mod face set as the amalgamation hack " +
            "before the hack can be dropped (see test output for the per-mod divergences)");
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────

    /// <summary>Face identity for set comparison: server form key + full image URL (what the
    /// app keys off). ExternalUrl/UpdatedAt are excluded so representation differences cannot
    /// fake a mismatch.</summary>
    private static HashSet<string> ToFaceSet(IEnumerable<FaceFinderModFaceResult> faces) =>
        faces.Select(f => f.FormKey + "\n" + f.ImageUrl).ToHashSet(StringComparer.Ordinal);

    private static int GetModSampleSize()
    {
        var raw = Environment.GetEnvironmentVariable(ModSampleEnvVar);
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0) return n;
        return DefaultModSample;
    }

    private static bool IsLiveRunRequested(out string skipReason)
    {
        var raw = Environment.GetEnvironmentVariable(LiveEnvVar);
        if (string.IsNullOrWhiteSpace(raw)
            || raw.Equals("0", StringComparison.Ordinal)
            || raw.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            skipReason = $"live FaceFinder comparison is opt-in; set {LiveEnvVar}=1 to run it.";
            return false;
        }
        skipReason = string.Empty;
        return true;
    }

    private static FaceFinderClient MakeClient(Settings settings, string cwd)
    {
        var previous = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(cwd);
        try
        {
            var logger = new EventLogger(settings);
            return new FaceFinderClient(settings, logger);
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }
    }
}
