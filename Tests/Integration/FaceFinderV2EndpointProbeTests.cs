using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using FluentAssertions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace NPC_Plugin_Chooser_2.Tests.Integration;

/// <summary>
/// Live discovery probe: do the v2 server endpoints exist for the two remaining v1
/// "amalgamation" walks — <see cref="FaceFinderClient.GetAllModsAsync"/>
/// (<c>/api/public/mods/search</c>) and <see cref="FaceFinderClient.GetAllFacesForModAsync"/>
/// (<c>/api/public/mod/faces/search</c>) — and, if so, what JSON shape do they return?
///
/// <para>The server maintainer only documented the v2 <i>NPC</i> face endpoint, and explicitly
/// hinted the message may not be comprehensive. Rather than assume the mod/mods endpoints were
/// migrated with the same <c>{ results, hasMore }</c> envelope, this probe asks the server
/// directly: it hits the candidate v2 URLs and dumps HTTP status + top-level structure +
/// sample element keys, so a migration can be written against the <i>real</i> shape (or skipped
/// if the endpoints 404). It asserts nothing about equivalence — it is a reconnaissance tool
/// whose output is read by a human.</para>
///
/// <para>Opt-in and live, exactly like <see cref="FaceFinderV2EquivalenceTests"/>: runs only
/// when <c>NPC2_FACEFINDER_LIVE</c> is truthy; otherwise it skips as a no-op so the default
/// offline suite stays green.</para>
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class FaceFinderV2EndpointProbeTests
{
    private const string LiveEnvVar = "NPC2_FACEFINDER_LIVE";
    private const string ApiBaseUrl = "https://npcfacefinder.com";

    private readonly ITestOutputHelper _output;
    public FaceFinderV2EndpointProbeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Probe_V2ModAndModsEndpoints_ReportShape()
    {
        if (!IsLiveRunRequested(out var liveSkip))
        {
            _output.WriteLine("SKIPPED: " + liveSkip);
            return;
        }

        var apiKey = Reflect.InvokeStatic<FaceFinderClient, string>("GetAPIKey")!;
        using var http = new HttpClient();

        // Grab a real modId to probe the per-mod faces endpoint with. Use the v1 mods walk
        // (the thing we're investigating replacing) just to source a valid id.
        using var tmp = new TempDir();
        var client = MakeClient(new Settings(), tmp.Path);
        var v1Mods = await client.GetAllModsAsync();
        _output.WriteLine($"v1 GetAllModsAsync returned {v1Mods.Count} mod(s).");
        int? sampleModId = v1Mods.Count > 0 ? v1Mods.Values.OrderBy(id => id).First() : (int?)null;

        // ── Candidate v2 endpoints to probe (page 1 only — enough to learn the shape). ──────
        var probes = new List<(string label, string uri)>
        {
            ("v2 mods/search (no page)",       "/api/public/v2/mods/search"),
            ("v2 mods/search?page=1",          "/api/public/v2/mods/search?page=1"),
        };
        if (sampleModId is int modId)
        {
            probes.Add(("v2 mod/faces/search (no page)",
                $"/api/public/v2/mod/faces/search?modId={modId}"));
            probes.Add(("v2 mod/faces/search?page=1",
                $"/api/public/v2/mod/faces/search?modId={modId}&page=1"));
        }
        else
        {
            _output.WriteLine("WARNING: no modId available; skipping the mod/faces probes.");
        }

        foreach (var (label, uri) in probes)
        {
            _output.WriteLine("");
            _output.WriteLine($"=== {label} ===");
            _output.WriteLine($"GET {ApiBaseUrl + uri}");

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiBaseUrl + uri));
                req.Headers.Add("X-API-Key", apiKey);
                using var resp = await http.SendAsync(req);
                var body = await resp.Content.ReadAsStringAsync();

                _output.WriteLine($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
                if (!resp.IsSuccessStatusCode)
                {
                    _output.WriteLine("Body (first 400 chars): " + Truncate(body, 400));
                    continue;
                }

                _output.WriteLine(DescribeJson(body));
            }
            catch (Exception ex)
            {
                _output.WriteLine($"EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // The probe is informational; it must not fail the run regardless of what the server says.
        true.Should().BeTrue();
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Describes the top-level JSON shape: whether it is a bare array (the v1 mod/mods shape)
    /// or a <c>{ results, hasMore }</c>-style envelope (the v2 NPC shape), how many elements it
    /// carries, and the property names of the first element — enough to know whether a v2
    /// migration can reuse the existing field reads (<c>id</c>/<c>name</c> for mods,
    /// <c>npc.form_key</c>/<c>images.full</c>/<c>updated_at</c> for faces).
    /// </summary>
    private static string DescribeJson(string body)
    {
        JToken root;
        try
        {
            root = JsonConvert.DeserializeObject<JToken>(body, new JsonSerializerSettings
            {
                DateParseHandling = DateParseHandling.None
            })!;
        }
        catch (Exception ex)
        {
            return "Body is not valid JSON (" + ex.Message + "): " + Truncate(body, 400);
        }

        if (root is JArray bareArray)
        {
            return "Top level: ARRAY (bare, v1-style). " +
                   $"count={bareArray.Count}. First element: {DescribeElement(bareArray.FirstOrDefault())}";
        }

        if (root is JObject obj)
        {
            var keys = string.Join(", ", obj.Properties().Select(p => p.Name));
            var sb = new System.Text.StringBuilder();
            sb.Append($"Top level: OBJECT (envelope, v2-style). keys=[{keys}].");

            // Common envelope shapes: results[] + hasMore/has_more.
            var results = (obj["results"] ?? obj["data"] ?? obj["items"]) as JArray;
            if (results != null)
            {
                sb.Append($" results: array count={results.Count}, first element: {DescribeElement(results.FirstOrDefault())}.");
            }
            var hasMore = obj["hasMore"] ?? obj["has_more"];
            if (hasMore != null)
                sb.Append($" paging flag '{(obj["hasMore"] != null ? "hasMore" : "has_more")}'={hasMore} (type {hasMore.Type}).");
            else
                sb.Append(" NO hasMore/has_more flag found.");
            return sb.ToString();
        }

        return "Top level: " + root.Type + " => " + Truncate(body, 200);
    }

    private static string DescribeElement(JToken? el)
    {
        if (el is null) return "(none)";
        if (el is JObject o) return "{ " + string.Join(", ", o.Properties().Select(p => p.Name + ":" + p.Value.Type)) + " }";
        return el.Type.ToString();
    }

    private static string Truncate(string s, int n) =>
        string.IsNullOrEmpty(s) ? "(empty)" : (s.Length <= n ? s : s.Substring(0, n) + "…");

    private static bool IsLiveRunRequested(out string skipReason)
    {
        var raw = Environment.GetEnvironmentVariable(LiveEnvVar);
        if (string.IsNullOrWhiteSpace(raw)
            || raw.Equals("0", StringComparison.Ordinal)
            || raw.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            skipReason =
                $"live FaceFinder probe is opt-in; set {LiveEnvVar}=1 to run it " +
                "(makes real HTTPS calls to npcfacefinder.com).";
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
