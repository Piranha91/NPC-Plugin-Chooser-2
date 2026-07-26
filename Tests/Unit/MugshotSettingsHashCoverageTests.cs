using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CharacterViewer.Rendering;
using FluentAssertions;
using NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost;
using NPC_Plugin_Chooser_2.Models;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// Guards the rule that anything changing a mugshot's PIXELS must drift
/// <see cref="InternalMugshotMetadata.ComputeSettingsHash"/>, so the staleness
/// checker re-renders it. The counterexample this file exists for: the hash
/// carried the selected lighting preset's NAME but not its CONTENTS, so saving
/// over a user preset (the editor offers to overwrite in place) relit every
/// render without staling a single tile.
///
/// <para>Settings that deliberately do NOT hash are listed in
/// <see cref="NonPixelFields"/> with the reason — the reflection test fails when
/// a new field appears in neither camp, forcing a decision at review time
/// instead of a silent miss.</para>
/// </summary>
public class MugshotSettingsHashCoverageTests
{
    /// <summary>Fields that cannot change rendered pixels, so their absence from
    /// the hash is correct. Anything not here must be pixel-affecting AND
    /// hashed.</summary>
    private static readonly Dictionary<string, string> NonPixelFields = new()
    {
        ["VerboseLog"] = "diagnostic logging only",
        ["LogRenderLogic"] = "diagnostic logging only",
        ["CacheMode"] = "decode-cache budget; performance only",
        ["CacheFixedBudgetGB"] = "decode-cache budget; performance only",
        ["CacheFreeRamPercent"] = "decode-cache budget; performance only",
        ["ShowMissingNpcAssetsIcon"] = "tile overlay, not render output; MugshotStalenessChecker " +
                                       "has a dedicated stamp-present-while-toggle-off gate",
        ["ShowMissingOutfitAssetsIcon"] = "tile overlay, not render output; same dedicated gate",
        ["WigModeOverride"] = "harness-only; reaches the cache through the wig identity suffix " +
                              "(GetEffectiveRenderWigMode), not the settings hash",
        ["AntlerModeOverride"] = "harness-only; same route via GetEffectiveRenderAntlerMode",
        ["UserLightingLayouts"] = "the library; only the SELECTED preset's resolved contents hash (v18)",
        ["UserLightingColorSchemes"] = "the library; only the SELECTED scheme's resolved contents hash (v18)",
        ["EmulateRaceMenuHairSlotTint"] = "retired from the global hash at v19; repaints only worn " +
                                          "hair-slot wigs, so it lives on the per-tile wig identity " +
                                          "suffix (+wigtint) instead",
        ["IncludeDefaultOutfit"] = "hashed via the effective-attire parameter (per-NPC override aware)",
        ["IncludeHeadgear"] = "hashed via the effective-attire parameter (per-NPC override aware)",
    };

    /// <summary>Every settable property must either hash or be justified above.
    /// Detection is behavioural: mutate the property away from its default and
    /// require the hash to move.</summary>
    [Fact]
    public void EveryPixelAffectingSetting_DriftsTheHash()
    {
        var unaccounted = new List<string>();
        int examined = 0;

        foreach (var prop in typeof(InternalMugshotSettings).GetProperties(
                     BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanWrite || !prop.CanRead) continue;
            if (NonPixelFields.ContainsKey(prop.Name)) continue;

            var cfg = new InternalMugshotSettings();
            string before = HashWorstCase(cfg);
            if (!TryMutate(prop, cfg)) continue;   // no meaningful alternate value
            examined++;
            string after = HashWorstCase(cfg);

            if (string.Equals(before, after, StringComparison.OrdinalIgnoreCase))
                unaccounted.Add(prop.Name);
        }

        // Guards against the sweep silently mutating nothing (a TryMutate gap or
        // a reflection change) and passing vacuously.
        examined.Should().BeGreaterThan(30, "the settings class carries dozens of pixel-affecting knobs");

        unaccounted.Should().BeEmpty(
            "these settings change the render but not the hash, so tiles would never go stale — " +
            "add them to ComputeSettingsHashAtSchema at a new schema version, or document them " +
            "in NonPixelFields with the reason they cannot affect pixels");
    }

