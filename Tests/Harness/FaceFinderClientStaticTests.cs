using System.IO;
using System.Text;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using Newtonsoft.Json;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.Integration;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Harness;

/// <summary>
/// Deterministic, offline-reachable surface of <see cref="FaceFinderClient"/>:
///   • <c>GetAPIKey()</c> — private static XOR-decode of the obfuscated key bytes
///     (reflected; verified non-empty, stable across calls, pure ASCII).
///   • <see cref="FaceFinderClient.MetadataFileExtension"/> — public const sidecar suffix.
///   • <see cref="FaceFinderClient.FaceFinderMetadata"/> — JSON DTO round-trip through the
///     production serializer (Newtonsoft), including the "Source" default of "FaceFinder"
///     and the missing-Source -> default behaviour.
///   • <c>WriteMetadataAsync</c> / <c>ReadMetadataAsync</c> — the sidecar round-trip. These
///     are instance methods, so the client is constructed; the ctor only touches the local
///     working directory (clears a log file, swallowing any error) and never hits the network,
///     so it is safe offline. Construction happens with the process CWD redirected into a
///     <see cref="TempDir"/> so neither <c>EventLog.txt</c> nor <c>FaceFinderLog.txt</c> leaks
///     into the source tree.
///   • <c>IsCacheStaleAsync</c> — only its pre-network early-return branches (missing sidecar,
///     non-FaceFinder source, corrupt JSON). The "valid FaceFinder metadata" branch issues a
///     live API call and is covered by the integration wave (see NOTE below).
///
/// Determinism: no clock dependence (only stored timestamps are compared), no network, no game
/// install. The CWD redirect makes the client ctor / EventLogger ctor write only into a temp dir.
///
/// CWD is process-global, so the constructing tests serialise via the integration collection and
/// snapshot/restore the working directory themselves.
///
/// NOTE: GetFaceDataAsync / GetAllModsAsync / GetAllFacesForModAsync / GetAllModNamesAsync /
/// GetAllFaceDataForNpcAsync not covered: every one issues a live HTTP request to
/// https://npcfacefinder.com and has no injection seam for a fake transport, so they require
/// network access and belong to the separate integration wave.
/// NOTE: IsCacheStaleAsync "valid FaceFinder sidecar" branch not covered: it calls
/// GetFaceDataAsync (network) before deciding staleness.
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class FaceFinderClientStaticTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    private static string CallGetApiKey() =>
        Reflect.InvokeStatic<FaceFinderClient, string>("GetAPIKey")!;

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
            var logger = new EventLogger(settings); // writes EventLog.txt into cwd; errors swallowed
            return new FaceFinderClient(settings, logger);
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }
    }

    // ── GetAPIKey (private static) ─────────────────────────────────────────────

    [Fact]
    public void GetAPIKey_ReturnsNonEmptyString()
    {
        CallGetApiKey().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void GetAPIKey_IsDeterministicAcrossCalls()
    {
        // Pure function of static byte arrays + XOR key; every call must agree.
        var a = CallGetApiKey();
        var b = CallGetApiKey();
        b.Should().Be(a);
    }

    [Fact]
    public void GetAPIKey_DecodedLengthMatchesObfuscatedByteCount()
    {
        // The decoder XORs each obfuscated byte 1:1, then UTF8-decodes. Because the payload is
        // pure ASCII, the decoded char count equals the obfuscated byte count.
        var obfuscated = Reflect.GetField<byte[]>(
            Reflect.Uninitialized<FaceFinderClient>(), "_obfuscatedBytes");
        CallGetApiKey().Length.Should().Be(obfuscated.Length);
    }

    [Fact]
    public void GetAPIKey_IsPureAscii()
    {
        var key = CallGetApiKey();
        key.All(c => c <= '\x7F').Should().BeTrue("the API key is an ASCII token");
        // No control characters / whitespace padding in a header value.
        key.Should().NotContain("\0");
        key.Trim().Should().Be(key);
    }

    [Fact]
    public void GetAPIKey_RoundTripsThroughManualXorDecode()
    {
        // Independently reproduce the decode (XOR every byte with the static key, then UTF8)
        // to pin the exact algorithm, not just "some non-empty string".
        var uninit = Reflect.Uninitialized<FaceFinderClient>();
        var obfuscated = Reflect.GetField<byte[]>(uninit, "_obfuscatedBytes");
        var xorKey = Reflect.GetField<byte>(uninit, "_xorKey");

        var decoded = new byte[obfuscated.Length];
        for (int i = 0; i < obfuscated.Length; i++)
            decoded[i] = (byte)(obfuscated[i] ^ xorKey);

        CallGetApiKey().Should().Be(Encoding.UTF8.GetString(decoded));
    }

    // ── MetadataFileExtension (public const) ───────────────────────────────────

    [Fact]
    public void MetadataFileExtension_IsExactSuffix()
    {
        FaceFinderClient.MetadataFileExtension.Should().Be(".ffmeta.json");
    }

    // ── FaceFinderMetadata DTO (JSON round-trip via production serializer) ──────

    [Fact]
    public void FaceFinderMetadata_DefaultSource_IsFaceFinder()
    {
        new FaceFinderClient.FaceFinderMetadata().Source.Should().Be("FaceFinder");
    }

    [Fact]
    public void FaceFinderMetadata_RoundTripsThroughNewtonsoft()
    {
        var ts = new DateTime(2024, 6, 1, 12, 30, 0, DateTimeKind.Utc);
        var original = new FaceFinderClient.FaceFinderMetadata
        {
            UpdatedAt = ts,
            ExternalUrl = "https://nexus.example/mods/42",
        };
        original.Source.Should().Be("FaceFinder"); // unchanged default

        var json = JsonConvert.SerializeObject(original);
        var restored = JsonConvert.DeserializeObject<FaceFinderClient.FaceFinderMetadata>(json)!;

        restored.Source.Should().Be("FaceFinder");
        restored.UpdatedAt.Should().Be(ts);
        restored.ExternalUrl.Should().Be("https://nexus.example/mods/42");
    }

    [Fact]
    public void FaceFinderMetadata_MissingSourceInJson_DefaultsToFaceFinder()
    {
        // A sidecar that predates / omits the Source field must deserialize to the default,
        // because IsCacheStaleAsync gates on Source == "FaceFinder".
        const string json = "{ \"UpdatedAt\": \"2023-01-02T03:04:05Z\" }";
        var restored = JsonConvert.DeserializeObject<FaceFinderClient.FaceFinderMetadata>(json)!;

        restored.Source.Should().Be("FaceFinder");
        restored.ExternalUrl.Should().BeNull();
    }

    [Fact]
    public void FaceFinderMetadata_ExplicitNonFaceFinderSource_IsPreserved()
    {
        const string json = "{ \"Source\": \"ManualImport\", \"UpdatedAt\": \"2023-01-02T03:04:05Z\" }";
        var restored = JsonConvert.DeserializeObject<FaceFinderClient.FaceFinderMetadata>(json)!;

        restored.Source.Should().Be("ManualImport");
    }

    [Fact]
    public void FaceFinderMetadata_NullExternalUrl_RoundTrips()
    {
        var meta = new FaceFinderClient.FaceFinderMetadata { UpdatedAt = DateTime.UnixEpoch };
        var restored = JsonConvert.DeserializeObject<FaceFinderClient.FaceFinderMetadata>(
            JsonConvert.SerializeObject(meta))!;

        restored.ExternalUrl.Should().BeNull();
        restored.UpdatedAt.Should().Be(DateTime.UnixEpoch);
    }

    // ── WriteMetadataAsync / ReadMetadataAsync sidecar round-trip ───────────────

    [Fact]
    public async Task WriteMetadataAsync_WritesSidecarAtImagePathPlusExtension()
    {
        using var tmp = new TempDir();
        var settings = new Settings();
        var client = MakeClient(settings, tmp.Path);

        var imagePath = Path.Combine(tmp.Path, "cache", "Whiterun", "Lydia.png");
        Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
        var result = new FaceFinderResult
        {
            ImageUrl = "https://npcfacefinder.com/i/lydia.png",
            UpdatedAt = new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc),
            ExternalUrl = "https://nexus.example/mods/7",
        };

        await client.WriteMetadataAsync(imagePath, result);

        var sidecar = imagePath + FaceFinderClient.MetadataFileExtension;
        File.Exists(sidecar).Should().BeTrue();
    }

    [Fact]
    public async Task WriteMetadataAsync_RegistersPathInSettingsCache()
    {
        using var tmp = new TempDir();
        var settings = new Settings();
        var client = MakeClient(settings, tmp.Path);

        var imagePath = tmp.WriteText("portrait.png", "fake");
        var result = new FaceFinderResult
        {
            ImageUrl = "https://npcfacefinder.com/i/x.png",
            UpdatedAt = DateTime.UtcNow,
        };

        await client.WriteMetadataAsync(imagePath, result);

        settings.CachedFaceFinderPaths.Should().Contain(imagePath);
    }

    [Fact]
    public async Task WriteThenRead_RoundTripsUpdatedAtAndExternalUrl()
    {
        using var tmp = new TempDir();
        var settings = new Settings();
        var client = MakeClient(settings, tmp.Path);

        var imagePath = tmp.WriteText("face.png", "fake");
        var ts = new DateTime(2022, 11, 12, 13, 14, 15, DateTimeKind.Utc);
        var result = new FaceFinderResult
        {
            ImageUrl = "https://npcfacefinder.com/i/face.png",
            UpdatedAt = ts,
            ExternalUrl = "https://nexus.example/mods/99",
        };

        await client.WriteMetadataAsync(imagePath, result);
        var readBack = await client.ReadMetadataAsync(imagePath);

        readBack.Should().NotBeNull();
        readBack!.Source.Should().Be("FaceFinder"); // writer never sets it -> serialized default
        readBack.UpdatedAt.Should().Be(ts);
        readBack.ExternalUrl.Should().Be("https://nexus.example/mods/99");
    }

    [Fact]
    public async Task WriteMetadataAsync_NullExternalUrl_RoundTripsAsNull()
    {
        using var tmp = new TempDir();
        var settings = new Settings();
        var client = MakeClient(settings, tmp.Path);

        var imagePath = tmp.WriteText("face2.png", "fake");
        var result = new FaceFinderResult
        {
            ImageUrl = "https://npcfacefinder.com/i/face2.png",
            UpdatedAt = DateTime.UnixEpoch,
            ExternalUrl = null,
        };

        await client.WriteMetadataAsync(imagePath, result);
        var readBack = await client.ReadMetadataAsync(imagePath);

        readBack.Should().NotBeNull();
        readBack!.ExternalUrl.Should().BeNull();
        readBack.UpdatedAt.Should().Be(DateTime.UnixEpoch);
    }

    [Fact]
    public async Task ReadMetadataAsync_NoSidecar_ReturnsNull()
    {
        using var tmp = new TempDir();
        var settings = new Settings();
        var client = MakeClient(settings, tmp.Path);

        var imagePath = Path.Combine(tmp.Path, "never-written.png");
        (await client.ReadMetadataAsync(imagePath)).Should().BeNull();
    }

    [Fact]
    public async Task ReadMetadataAsync_CorruptSidecar_ReturnsNull()
    {
        using var tmp = new TempDir();
        var settings = new Settings();
        var client = MakeClient(settings, tmp.Path);

        var imagePath = Path.Combine(tmp.Path, "broken.png");
        File.WriteAllText(imagePath + FaceFinderClient.MetadataFileExtension, "{ this is not json");

        // Deserialization throws internally; the method swallows it and returns null.
        (await client.ReadMetadataAsync(imagePath)).Should().BeNull();
    }

    [Fact]
    public async Task ReadMetadataAsync_ManuallyAuthoredSidecar_DeserializesFields()
    {
        using var tmp = new TempDir();
        var settings = new Settings();
        var client = MakeClient(settings, tmp.Path);

        var imagePath = Path.Combine(tmp.Path, "manual.png");
        File.WriteAllText(imagePath + FaceFinderClient.MetadataFileExtension,
            "{ \"Source\": \"FaceFinder\", \"UpdatedAt\": \"2020-05-06T07:08:09Z\", " +
            "\"ExternalUrl\": \"https://nexus.example/mods/3\" }");

        var meta = await client.ReadMetadataAsync(imagePath);

        meta.Should().NotBeNull();
        meta!.Source.Should().Be("FaceFinder");
        // Compare the UTC instant so the assertion is independent of Newtonsoft's
        // DateTimeZoneHandling (the "Z" input represents a fixed point in time).
        meta.UpdatedAt.ToUniversalTime().Should().Be(new DateTime(2020, 5, 6, 7, 8, 9, DateTimeKind.Utc));
        meta.ExternalUrl.Should().Be("https://nexus.example/mods/3");
    }

    // ── IsCacheStaleAsync — pre-network early returns only ──────────────────────

    [Fact]
    public async Task IsCacheStaleAsync_MissingSidecar_ReturnsFalse()
    {
        // CASE 1: image present, no sidecar => manually downloaded => never stale.
        using var tmp = new TempDir();
        var settings = new Settings();
        var client = MakeClient(settings, tmp.Path);

        var imagePath = tmp.WriteText("manual-image.png", "fake");
        var npc = FormKey.Factory("01A696:Skyrim.esm");

        (await client.IsCacheStaleAsync(imagePath, npc, "SomeMod")).Should().BeFalse();
    }

    [Fact]
    public async Task IsCacheStaleAsync_NonFaceFinderSource_ReturnsFalse()
    {
        // Sidecar exists but Source != "FaceFinder" => treated as a manual file => not stale.
        // This early-returns before any API call, so it is offline-deterministic.
        using var tmp = new TempDir();
        var settings = new Settings();
        var client = MakeClient(settings, tmp.Path);

        var imagePath = Path.Combine(tmp.Path, "external.png");
        File.WriteAllText(imagePath + FaceFinderClient.MetadataFileExtension,
            "{ \"Source\": \"ManualImport\", \"UpdatedAt\": \"2024-01-01T00:00:00Z\" }");
        var npc = FormKey.Factory("000801:Skyrim.esm");

        (await client.IsCacheStaleAsync(imagePath, npc, "SomeMod")).Should().BeFalse();
    }

    [Fact]
    public async Task IsCacheStaleAsync_CorruptSidecar_ReturnsTrue()
    {
        // A sidecar that fails to parse is treated as corrupt; the catch returns true so the
        // system can re-download a valid copy. This throws during deserialization, before any
        // network access, so it is offline-deterministic.
        using var tmp = new TempDir();
        var settings = new Settings();
        var client = MakeClient(settings, tmp.Path);

        var imagePath = Path.Combine(tmp.Path, "corrupt.png");
        File.WriteAllText(imagePath + FaceFinderClient.MetadataFileExtension, "{ not valid json ]");
        var npc = FormKey.Factory("000801:Skyrim.esm");

        (await client.IsCacheStaleAsync(imagePath, npc, "SomeMod")).Should().BeTrue();
    }
}
