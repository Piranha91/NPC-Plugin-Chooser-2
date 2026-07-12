using System.IO;
using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// <see cref="Auxilliary.LazyEnumerateMajorRecords(ISkyrimModGetter)"/> and
/// <see cref="Auxilliary.CountRecordsByAppearance"/>: identity enumeration walks each group's
/// FormKey cache without constructing the records, so scanning must survive plugins containing
/// records whose subrecord data Mutagen rejects as malformed. The binary test reproduces the
/// real-world "Baba Yaga" case (ksws03_quest.esp): a FootstepSet whose DATA payload disagrees
/// with its XCNT counts, which throws on record construction but must not abort mod analysis.
/// </summary>
public class AuxilliaryLazyEnumerationTests
{
    // ---- In-memory identity enumeration ------------------------------------------------------

    [Fact]
    public void LazyEnumerateMajorRecords_YieldsIdentityForEveryTopLevelRecord()
    {
        var mod = MutagenFixtures.NewMod("Enum.esp");
        var npc = mod.Npcs.AddNew();
        var weapon = mod.Weapons.AddNew();
        var footstepSet = mod.FootstepSets.AddNew();

        var links = Auxilliary.LazyEnumerateMajorRecords(mod).ToList();

        links.Select(l => (l.FormKey, l.Type)).Should().BeEquivalentTo(new[]
        {
            (npc.FormKey, typeof(INpcGetter)),
            (weapon.FormKey, typeof(IWeaponGetter)),
            (footstepSet.FormKey, typeof(IFootstepSetGetter)),
        });
    }

    [Fact]
    public void LazyEnumerateMajorRecords_SkipsRequestedGroups()
    {
        var mod = MutagenFixtures.NewMod("Enum.esp");
        var npc = mod.Npcs.AddNew();
        var weapon = mod.Weapons.AddNew();

        var links = Auxilliary.LazyEnumerateMajorRecords(mod, new HashSet<Type> { typeof(INpcGetter) }).ToList();

        links.Select(l => l.FormKey).Should().ContainSingle().Which.Should().Be(weapon.FormKey);
        _ = npc; // silence unused-variable analysis; presence in the mod is the point
    }

    [Fact]
    public void MergeInClassifierCountPlugin_SplitsSupportAndHardUsingAppearanceRecordTypes()
    {
        var mod = MutagenFixtures.NewMod("Counts.esp");
        mod.Npcs.AddNew();
        mod.TextureSets.AddNew();
        mod.Weapons.AddNew();
        mod.FootstepSets.AddNew();
        mod.Quests.AddNew();

        var counts = MergeInClassifier.CountPlugin(mod, new HashSet<ModKey> { mod.ModKey });

        counts.Should().Be(new MergeInClassifier.Counts(
            OverrideNpcs: 0, NewNpcs: 1, SupportRecords: 1, HardRecords: 3));
    }

    // ---- Malformed-record robustness (Baba Yaga regression) ----------------------------------

    [Fact]
    public async Task MalformedFootstepSet_BreaksRecordParsing_ButNotIdentityEnumerationOrCounts()
    {
        using var tmp = new TempDir("ftst");
        var mod = MutagenFixtures.NewMod("Corrupt.esp");
        var npc = mod.Npcs.AddNew();
        var weapon = mod.Weapons.AddNew();
        var footstep = mod.Footsteps.AddNew();
        var set = mod.FootstepSets.AddNew();
        set.WalkForwardFootsteps.Add(new FormLink<IFootstepGetter>(footstep.FormKey));

        var path = tmp.Combine("Corrupt.esp");
        await mod.BeginWrite.ToPath(path).WithLoadOrderFromHeaderMasters().WithNoDataFolder().WriteAsync();

        // Zero out the first XCNT count in place (1 -> 0, little-endian) so the FTST's 4-byte DATA
        // payload no longer matches the declared counts — the exact "DATA record had unexpected
        // length that did not match previous counts 4 != 0" shape seen in the wild. No lengths
        // change, so record/group headers stay valid and the overlay still opens.
        var bytes = File.ReadAllBytes(path);
        int idx = bytes.AsSpan().IndexOf("XCNT"u8);
        idx.Should().BeGreaterThan(0);
        bytes.AsSpan().LastIndexOf("XCNT"u8).Should().Be(idx, "the test assumes a single FTST XCNT subrecord");
        bytes[idx + 6] = 0; // 4-byte type + 2-byte size, then the first uint32 count
        File.WriteAllBytes(path, bytes);

        using var overlay = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);

        // Premise guard: constructing the FootstepSet record must throw. If a Mutagen upgrade ever
        // makes this parse leniently, the robustness assertions below stop testing anything.
        Action constructRecord = () => overlay.FootstepSets.First();
        constructRecord.Should().Throw<Exception>();

        var links = Auxilliary.LazyEnumerateMajorRecords(overlay).ToList();
        links.Select(l => l.FormKey).Should().BeEquivalentTo(new[]
        {
            npc.FormKey, weapon.FormKey, footstep.FormKey, set.FormKey,
        });

        var counts = MergeInClassifier.CountPlugin(overlay, new HashSet<ModKey> { overlay.ModKey });
        counts.Should().Be(new MergeInClassifier.Counts(
            OverrideNpcs: 0, NewNpcs: 1, SupportRecords: 0, HardRecords: 3));
    }
}
