using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// <see cref="RecordFieldPathMapper"/> — the reverse map from a FormKey back to the field that
/// holds it.
///
/// <para><b>Why it exists.</b> Mutagen's <c>EnumerateFormLinks</c> yields bare links, and a link's
/// declared type is only as specific as the field declaring it. A Papyrus script property is
/// <c>IFormLinkGetter&lt;ISkyrimMajorRecordGetter&gt;</c>, which names no record type at all, so for
/// those links the field path is the ONLY thing that says where the reference lives. Both the
/// patcher's missing-master report and the pre-run screening dialog lean on it.</para>
///
/// <para>Measured against the motivating specimen: High Poly NPC Overhaul's Miraak
/// (017936:Dragonborn.esm) holds 000014:Skyrim.esm at
/// <c>VirtualMachineAdapter.Scripts[1].Properties[0].Object</c>, and the walker names all 38 of that
/// record's links — the depth limit has to reach a script property or the generic-link case is
/// left with nothing to show.</para>
/// </summary>
public class RecordFieldPathMapperTests
{
    private static readonly FormKey PlayerRef = FormKey.Factory("000014:Skyrim.esm");
    private static readonly FormKey NordRace = FormKey.Factory("013746:Skyrim.esm");
    private static readonly FormKey SomeHeadPart = FormKey.Factory("000933:High Poly Head.esm");

    [Fact]
    public void NamesADirectLinkField()
    {
        var npc = MutagenFixtures.NewNpc(MutagenFixtures.NewMod("Paths.esp"), "PathNpc");
        npc.Race.SetTo(NordRace);

        RecordFieldPathMapper.FindFieldPath(npc, NordRace).Should().Be("Race");
    }

    [Fact]
    public void NamesAListEntryByIndex()
    {
        var npc = MutagenFixtures.NewNpc(MutagenFixtures.NewMod("Paths.esp"), "PathNpc");
        npc.HeadParts.Add(FormKey.Factory("02425E:Skyrim.esm").ToLink<IHeadPartGetter>());
        npc.HeadParts.Add(SomeHeadPart.ToLink<IHeadPartGetter>());

        RecordFieldPathMapper.FindFieldPath(npc, SomeHeadPart).Should().Be("HeadParts[1]");
    }

    /// <summary>The case the depth limit exists for — six levels down, and the only reachable
    /// description of a link whose declared type is the "any record" base.</summary>
    [Fact]
    public void NamesAPapyrusScriptPropertyAtFullDepth()
    {
        var npc = MutagenFixtures.NewNpc(MutagenFixtures.NewMod("Paths.esp"), "PathNpc");
        var adapter = new VirtualMachineAdapter();
        var unrelated = new ScriptEntry { Name = "DLC2MiraakScript" };
        var carrying = new ScriptEntry { Name = "DLC2MiraakSoulStealScript" };
        var property = new ScriptObjectProperty { Name = "PlayerRef" };
        property.Object.SetTo(PlayerRef);
        carrying.Properties.Add(property);
        adapter.Scripts.Add(unrelated);
        adapter.Scripts.Add(carrying);
        npc.VirtualMachineAdapter = adapter;

        RecordFieldPathMapper.FindFieldPath(npc, PlayerRef)
            .Should().Be("VirtualMachineAdapter.Scripts[1].Properties[0].Object");
    }

    [Fact]
    public void NamesEveryLinkTheRecordActuallyHolds()
    {
        // The property that matters more than any single path: a link the walk cannot reach would
        // silently print as "(field unknown)" rather than fail anything.
        var npc = MutagenFixtures.NewNpc(MutagenFixtures.NewMod("Paths.esp"), "PathNpc");
        npc.Race.SetTo(NordRace);
        npc.HeadParts.Add(SomeHeadPart.ToLink<IHeadPartGetter>());
        var adapter = new VirtualMachineAdapter();
        var entry = new ScriptEntry { Name = "SomeScript" };
        var property = new ScriptObjectProperty { Name = "PlayerRef" };
        property.Object.SetTo(PlayerRef);
        entry.Properties.Add(property);
        adapter.Scripts.Add(entry);
        npc.VirtualMachineAdapter = adapter;

        var all = npc.EnumerateFormLinks().Where(l => !l.FormKey.IsNull)
            .Select(l => l.FormKey).ToHashSet();

        RecordFieldPathMapper.MapFieldNames(npc, all).Keys.Should().BeEquivalentTo(all);
    }

    [Fact]
    public void ReturnsNullForAKeyTheRecordDoesNotHold()
    {
        var npc = MutagenFixtures.NewNpc(MutagenFixtures.NewMod("Paths.esp"), "PathNpc");
        npc.Race.SetTo(NordRace);

        RecordFieldPathMapper.FindFieldPath(npc, PlayerRef).Should().BeNull();
    }

    [Fact]
    public void ToleratesANullRecordAndANullKey()
    {
        var npc = MutagenFixtures.NewNpc(MutagenFixtures.NewMod("Paths.esp"), "PathNpc");

        RecordFieldPathMapper.FindFieldPath(null, PlayerRef).Should().BeNull();
        RecordFieldPathMapper.FindFieldPath(npc, FormKey.Null).Should().BeNull();
    }
}