    /// <summary>
    /// The RaceMenu tint emulation only repaints a WORN hair-slot wig, so from
    /// schema 19 it is no longer in the global settings hash at all — hashing it
    /// for everyone meant one toggle flip re-rendered the entire library to
    /// reproduce identical pixels for every NPC without a wig. It lives on the
    /// per-tile wig identity suffix instead (see
    /// <c>OutfitDisplayResolver.ComputeWigIdentitySuffix</c>), recomputed live so
    /// a mod that later adds a wig still re-stales its tiles.
    /// </summary>
    [Fact]
    public void RaceMenuTintEmulation_IsNotInTheGlobalHashFromV19()
    {
        var on = new InternalMugshotSettings { EmulateRaceMenuHairSlotTint = true };
        var off = new InternalMugshotSettings { EmulateRaceMenuHairSlotTint = false };

        InternalMugshotMetadata.ComputeSettingsHashAtSchema(on, 19)
            .Should().Be(InternalMugshotMetadata.ComputeSettingsHashAtSchema(off, 19));
    }

    /// <summary>Tiles stamped 16-18 hashed the emulation directly; they must
    /// still reproduce their stamped hash, or retiring the entries would itself
    /// re-stale the library the change was meant to spare.</summary>
    [Theory]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    public void PreV19Tiles_KeepTheEmulationEntries(int schema)
    {
        var on = new InternalMugshotSettings { EmulateRaceMenuHairSlotTint = true };
        var off = new InternalMugshotSettings { EmulateRaceMenuHairSlotTint = false };

        InternalMugshotMetadata.ComputeSettingsHashAtSchema(on, schema)
            .Should().NotBe(InternalMugshotMetadata.ComputeSettingsHashAtSchema(off, schema));
    }

    /// <summary>Proves the sweep above can actually fail: a genuinely unhashed
    /// property must be reported as not drifting, and a hashed one must drift.
    /// Without this the sweep could pass because TryMutate quietly no-ops.</summary>
    [Fact]
    public void TheSweepDetectsBothOutcomes()
    {
        var unhashed = new InternalMugshotSettings();
        string beforeUnhashed = InternalMugshotMetadata.ComputeSettingsHash(unhashed);
        unhashed.VerboseLog = !unhashed.VerboseLog;
        InternalMugshotMetadata.ComputeSettingsHash(unhashed).Should().Be(beforeUnhashed,
            "VerboseLog is deliberately excluded — if this drifts, the exclusion list is wrong");

        var hashed = new InternalMugshotSettings();
        string beforeHashed = InternalMugshotMetadata.ComputeSettingsHash(hashed);
        hashed.Exposure += 0.25f;
        InternalMugshotMetadata.ComputeSettingsHash(hashed).Should().NotBe(beforeHashed,
            "Exposure is hashed at v10 — if this does not drift, the sweep cannot detect anything");
    }

    /// <summary>Only the SELECTED preset's contents may matter — editing an
    /// unrelated saved preset must not re-stale the whole library.</summary>
    [Fact]
    public void EditingTheSelectedLightingLayout_DriftsTheHash()
    {
        var cfg = new InternalMugshotSettings { LightingLayoutName = "MyRig" };
        var mine = new CharacterViewerLightingLayout { Name = "MyRig", KeyIntensity = 1.0, Ambient = 0.2 };
        var other = new CharacterViewerLightingLayout { Name = "Unrelated", KeyIntensity = 1.0 };
        cfg.UserLightingLayouts.Add(mine);
        cfg.UserLightingLayouts.Add(other);

        string before = InternalMugshotMetadata.ComputeSettingsHash(cfg);

        other.KeyIntensity = 9.0;   // a preset the mugshot doesn't use
        InternalMugshotMetadata.ComputeSettingsHash(cfg).Should().Be(before,
            "editing an unselected preset changes nothing about this render");

        mine.KeyIntensity = 9.0;    // the one it does
        InternalMugshotMetadata.ComputeSettingsHash(cfg).Should().NotBe(before,
            "saving over the selected preset relights every render");
    }

