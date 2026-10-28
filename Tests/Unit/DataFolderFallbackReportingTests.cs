using System;
using System.Collections.Generic;
using System.IO;
using SkyRender;
using SkyRender.Character;
using FluentAssertions;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// Pins the resolver-side contract behind the data-folder-asset badge
/// (CharacterViewer.Rendering 2.9.0):
///
/// <list type="bullet">
/// <item>An engine-order Tier 2 (data-folder loose) hit carries
/// <see cref="AssetSource.ViaDataFolderFallback"/> and reports its game path
/// to the pushed sink; a mod-scope (Tier 1) hit does neither.</item>
/// <item>Warm resolves (scoped-resolve-cache hits) report the same paths as
/// the cold resolve that populated the cache.</item>
/// <item><see cref="GameAssetResolver.PushDataFolderFallbackReportSuppression"/>
/// mutes REPORTING (never the flag) for the current flow — the
/// referencer-scoping rule that keeps a fallback-resolved baseline NIF's
/// internal textures (femalebody_etc_v2_*) out of the badge while the NIF
/// itself, and anything referenced by the mod's own in-scope NIFs, still
/// reports.</item>
/// </list>
/// </summary>
public class DataFolderFallbackReportingTests : IDisposable
{
    private readonly string _root;
    private readonly string _dataDir;
    private readonly string _modDir;
    private readonly GameAssetResolver _resolver;

    private const string DepPath = @"textures\dep.dds";     // only in data folder
    private const string OwnPath = @"textures\own.dds";     // only in mod folder

    public DataFolderFallbackReportingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "NPC2Tests_DataFolderFallback_" + Guid.NewGuid().ToString("N"));
        _dataDir = Path.Combine(_root, "Data");
        _modDir = Path.Combine(_root, "Mod");
        Directory.CreateDirectory(Path.Combine(_dataDir, "textures"));
        Directory.CreateDirectory(Path.Combine(_modDir, "textures"));
        File.WriteAllBytes(Path.Combine(_dataDir, DepPath), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(_modDir, OwnPath), new byte[] { 1 });

        _resolver = new GameAssetResolver(
            new FakeDataFolder(_dataDir), new FakeBsaProvider(),
            new RenderLogGate(), new FakeLogger());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    /// <summary>Scope chain shaped like a mugshot render's: vanilla (data
    /// folder) at index 0, the mod's folder above it.</summary>
    private IReadOnlyList<RenderScope> Scopes() => new List<RenderScope>
    {
        new(_dataDir, Array.Empty<string>()),
        new(_modDir, Array.Empty<string>()),
    };

    private IDisposable PushRenderScopes() => _resolver.PushScopes(
        Scopes(), folders: null,
        vanillaLooseOverridesBsa: true, vanillaLooseOverridesModLoose: false,
        allowLoadOrderFallback: true);

    [Fact]
    public void Tier2Hit_IsFlagged_AndReported()
    {
        using var scopes = PushRenderScopes();
        var reported = new List<string>();
        using var sink = _resolver.PushDataFolderFallbackSink(reported.Add);

        var source = _resolver.ResolveAssetSource(DepPath);

        source.Kind.Should().Be(AssetOriginKind.Loose);
        source.ViaDataFolderFallback.Should().BeTrue();
        reported.Should().ContainSingle().Which.Should().Be(DepPath);
    }

    [Fact]
    public void ModScopeHit_IsNotFlagged_AndNotReported()
    {
        using var scopes = PushRenderScopes();
        var reported = new List<string>();
        using var sink = _resolver.PushDataFolderFallbackSink(reported.Add);

        var source = _resolver.ResolveAssetSource(OwnPath);

        source.Kind.Should().Be(AssetOriginKind.Loose);
        source.ViaDataFolderFallback.Should().BeFalse();
        reported.Should().BeEmpty();
    }

    [Fact]
    public void WarmCacheHit_ReportsAgain()
    {
        using var scopes = PushRenderScopes();
        _resolver.ResolveAssetSource(DepPath); // cold — populates the scoped resolve cache

        var reported = new List<string>();
        using var sink = _resolver.PushDataFolderFallbackSink(reported.Add);
        _resolver.ResolveAssetSource(DepPath); // warm

        reported.Should().ContainSingle().Which.Should().Be(DepPath);
    }

    [Fact]
    public void Suppression_MutesReporting_ButKeepsTheFlag()
    {
        using var scopes = PushRenderScopes();
        var reported = new List<string>();
        using var sink = _resolver.PushDataFolderFallbackSink(reported.Add);

        AssetSource suppressed;
        using (_resolver.PushDataFolderFallbackReportSuppression())
        {
            suppressed = _resolver.ResolveAssetSource(DepPath);
        }

        suppressed.ViaDataFolderFallback.Should().BeTrue(
            "suppression mutes reporting, never the AssetSource flag");
        reported.Should().BeEmpty();

        // Reporting resumes once the token is disposed — same path, warm hit.
        _resolver.ResolveAssetSource(DepPath);
        reported.Should().ContainSingle().Which.Should().Be(DepPath);
    }

    // --- minimal fakes -------------------------------------------------------

    private sealed class FakeDataFolder : IDataFolderProvider
    {
        public FakeDataFolder(string path) { DataFolderPath = path; }
        public string DataFolderPath { get; }
        public object? CurrentLoadOrderToken => null;
    }

    private sealed class FakeBsaProvider : IBsaArchiveProvider
    {
        public void EnsureAllArchivesOpened() { }
        public bool TryLocateInBsa(string subpath, out string? containingBsaPath)
        { containingBsaPath = null; return false; }
        public bool TryLocateInScopedBsa(string subpath, string folderPath,
            IReadOnlyList<string> modKeyFileNames, out string? containingBsaPath)
        { containingBsaPath = null; return false; }
        public bool TryExtractToDisk(string containingBsaPath, string subpath, string destPath, out string? error)
        { error = "fake"; return false; }
    }

    private sealed class FakeLogger : IRenderLogger
    {
        public void LogMessage(string message) { }
        public void LogError(string message) { }
        public void LogError(string message, Exception ex) { }
    }
}
