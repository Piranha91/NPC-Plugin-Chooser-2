using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.BackEnd.OutfitDistribution;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.Integration;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// <see cref="ForwardedOutfitDistributor"/> — republishes a forwarded wig/antler outfit
/// through whichever runtime distributor contests the NPC's outfit slot.
///
/// Everything here is file/queue behavior, so no game environment is needed: the
/// SkyPatcher side is observed through a real <see cref="SkyPatcherInterface"/> whose ini
/// is written to a <see cref="TempDir"/>, and the SPID side through the generated
/// *_DISTR.ini. The ordering assertions encode the two tools' verified selection rules —
/// SkyPatcher applies the LAST matching assignment, SPID the FIRST — which is what makes
/// the file names load-bearing.
/// </summary>
public class ForwardedOutfitDistributorTests
{
    private static readonly ModKey OutputKey = ModKey.FromNameAndExtension("NPCTest.esp");
    private static readonly FormKey Npc = FormKey.Factory("013295:Skyrim.esm");        // Ataf
    private static readonly FormKey OutfitDup = FormKey.Factory("000801:NPCTest.esp");

    private static RuntimeOutfitContest SkyContest(bool outranked = false) => new()
    {
        SkyPatcher = true,
        SkyPatcherDetail = "Skyrim_Equipments_Distribution_F.ini (line 3)",
        SkyPatcherOutranksNpc2Ini = outranked,
        WinningSourceDetail = "SkyPatcher: Skyrim_Equipments_Distribution_F.ini (line 3)",
    };

    private static RuntimeOutfitContest SpidContest(string file = "kco_brd_DISTR.ini") => new()
    {
        Spid = true,
        SpidDetail = file + " (line 7)",
        SpidSourceFile = file,
        WinningSourceDetail = "SPID: " + file + " (line 7)",
    };

    private sealed class Harness
    {
        public Settings Settings = null!;
        public SkyPatcherInterface SkyPatcher = null!;
        public ForwardedOutfitDistributor Distributor = null!;
        public List<(string Message, bool IsError)> Log = new();
    }

    private static Harness Make(bool skyPatcherMode = false, bool publish = true)
    {
        var env = NpcChooserTestEnvironment.Invalid();
        env.OutputMod = new SkyrimMod(OutputKey, SkyrimRelease.SkyrimSE);

        var h = new Harness
        {
            Settings = new Settings
            {
                UseSkyPatcherMode = skyPatcherMode,
                PublishForwardedOutfitsToDistributors = publish,
            },
        };
        h.SkyPatcher = new SkyPatcherInterface(env);
        h.Distributor = new ForwardedOutfitDistributor(h.Settings, h.SkyPatcher);
        h.Distributor.ConnectToUILogger((m, e, _) => h.Log.Add((m, e)), null, null, null);
        h.SkyPatcher.ConnectToUILogger((m, e, _) => h.Log.Add((m, e)), null, null, null);
        return h;
    }

    private static string SkyIniPath(string root) =>
        Path.Combine(root, "SKSE", "Plugins", "SkyPatcher", "npc", "NPC Plugin Chooser", "NPCTest.ini");

    // ── SPID form-reference formatting (pure static) ──────────────────────────

    [Fact]
    public void FormatFormKeyForSpid_UsesTildeSyntax_AndStripsLeadingZeros()
    {
        // "0x00013295" would be rewritten by SPID's own sanitizer into a load-order
        // relative runtime FormID, which is exactly what we must not emit.
        ForwardedOutfitDistributor.FormatFormKeyForSpid(Npc).Should().Be("0x13295~Skyrim.esm");
        ForwardedOutfitDistributor.FormatFormKeyForSpid(OutfitDup).Should().Be("0x801~NPCTest.esp");
    }

    // ── Generated SPID file name ──────────────────────────────────────────────

