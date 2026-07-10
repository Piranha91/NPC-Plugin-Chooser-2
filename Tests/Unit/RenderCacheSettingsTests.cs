using CharacterViewer.Rendering;
using FluentAssertions;
using NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost.Adapters;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.View_Models;
using Newtonsoft.Json;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// Locks the plumbing of the renderer decode-cache mode setting: its default, the GB→bytes conversion the
/// adapter exposes to <see cref="ICharacterViewerSettings"/>, model write-through, and JSON persistence.
/// Pure — no game, no UI thread.
/// </summary>
public class RenderCacheSettingsTests
{
    private static NpcChooserSettingsAdapter MakeAdapter(Settings s) =>
        new(s, new System.Lazy<VM_Settings>(() => null!));

    [Fact]
    public void Default_Is_PercentFreeRam()
    {
        new Settings().InternalMugshot.CacheMode.Should().Be(RenderCacheMode.PercentFreeRam);
        MakeAdapter(new Settings()).CacheMode.Should().Be(RenderCacheMode.PercentFreeRam);
    }

    [Fact]
    public void FixedCacheBudgetBytes_ConvertsGbToBytes()
    {
        var s = new Settings();
        s.InternalMugshot.CacheFixedBudgetGB = 2.0;
        MakeAdapter(s).FixedCacheBudgetBytes.Should().Be(2L * 1024 * 1024 * 1024);
    }

    [Fact]
    public void FixedCacheBudgetBytes_ClampsNegativeToZero()
    {
        var s = new Settings();
        s.InternalMugshot.CacheFixedBudgetGB = -5;
        MakeAdapter(s).FixedCacheBudgetBytes.Should().Be(0);
    }

    [Fact]
    public void SettingCacheMode_WritesThroughToModel()
    {
        var s = new Settings();
        var adapter = MakeAdapter(s);
        adapter.CacheMode = RenderCacheMode.Disabled;
        s.InternalMugshot.CacheMode.Should().Be(RenderCacheMode.Disabled);
    }

    [Fact]
    public void CacheSettings_RoundTripThroughJson()
    {
        // Round-trip the InternalMugshot sub-object (no FormKey fields, so no TypeConverter setup needed).
        var block = new InternalMugshotSettings
        {
            CacheMode = RenderCacheMode.FixedRam,
            CacheFixedBudgetGB = 3.5,
        };

        var json = JsonConvert.SerializeObject(block);
        var back = JsonConvert.DeserializeObject<InternalMugshotSettings>(json)!;

        back.CacheMode.Should().Be(RenderCacheMode.FixedRam);
        back.CacheFixedBudgetGB.Should().Be(3.5);
    }

    [Fact]
    public void MissingCacheFields_DeserializeToDefaults()
    {
        // An older Settings.json (written before these fields existed) has no CacheMode / CacheFixedBudgetGB.
        var legacyJson = "{ \"InternalMugshot\": { \"OutputWidth\": 750 } }";
        var back = JsonConvert.DeserializeObject<Settings>(legacyJson)!;
        back.InternalMugshot.CacheMode.Should().Be(RenderCacheMode.PercentFreeRam);
        back.InternalMugshot.CacheFixedBudgetGB.Should().Be(4.0);
    }
}
