using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.View_Models;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// <see cref="NpcConsistencyProvider"/> — per-NPC selection state + change observable.
/// Constructed with a real <see cref="Settings"/> and a null-yielding Lazy&lt;VM_Settings&gt;
/// (its only use is the optional <c>RequestThrottledSave()</c>, which is null-conditional).
/// </summary>
public class NpcConsistencyProviderTests
{
    private static readonly FormKey Npc1 = FormKey.Factory("000801:Skyrim.esm");
    private static readonly FormKey Npc2 = FormKey.Factory("000802:Skyrim.esm");
    private static readonly FormKey Src1 = FormKey.Factory("0A0001:Mod.esp");

    private static NpcConsistencyProvider Make(Settings s) =>
        new(s, new Lazy<VM_Settings>(() => null!));

    private static (NpcConsistencyProvider provider, List<NpcSelectionChangedEventArgs> events) MakeWithCapture(Settings s)
    {
        var provider = Make(s);
        var events = new List<NpcSelectionChangedEventArgs>();
        provider.NpcSelectionChanged.Subscribe(events.Add);
        return (provider, events);
    }

    [Fact]
    public void Ctor_CopiesExistingSelectionsFromSettings()
    {
        var s = new Settings();
        s.SelectedAppearanceMods[Npc1] = ("ModA", Src1);
        var provider = Make(s);
        provider.DoesNpcHaveSelection(Npc1).Should().BeTrue();
        provider.GetSelectedMod(Npc1).Should().Be(("ModA", Src1));
    }

    [Fact]
    public void SetSelectedMod_FirstSet_RaisesEventAndWritesSettings()
    {
        var s = new Settings();
        var (provider, events) = MakeWithCapture(s);

        provider.SetSelectedMod(Npc1, "ModA", Src1);

        events.Should().ContainSingle();
        events[0].NpcFormKey.Should().Be(Npc1);
        events[0].SelectedModName.Should().Be("ModA");
        events[0].SourceNpcFormKey.Should().Be(Src1);
        s.SelectedAppearanceMods[Npc1].Should().Be(("ModA", Src1));
    }

    [Fact]
    public void SetSelectedMod_EmptyModName_IsNoOp()
    {
        var (provider, events) = MakeWithCapture(new Settings());
        provider.SetSelectedMod(Npc1, "", Src1);
        events.Should().BeEmpty();
        provider.DoesNpcHaveSelection(Npc1).Should().BeFalse();
    }

    [Fact]
    public void SetSelectedMod_SameValue_DoesNotRaiseAgain()
    {
        var (provider, events) = MakeWithCapture(new Settings());
        provider.SetSelectedMod(Npc1, "ModA", Src1);
        provider.SetSelectedMod(Npc1, "ModA", Src1);
        events.Should().ContainSingle("identical re-selection is suppressed");
    }

    [Fact]
    public void SetSelectedMod_DifferentSource_RaisesEvent()
    {
        var (provider, events) = MakeWithCapture(new Settings());
        provider.SetSelectedMod(Npc1, "ModA", Src1);
        provider.SetSelectedMod(Npc1, "ModA", FormKey.Factory("0B0002:Mod.esp"));
        events.Should().HaveCount(2);
    }

    [Fact]
    public void ClearSelectedMod_RaisesNullEvent()
    {
        var s = new Settings();
        var (provider, events) = MakeWithCapture(s);
        provider.SetSelectedMod(Npc1, "ModA", Src1);
        events.Clear();

        provider.ClearSelectedMod(Npc1);

        events.Should().ContainSingle();
        events[0].SelectedModName.Should().BeNull();
        events[0].SourceNpcFormKey.Should().Be(FormKey.Null);
        s.SelectedAppearanceMods.Should().NotContainKey(Npc1);
    }

    [Fact]
    public void ClearSelectedMod_NoSelection_IsNoOp()
    {
        var (provider, events) = MakeWithCapture(new Settings());
        provider.ClearSelectedMod(Npc1);
        events.Should().BeEmpty();
    }

    [Fact]
    public void ClearAllSelections_RaisesEventPerKey()
    {
        var (provider, events) = MakeWithCapture(new Settings());
        provider.SetSelectedMod(Npc1, "ModA", Src1);
        provider.SetSelectedMod(Npc2, "ModB", Src1);
        events.Clear();

        provider.ClearAllSelections();

        events.Should().HaveCount(2);
        events.Select(e => e.NpcFormKey).Should().BeEquivalentTo(new[] { Npc1, Npc2 });
        provider.DoesNpcHaveSelection(Npc1).Should().BeFalse();
        provider.DoesNpcHaveSelection(Npc2).Should().BeFalse();
    }

    [Fact]
    public void IsModSelected_MatchesNameAndSource()
    {
        var provider = Make(new Settings());
        provider.SetSelectedMod(Npc1, "ModA", Src1);
        provider.IsModSelected(Npc1, "ModA", Src1).Should().BeTrue();
        provider.IsModSelected(Npc1, "ModA", FormKey.Null).Should().BeFalse();
        provider.IsModSelected(Npc1, "ModB", Src1).Should().BeFalse();
    }

    [Fact]
    public void GetSelectedMod_NoSelection_ReturnsNullAndNullFormKey()
    {
        Make(new Settings()).GetSelectedMod(Npc1).Should().Be(((string?)null, FormKey.Null));
    }

    [Fact]
    public void RestoreSelections_ClearsThenReapplies()
    {
        var (provider, events) = MakeWithCapture(new Settings());
        provider.SetSelectedMod(Npc1, "Old", Src1);
        events.Clear();

        var backup = new Dictionary<FormKey, (string ModName, FormKey NpcFormKey)>
        {
            [Npc2] = ("ModB", Src1),
        };
        provider.RestoreSelections(backup);

        provider.DoesNpcHaveSelection(Npc1).Should().BeFalse("the old selection was cleared");
        provider.GetSelectedMod(Npc2).Should().Be(("ModB", Src1));
    }
}