    [Fact]
    public void SpidFileName_SortsBeforeOrdinaryConfigs()
    {
        var ours = ForwardedOutfitDistributor.BuildSpidFileName(OutputKey);
        ours.Should().EndWith("_DISTR.ini", "SPID only reads Data\\*.ini whose name contains _DISTR");

        // SPID applies the FIRST matching outfit entry in ordinal path order, so the
        // generated file has to sort ahead of the configs it is competing with.
        foreach (var competitor in new[]
                 {
                     "kco_brd_DISTR.ini", "Skyrim_DISTR.ini", "AAA_DISTR.ini", "0_DISTR.ini",
                 })
        {
            StringComparer.Ordinal.Compare(ours, competitor).Should().BeLessThan(0, $"vs {competitor}");
        }
    }

    [Fact]
    public void IsNpc2SpidConfig_RecognizesOwnOutput_AndRejectsOthers()
    {
        ForwardedOutfitDistributor.IsNpc2SpidConfig(ForwardedOutfitDistributor.BuildSpidFileName(OutputKey))
            .Should().BeTrue("a deployed previous run must not read back as an external contest");
        ForwardedOutfitDistributor.IsNpc2SpidConfig("kco_brd_DISTR.ini").Should().BeFalse();
        ForwardedOutfitDistributor.IsNpc2SpidConfig("").Should().BeFalse();
    }

    // ── Record mode: mirror the contesting distributor ────────────────────────

    [Fact]
    public void RecordMode_SkyPatcherContest_EmitsOutfitDefaultDirective()
    {
        var h = Make();
        using var tmp = new TempDir();

        h.Distributor.Publish(Npc, OutfitDup, SkyContest(), "Ataf");

        h.SkyPatcher.HasEntries.Should().BeTrue("no surrogate exists in record mode; the line is created on demand");
        h.Distributor.HasSpidEntries.Should().BeFalse("only SkyPatcher contested this NPC");

        h.SkyPatcher.WriteIni(tmp.Path).Should().BeTrue();
        File.ReadAllLines(SkyIniPath(tmp.Path)).Where(l => l.Length > 0).Should().Equal(
            "; Ataf [013295:Skyrim.esm] — patched from SkyPatcher: Skyrim_Equipments_Distribution_F.ini (line 3)",
            "filterByNPCs=Skyrim.esm|13295:outfitDefault=NPCTest.esp|801");
    }

    [Fact]
    public void RecordMode_SpidContest_WritesDistrIniTargetingTheNpc()
    {
        var h = Make();
        using var tmp = new TempDir();

        h.Distributor.Publish(Npc, OutfitDup, SpidContest(), "Ataf");

        h.Distributor.HasSpidEntries.Should().BeTrue();
        h.SkyPatcher.HasEntries.Should().BeFalse("only SPID contested this NPC");

        h.Distributor.WriteSpidConfig(tmp.Path, OutputKey).Should().BeTrue();

        var lines = File.ReadAllLines(Path.Combine(tmp.Path,
            ForwardedOutfitDistributor.BuildSpidFileName(OutputKey)));
        var entry = lines.Single(l => l.StartsWith("Outfit", StringComparison.Ordinal));

        // Form|StringFilters|FormFilters — the outfit, no string filters, the NPC.
        entry.Should().Be("Outfit = 0x801~NPCTest.esp|NONE|0x13295~Skyrim.esm");
        entry.Should().NotContain("FinalOutfit",
            "plain Outfit keeps compatibility with SPID builds predating the final-outfit key");
    }

    // ── Provenance comments ───────────────────────────────────────────────────

    [Fact]
    public void GeneratedLines_AreCommentedWithTheNpcAndThePriorConflictWinner()
    {
        var h = Make();
        using var tmp = new TempDir();

        h.Distributor.Publish(Npc, OutfitDup, SpidContest(), "Ataf");
        h.Distributor.WriteSpidConfig(tmp.Path, OutputKey);

        var lines = File.ReadAllLines(Path.Combine(tmp.Path,
            ForwardedOutfitDistributor.BuildSpidFileName(OutputKey))).ToList();
        int entryIdx = lines.FindIndex(l => l.StartsWith("Outfit", StringComparison.Ordinal));

        lines[entryIdx - 1].Should()
            .Be("; Ataf [013295:Skyrim.esm] — patched from SPID: kco_brd_DISTR.ini (line 7)");
    }

