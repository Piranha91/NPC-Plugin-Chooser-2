using System.Globalization;
using System.IO;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace NPC_Plugin_Chooser_2.Tests.Integration;

/// <summary>
/// Live diagnostic: does the new v2 search endpoint
/// (<see cref="FaceFinderClient.GetAllFaceDataForNpcV2Async"/>, a plain <c>hasMore</c>
/// pagination walk) return the same per-NPC face set as the current "amalgamation hack"
/// (<see cref="FaceFinderClient.GetAllFaceDataForNpcAsync"/>, which fetches page 1 twice —
/// implicit + explicit — and de-duplicates to defend against the server's page-1
/// divergence)?
///
/// <para>The server maintainer reports the page-1 bug is fixed on the v2 endpoints, so the
/// union-of-both-page-1-views work-around should no longer be necessary. This test answers
/// that empirically: it samples ~100 real NPCs off the live server, runs both methods per
/// NPC, and asserts the resulting face sets are equal. If it passes, the amalgamation hack
/// can be retired in favour of the v2 walk. If it fails, the per-NPC report names exactly
/// which faces each method saw that the other did not — i.e. it tells you the hack is still
/// load-bearing and where v2 still diverges.</para>
///
/// <para><b>Opt-in and live.</b> Every assertion here issues real HTTPS calls to
/// npcfacefinder.com (hundreds of them for a 100-NPC sample) using the client's embedded
/// API key, so it is NOT part of the deterministic offline suite. It runs only when the
/// <c>NPC2_FACEFINDER_LIVE</c> environment variable is set to a truthy value; otherwise it
/// skips as a no-op so a default <c>dotnet test</c> stays offline and green. The sample size
/// is overridable via <c>NPC2_FACEFINDER_SAMPLE</c> (default 100).</para>
///
/// <para>Equivalence is compared on the <c>(ModName, ImageUrl)</c> identity of each returned
/// face — that is what the app actually consumes per NPC. <c>UpdatedAt</c> is deliberately
/// excluded so timestamp-formatting differences between the endpoints cannot manufacture a
/// false mismatch. Comparing as sets also means v2 returning duplicates (it does no
/// client-side dedup) is not by itself a failure; duplicate counts are reported as
/// information.</para>
///
/// <para>The NPC sample is harvested from the server itself (mods -> faces) so the test
/// never depends on a Skyrim install and always exercises NPCs the server actually has
/// faces for. The harvest uses the (v1) mod-faces walk; the harvest is not the thing under
/// test, only the per-NPC v1-vs-v2 comparison is.</para>
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class FaceFinderV2EquivalenceTests
{
    private const string LiveEnvVar = "NPC2_FACEFINDER_LIVE";
    private const string SampleSizeEnvVar = "NPC2_FACEFINDER_SAMPLE";
    private const int DefaultSampleSize = 100;

    private readonly ITestOutputHelper _output;
    public FaceFinderV2EquivalenceTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task V2Search_ReturnsSameFaceSetAsAmalgamationHack()
    {
        if (!IsLiveRunRequested(out var liveSkip))
        {
            _output.WriteLine("SKIPPED: " + liveSkip);
            return;
        }

        int targetSample = GetSampleSize();

        // The client ctor clears a log file in the CWD; point the CWD at a temp dir so nothing
        // leaks into the source tree (same trick the offline FaceFinder tests use).
        using var tmp = new TempDir();
        var settings = new Settings();
        var client = MakeClient(settings, tmp.Path);

        // ── 1. Harvest a sample of real NPC FormKeys the server has faces for. ──────────────
        var sample = await HarvestNpcSampleAsync(client, targetSample);
        if (sample.Count == 0)
        {
            _output.WriteLine(
                "SKIPPED: could not harvest any NPC form keys from the server " +
                "(no network, bad API key, or empty mod/faces index).");
            return;
        }

        _output.WriteLine($"Sampled {sample.Count} NPC(s) from the server " +
                          $"(target {targetSample}). Comparing v1 amalgamation vs v2 walk...");

        // ── 2. Compare v1 vs v2 per NPC. ────────────────────────────────────────────────────
        var mismatches = new List<string>();
        int matched = 0;
        int v2DuplicateNpcs = 0;
        long totalV1Faces = 0, totalV2Faces = 0;

        foreach (var npc in sample)
        {
            var v1 = await client.GetAllFaceDataForNpcAsync(npc);
            var v2 = await client.GetAllFaceDataForNpcV2Async(npc);

            totalV1Faces += v1.Count;
            totalV2Faces += v2.Count;

            var v1Set = ToFaceSet(v1);
            var v2Set = ToFaceSet(v2);

            if (v2.Count != v2Set.Count) v2DuplicateNpcs++; // v2 returned a face twice

            if (v1Set.SetEquals(v2Set))
            {
                matched++;
                continue;
            }

            var onlyInV1 = v1Set.Except(v2Set).OrderBy(s => s).ToList();
            var onlyInV2 = v2Set.Except(v1Set).OrderBy(s => s).ToList();

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"  NPC {npc}: v1={v1Set.Count} face(s), v2={v2Set.Count} face(s)");
            foreach (var f in onlyInV1) sb.AppendLine($"      only in v1 (amalgamation): {f}");
            foreach (var f in onlyInV2) sb.AppendLine($"      only in v2 (clean walk):  {f}");
            mismatches.Add(sb.ToString().TrimEnd());
        }

        // ── 3. Report, then assert. ─────────────────────────────────────────────────────────
        _output.WriteLine(
            $"Compared {sample.Count} NPC(s): {matched} identical, {mismatches.Count} divergent. " +
            $"Total faces returned — v1: {totalV1Faces}, v2: {totalV2Faces}. " +
            $"v2 returned intra-NPC duplicates for {v2DuplicateNpcs} NPC(s).");

        if (mismatches.Count == 0)
        {
            _output.WriteLine(
                "RESULT: v2 is equivalent to the amalgamation hack across the sample — " +
                "the dual page-1 fetch + dedup can be retired in favour of GetAllFaceDataForNpcV2Async.");
        }
        else
        {
            _output.WriteLine(
                "RESULT: v2 still diverges from the amalgamation hack — the hack is still load-bearing. " +
                "Divergences:");
            foreach (var m in mismatches) _output.WriteLine(m);
        }

        mismatches.Should().BeEmpty(
            "the v2 endpoint must return the same per-NPC face set as the amalgamation hack " +
            "before the hack can be dropped (see test output for the per-NPC divergences)");
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────

    private static bool IsLiveRunRequested(out string skipReason)
    {
        var raw = Environment.GetEnvironmentVariable(LiveEnvVar);
        if (string.IsNullOrWhiteSpace(raw)
            || raw.Equals("0", StringComparison.Ordinal)
            || raw.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            skipReason =
                $"live FaceFinder comparison is opt-in; set {LiveEnvVar}=1 to run it " +
                "(makes real HTTPS calls to npcfacefinder.com).";
            return false;
        }
        skipReason = string.Empty;
        return true;
    }

    private static int GetSampleSize()
    {
        var raw = Environment.GetEnvironmentVariable(SampleSizeEnvVar);
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0)
            return n;
        return DefaultSampleSize;
    }

    /// <summary>
    /// Constructs a real <see cref="FaceFinderClient"/> with the process CWD pointed at
    /// <paramref name="cwd"/> so the ctor's log-file housekeeping writes only into the temp dir.
    /// The CWD is restored before returning.
    /// </summary>
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

    /// <summary>
    /// Pulls distinct NPC <see cref="FormKey"/>s off the live server (mods -> faces) until
    /// <paramref name="target"/> are collected or the mod list is exhausted. Returns whatever
    /// it managed to gather (possibly empty if the server is unreachable).
    /// </summary>
    private async Task<List<FormKey>> HarvestNpcSampleAsync(FaceFinderClient client, int target)
    {
        var collected = new List<FormKey>();
        var seen = new HashSet<FormKey>();

        var mods = await client.GetAllModsAsync();
        if (mods.Count == 0) return collected;

        // Deterministic order so a partial sample is stable run-to-run for a given server state.
        foreach (var modId in mods.Values.OrderBy(id => id))
        {
            if (collected.Count >= target) break;

            List<FaceFinderModFaceResult> faces;
            try
            {
                faces = await client.GetAllFacesForModAsync(modId);
            }
            catch
            {
                continue; // a single bad mod must not abort the harvest
            }

            foreach (var face in faces)
            {
                if (collected.Count >= target) break;
                if (TryParseServerFormKey(face.FormKey, out var fk) && seen.Add(fk))
                    collected.Add(fk);
            }
        }

        return collected;
    }

    /// <summary>
    /// Parses the server's form-key string (NPC2 sends it as <c>{ID:X8}:{lowercased plugin}</c>,
    /// e.g. <c>0001A696:skyrim.esm</c>) back into a Mutagen <see cref="FormKey"/>. Round-trips
    /// through the same representation NPC2 emits, so the rebuilt key re-encodes to the exact
    /// string both search methods send to the server.
    /// </summary>
    internal static bool TryParseServerFormKey(string? serverFormKey, out FormKey formKey)
    {
        formKey = default;
        if (string.IsNullOrWhiteSpace(serverFormKey)) return false;

        var parts = serverFormKey.Split(':');
        if (parts.Length != 2) return false;

        if (!uint.TryParse(parts[0].Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id))
            return false;
        if (id > 0xFFFFFF) return false; // a form ID is 24-bit; the X8 prefix is just zero padding

        var filename = parts[1].Trim();
        if (string.IsNullOrWhiteSpace(filename)) return false;

        try
        {
            formKey = FormKey.Factory($"{id:X6}:{filename}");
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Projects a per-NPC result list to its comparable identity set: one entry per face,
    /// keyed by <c>ModName</c> + <c>ImageUrl</c> (case-sensitive — URLs and mod names are
    /// compared as the server delivers them). De-dups, so duplicate rows collapse.
    /// </summary>
    private static HashSet<string> ToFaceSet(IEnumerable<FaceFinderNpcResult> faces) =>
        // Newline separator keeps the composite key unambiguous (a URL has no line breaks).
        faces.Select(f => f.ModName + "\n" + f.ImageUrl).ToHashSet(StringComparer.Ordinal);
}
