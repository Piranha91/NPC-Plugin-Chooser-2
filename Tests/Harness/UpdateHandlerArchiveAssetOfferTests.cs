using System.IO;
using FluentAssertions;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using Xunit;
using Outcome = NPC_Plugin_Chooser_2.BackEnd.UpdateHandler.ArchiveAssetOfferOutcome;

namespace NPC_Plugin_Chooser_2.Tests.Harness;

/// <summary>
/// The 2.2.6 prompt that moves internal-renderer users off BSA extraction
/// (<see cref="UpdateHandler.OfferInMemoryArchiveAssets"/>), run against temp cache folders with
/// a scripted answer instead of the dialog.
/// </summary>
public class UpdateHandlerArchiveAssetOfferTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "npc2-archive-offer-" + Guid.NewGuid().ToString("N"));
    private readonly string _cache;
    private readonly string _legacyCache;

    public UpdateHandlerArchiveAssetOfferTests()
    {
        _cache = Path.Combine(_root, "SkyRenderCache");
        _legacyCache = Path.Combine(_root, "CharacterViewerCache");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private static Settings InternalRendererUser() => new()
    {
        UsePortraitCreatorFallback = true,
        SelectedRenderer = MugshotRenderer.Internal,
    };

    private void AddFile(string folder, string relative, int length)
    {
        string path = Path.Combine(folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[length]);
    }

    private Task<Outcome> Offer(Settings settings, Func<string, bool> confirm) =>
        UpdateHandler.OfferInMemoryArchiveAssets(settings, new[] { _cache, _legacyCache }, confirm);

    [Fact]
    public async Task Accepting_DeletesBothCachesAndKeepsMemoryMode()
    {
        AddFile(_cache, @"1a2b3c4d\meshes\head.nif", 3 * 1024 * 1024);
        AddFile(_cache, @"1a2b3c4d\textures\skin.dds", 1024 * 1024);
        AddFile(_legacyCache, @"old\textures\eye.dds", 512);
        var settings = InternalRendererUser();
        string? shown = null;

        var outcome = await Offer(settings, m => { shown = m; return true; });

        outcome.Should().Be(Outcome.Accepted);
        Directory.Exists(_cache).Should().BeFalse();
        Directory.Exists(_legacyCache).Should().BeFalse();
        settings.InternalMugshot.ServeArchiveAssetsFromMemory.Should().BeTrue();
        shown.Should().Contain("3 extracted files using 4 MB").And.Contain(_cache).And.Contain(_legacyCache);
    }

    [Fact]
    public async Task Declining_KeepsFilesAndSwitchesBackToExtraction()
    {
        AddFile(_cache, @"1a2b3c4d\meshes\head.nif", 100);
        var settings = InternalRendererUser();

        var outcome = await Offer(settings, _ => false);

        outcome.Should().Be(Outcome.Declined);
        File.Exists(Path.Combine(_cache, @"1a2b3c4d\meshes\head.nif")).Should().BeTrue();
        settings.InternalMugshot.ServeArchiveAssetsFromMemory.Should().BeFalse();

        // Answered: a later launch on the same version doesn't ask again.
        (await Offer(settings, _ => throw new InvalidOperationException("asked twice")))
            .Should().Be(Outcome.NotApplicable);
    }

    [Fact]
    public async Task EmptyOrMissingCaches_SwitchSilently()
    {
        Directory.CreateDirectory(Path.Combine(_cache, "1a2b3c4d", "meshes")); // folders, no files
        var settings = InternalRendererUser();

        var outcome = await Offer(settings, _ => throw new InvalidOperationException("should not ask"));

        outcome.Should().Be(Outcome.NothingToDelete);
        settings.InternalMugshot.ServeArchiveAssetsFromMemory.Should().BeTrue();
    }

    [Theory]
    [InlineData(false, MugshotRenderer.Internal)]
    [InlineData(true, MugshotRenderer.LegacyPortraitCreator)]
    public async Task UsersWithACacheAreAsked_WhicheverMugshotRendererTheyUse(bool generateMugshots, MugshotRenderer renderer)
    {
        // The 3D preview fills the same cache, so a user who doesn't generate internal-renderer
        // mugshots can still have one; 2.2.5 users have only the old CharacterViewerCache.
        AddFile(_legacyCache, @"1a2b3c4d\meshes\head.nif", 100);
        var settings = new Settings { UsePortraitCreatorFallback = generateMugshots, SelectedRenderer = renderer };
        bool asked = false;

        var outcome = await Offer(settings, _ => asked = true);

        asked.Should().BeTrue();
        outcome.Should().Be(Outcome.Accepted);
        Directory.Exists(_legacyCache).Should().BeFalse();
    }
}