    [Fact]
    public void Comments_AreWholeLines_NeverTrailing()
    {
        // SimpleIni (SPID) and SkyPatcher both only honour ';' at the START of a line;
        // mid-line it is swallowed into the value. A trailing note on an entry would
        // therefore corrupt the NPC form filter and silently stop it ever matching.
        var h = Make();
        using var tmp = new TempDir();

        h.Distributor.Publish(Npc, OutfitDup, SkyContest(), "Ataf");
        h.Distributor.Publish(Npc, OutfitDup, SpidContest(), "Ataf");
        h.Distributor.WriteSpidConfig(tmp.Path, OutputKey);
        h.SkyPatcher.WriteIni(tmp.Path);

        var all = File.ReadAllLines(Path.Combine(tmp.Path,
                      ForwardedOutfitDistributor.BuildSpidFileName(OutputKey)))
            .Concat(File.ReadAllLines(SkyIniPath(tmp.Path)));

        foreach (var line in all.Where(l => l.Trim().Length > 0))
        {
            if (line.TrimStart().StartsWith(";", StringComparison.Ordinal)) continue;
            line.Should().NotContain(";", "a directive line must carry no comment at all");
        }
    }

    // ── Round-trip through the real parsers ───────────────────────────────────

    [Fact]
    public void GeneratedSpidIni_ParsesBackToTheIntendedOutfitAndNpc()
    {
        var h = Make();
        using var tmp = new TempDir();

        h.Distributor.Publish(Npc, OutfitDup, SpidContest(), "Ataf");
        h.Distributor.WriteSpidConfig(tmp.Path, OutputKey);

        var fileName = ForwardedOutfitDistributor.BuildSpidFileName(OutputKey);
        var parsed = new SpidOutfitConfigParser()
            .ParseFile(File.ReadAllLines(Path.Combine(tmp.Path, fileName)), fileName);

        var entry = parsed.Should().ContainSingle().Subject;
        entry.IsFinal.Should().BeFalse();

        entry.OutfitForm.Kind.Should().Be(RuntimeFormIdentifierKind.ModAndLocalId);
        entry.OutfitForm.ModName.Should().Be("NPCTest.esp");
        entry.OutfitForm.LocalOrRuntimeId.Should().Be(OutfitDup.ID);

        var npcFilter = entry.FormsMatch.Should().ContainSingle().Subject;
        npcFilter.Kind.Should().Be(RuntimeFormIdentifierKind.ModAndLocalId,
            "a comment bleeding into this section would make it an unresolvable EditorID");
        npcFilter.ModName.Should().Be("Skyrim.esm");
        npcFilter.LocalOrRuntimeId.Should().Be(Npc.ID);

        entry.StringsMatch.Should().BeEmpty();
        entry.FormsNot.Should().BeEmpty();
        entry.ChancePercent.Should().Be(100.0, "a sub-100 chance would make it non-deterministic");
    }

    [Fact]
    public void GeneratedSkyPatcherIni_ParsesBackToTheIntendedOutfitAndNpc()
    {
        var h = Make();
        using var tmp = new TempDir();

        h.Distributor.Publish(Npc, OutfitDup, SkyContest(), "Ataf");
        h.SkyPatcher.WriteIni(tmp.Path);

        var parsed = new SkyPatcherOutfitConfigParser()
            .ParseFile(File.ReadAllLines(SkyIniPath(tmp.Path)), "NPCTest.ini");

        var instruction = parsed.Should().ContainSingle(
            "the ';' provenance line must be skipped, not parsed as an instruction").Subject;

        instruction.OutfitIdentifier.Should().Be("NPCTest.esp|801",
            "trailing text would be captured into the value by SkyPatcher's key regex");
        instruction.FilterByNpcs.Should().Equal("Skyrim.esm|13295");
    }