    [Fact]
    public void EditingTheSelectedColorScheme_DriftsTheHash()
    {
        var cfg = new InternalMugshotSettings { LightingColorSchemeName = "Warm" };
        var scheme = new CharacterViewerLightingColorScheme { Name = "Warm", KeyR = 1f, KeyG = 0.9f, KeyB = 0.8f };
        cfg.UserLightingColorSchemes.Add(scheme);

        string before = InternalMugshotMetadata.ComputeSettingsHash(cfg);
        scheme.KeyB = 0.2f;

        InternalMugshotMetadata.ComputeSettingsHash(cfg).Should().NotBe(before);
    }

    /// <summary>Deleting the selected preset silently falls back to the default
    /// rig — a full relight under an unchanged name.</summary>
    [Fact]
    public void DeletingTheSelectedLightingLayout_DriftsTheHash()
    {
        var cfg = new InternalMugshotSettings { LightingLayoutName = "MyRig" };
        cfg.UserLightingLayouts.Add(new CharacterViewerLightingLayout
        {
            Name = "MyRig", Ambient = 0.9, KeyIntensity = 4.2, RimElevation = 33,
        });

        string before = InternalMugshotMetadata.ComputeSettingsHash(cfg);
        cfg.UserLightingLayouts.Clear();

        InternalMugshotMetadata.ComputeSettingsHash(cfg).Should().NotBe(before);
    }

    /// <summary>Older tiles are compared at their own stamped schema, so a v17
    /// PNG must not be re-staled just because v18 added entries.</summary>
    [Fact]
    public void LightingContents_AreExcludedBelowSchema18()
    {
        var cfg = new InternalMugshotSettings { LightingLayoutName = "MyRig" };
        var mine = new CharacterViewerLightingLayout { Name = "MyRig", KeyIntensity = 1.0 };
        cfg.UserLightingLayouts.Add(mine);

        string before = InternalMugshotMetadata.ComputeSettingsHashAtSchema(cfg, 17);
        mine.KeyIntensity = 9.0;

        InternalMugshotMetadata.ComputeSettingsHashAtSchema(cfg, 17).Should().Be(before);
    }

    private static string HashWorstCase(InternalMugshotSettings cfg) =>
        InternalMugshotMetadata.ComputeSettingsHash(cfg);

    /// <summary>Moves a property off its default so the hash has something to
    /// notice. Returns false for types with no meaningful alternate value.</summary>
    private static bool TryMutate(PropertyInfo prop, InternalMugshotSettings cfg)
    {
        object? current = prop.GetValue(cfg);
        Type t = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;

        object? mutated;
        if (t == typeof(bool)) mutated = !(bool)(current ?? false);
        else if (t == typeof(float)) mutated = (float)(current ?? 0f) + 7.5f;
        else if (t == typeof(double)) mutated = (double)(current ?? 0d) + 7.5;
        else if (t == typeof(int)) mutated = (int)(current ?? 0) + 7;
        else if (t == typeof(byte)) mutated = (byte)(((byte)(current ?? (byte)0) + 7) % 256);
        else if (t == typeof(string)) mutated = (current as string) + "-mutated";
        else if (t.IsEnum)
        {
            var values = Enum.GetValues(t).Cast<object>().ToList();
            mutated = values.FirstOrDefault(v => !Equals(v, current));
            if (mutated == null) return false;
        }
        else return false;   // collections and reference types: covered explicitly above

        prop.SetValue(cfg, mutated);
        return true;
    }
}