    [Fact]
    public void RecordMode_BothContest_EmitsToBoth()
    {
        var h = Make();
        using var tmp = new TempDir();

        h.Distributor.Publish(Npc, OutfitDup, new RuntimeOutfitContest
        {
            SkyPatcher = true,
            SkyPatcherDetail = "someconfig.ini (line 1)",
            Spid = true,
            SpidDetail = "kco_brd_DISTR.ini (line 7)",
            SpidSourceFile = "kco_brd_DISTR.ini",
        }, "Ataf");

        h.SkyPatcher.HasEntries.Should().BeTrue();
        h.Distributor.HasSpidEntries.Should().BeTrue(
            "a SkyPatcher directive alone leaves SPID un-suspended once the record already holds the duplicate");
    }

    [Fact]
    public void NoContest_WritesNothing()
    {
        var h = Make();
        using var tmp = new TempDir();

        h.Distributor.Publish(Npc, OutfitDup, RuntimeOutfitContest.None, "Ataf");

        h.SkyPatcher.HasEntries.Should().BeFalse();
        h.Distributor.HasSpidEntries.Should().BeFalse();
        h.Distributor.WriteSpidConfig(tmp.Path, OutputKey).Should().BeTrue();
        File.Exists(Path.Combine(tmp.Path, ForwardedOutfitDistributor.BuildSpidFileName(OutputKey)))
            .Should().BeFalse("an uncontested outfit needs no runtime config");
    }

    // ── Auto-split remap ──────────────────────────────────────────────────────

    [Fact]
    public void SpidConfig_AppliesSplitRemapToTheOutfitKey()
    {
        var h = Make();
        using var tmp = new TempDir();
        var relocated = FormKey.Factory("000801:NPCTest_2.esp");

        h.Distributor.Publish(Npc, OutfitDup, SpidContest(), "Ataf");
        h.Distributor.WriteSpidConfig(tmp.Path, OutputKey,
            new Dictionary<FormKey, FormKey> { [OutfitDup] = relocated }).Should().BeTrue();

        var text = File.ReadAllText(Path.Combine(tmp.Path, ForwardedOutfitDistributor.BuildSpidFileName(OutputKey)));
        text.Should().Contain("0x801~NPCTest_2.esp", "the duplicate moved into the split file");
        text.Should().Contain("0x13295~Skyrim.esm", "the target NPC is not an output record and is untouched");
    }

    // ── Gating and warnings ───────────────────────────────────────────────────

    [Fact]
    public void SettingOff_WarnsInsteadOfPublishing()
    {
        var h = Make(publish: false);

        h.Distributor.Publish(Npc, OutfitDup, SpidContest(), "Ataf");

        h.SkyPatcher.HasEntries.Should().BeFalse();
        h.Distributor.HasSpidEntries.Should().BeFalse();
        h.Log.Should().ContainSingle(l => l.Message.Contains("WARNING") && l.Message.Contains("overwrite"));
    }

    [Fact]
    public void SkyPatcherMode_PublishesNothing_BecauseTheAppearanceIniAlreadyCarriesTheDirective()
    {
        var h = Make(skyPatcherMode: true);

        h.Distributor.Publish(Npc, OutfitDup, new RuntimeOutfitContest
        {
            SkyPatcher = true, SkyPatcherDetail = "x.ini (line 1)",
            Spid = true, SpidDetail = "y_DISTR.ini (line 1)", SpidSourceFile = "y_DISTR.ini",
        }, "Ataf");

        h.SkyPatcher.HasEntries.Should().BeFalse(
            "ApplySkyPatcherDirectives already emits outfitDefault=, which also stands SPID down");
        h.Distributor.HasSpidEntries.Should().BeFalse();
    }

    [Fact]
    public void OutrankedSkyPatcherConfig_Warns_ButStillEmits()
    {
        var h = Make();

        h.Distributor.Publish(Npc, OutfitDup, SkyContest(outranked: true), "Ataf");

        h.Log.Should().Contain(l => l.Message.Contains("read AFTER NPC2's own ini"));
        h.SkyPatcher.HasEntries.Should().BeTrue("it still wins over every config read earlier");
    }

    [Fact]
    public void EarlierSortingSpidConfig_IsReportedAtWriteTime()
    {
        var h = Make();
        using var tmp = new TempDir();

        // '!' beats letters and digits ordinally, but not a name starting with a space.
        h.Distributor.Publish(Npc, OutfitDup, SpidContest(" aaa_DISTR.ini"), "Ataf");
        h.Distributor.WriteSpidConfig(tmp.Path, OutputKey).Should().BeTrue();

        h.Log.Should().Contain(l => l.Message.Contains("WARNING") && l.Message.Contains(" aaa_DISTR.ini"));
    }

    // ── Reinitialize ──────────────────────────────────────────────────────────

    [Fact]
    public void Reinitialize_ClearsTheQueueAndTheStaleFile()
    {
        var h = Make();
        using var tmp = new TempDir();

        h.Distributor.Publish(Npc, OutfitDup, SpidContest(), "Ataf");
        h.Distributor.WriteSpidConfig(tmp.Path, OutputKey);
        var path = Path.Combine(tmp.Path, ForwardedOutfitDistributor.BuildSpidFileName(OutputKey));
        File.Exists(path).Should().BeTrue();

        h.Distributor.Reinitialize(tmp.Path, OutputKey);

        h.Distributor.HasSpidEntries.Should().BeFalse();
        File.Exists(path).Should().BeFalse("a run that publishes nothing must not leave the last run's file behind");
    }

    [Fact]
    public void Reinitialize_FirstIteration_SweepsEveryGeneratedIni_ButSparesForeignOnes()
    {
        var h = Make();
        using var tmp = new TempDir();

        // A previous run split into three plugins; this one only writes the first.
        var ours = new[]
        {
            ForwardedOutfitDistributor.BuildSpidFileName(OutputKey),
            ForwardedOutfitDistributor.BuildSpidFileName(ModKey.FromNameAndExtension("NPCTest_2.esp")),
            ForwardedOutfitDistributor.BuildSpidFileName(ModKey.FromNameAndExtension("NPCTest_3.esp")),
        };
        foreach (var f in ours) File.WriteAllText(Path.Combine(tmp.Path, f), "Outfit = x");
        File.WriteAllText(Path.Combine(tmp.Path, "kco_brd_DISTR.ini"), "Outfit = y");

        h.Distributor.Reinitialize(tmp.Path, OutputKey, clearAllGenerated: true);

        foreach (var f in ours)
        {
            File.Exists(Path.Combine(tmp.Path, f)).Should().BeFalse(
                $"{f} would otherwise keep distributing outfits from a plugin this run no longer writes");
        }
        File.Exists(Path.Combine(tmp.Path, "kco_brd_DISTR.ini")).Should().BeTrue(
            "only NPC2's own generated configs may be swept");
    }

    [Fact]
    public void Reinitialize_LaterIteration_SparesTheOtherPluginsInis()
    {
        var h = Make();
        using var tmp = new TempDir();

        var first = Path.Combine(tmp.Path, ForwardedOutfitDistributor.BuildSpidFileName(OutputKey));
        var second = ModKey.FromNameAndExtension("NPCTest_2.esp");
        File.WriteAllText(first, "Outfit = x");

        // Split output: iteration 2 must not delete what iteration 1 just wrote.
        h.Distributor.Reinitialize(tmp.Path, second);

        File.Exists(first).Should().BeTrue();
    }
}
