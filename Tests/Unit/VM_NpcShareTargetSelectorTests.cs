using System.Collections.ObjectModel;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using NPC_Plugin_Chooser_2.View_Models;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// <see cref="VM_NpcShareTargetSelector"/>'s filter pass: the appearance's own source NPC is kept
/// out of the pickable list (an NPC shared with itself yields an entry that can never be
/// unshared), and <c>ExclusionNote</c> explains that absence ONLY when the search has narrowed to
/// that NPC alone — otherwise the exclusion is silent.
/// <para>Both VMs are allocated with <see cref="Reflect.Uninitialized{T}"/> and only the fields
/// the filter reads are poked in, so no live game data, DI graph, STA dispatcher or ReactiveUI
/// scheduler is required.</para>
/// </summary>
public class VM_NpcShareTargetSelectorTests
{
    private static VM_NpcsMenuSelection MakeNpc(string display, string editorId, string formKey)
    {
        var npc = Reflect.Uninitialized<VM_NpcsMenuSelection>();
        Reflect.SetField(npc, "DisplayName", display);
        Reflect.SetField(npc, "NpcEditorId", editorId);
        Reflect.SetField(npc, "NpcFormKeyString", formKey);
        Reflect.SetField(npc, "NpcFormKey", MutagenFixtures.Fk(formKey));
        return npc;
    }

    /// <summary>Builds the selector past its constructor (which wires commands and a throttled
    /// subscription) and drives <c>ApplyFilter</c> directly.</summary>
    private static VM_NpcShareTargetSelector MakeSelector(
        List<VM_NpcsMenuSelection> pickable, VM_NpcsMenuSelection? excluded,
        string searchText, NpcSearchType searchType = NpcSearchType.Name)
    {
        var vm = Reflect.Uninitialized<VM_NpcShareTargetSelector>();
        Reflect.SetField(vm, "_allNpcs", pickable);
        Reflect.SetField(vm, "_excludedNpc", excluded);
        Reflect.SetField(vm, "FilteredNpcs", new ObservableCollection<VM_NpcsMenuSelection>());
        Reflect.SetField(vm, "ExclusionNote", string.Empty);
        Reflect.SetField(vm, "SearchText", searchText);
        Reflect.SetField(vm, "SearchType", searchType);
        Reflect.InvokeVoid(vm, "ApplyFilter");
        return vm;
    }

    private static readonly VM_NpcsMenuSelection Lydia =
        MakeNpc("Lydia", "Lydia", "0A2C94:Skyrim.esm");
    private static readonly VM_NpcsMenuSelection Amalee =
        MakeNpc("Amalee", "Amalee3DNPC", "000D01:3DNPC.esp");

    // ---- The exclusion itself -------------------------------------------------------

    [Fact]
    public void ExcludedNpc_IsNotInTheList()
    {
        var vm = MakeSelector(new List<VM_NpcsMenuSelection> { Amalee }, Lydia, string.Empty);

        vm.FilteredNpcs.Should().ContainSingle().Which.Should().BeSameAs(Amalee);
    }

    // ---- When the note appears -------------------------------------------------------

    [Fact]
    public void SearchMatchingOnlyTheExcludedNpc_ExplainsWhyTheListIsEmpty()
    {
        var vm = MakeSelector(new List<VM_NpcsMenuSelection> { Amalee }, Lydia, "Lydia");

        vm.HasExclusionNote.Should().BeTrue();
        vm.ExclusionNote.Should().Contain("Lydia").And.Contain("cannot be shared with itself");
    }

    [Fact]
    public void SearchMatchingOtherNpcsToo_StaysSilent()
    {
        // "a" matches both Amalee (listed) and Lydia (excluded): the list isn't empty, so there
        // is nothing to explain.
        var vm = MakeSelector(new List<VM_NpcsMenuSelection> { Amalee }, Lydia, "a");

        vm.FilteredNpcs.Should().ContainSingle().Which.Should().BeSameAs(Amalee);
        vm.HasExclusionNote.Should().BeFalse();
        vm.ExclusionNote.Should().BeEmpty();
    }

    [Fact]
    public void SearchMatchingNothingAtAll_StaysSilent()
    {
        // An empty list here is the user's own search miss, not the exclusion's doing.
        var vm = MakeSelector(new List<VM_NpcsMenuSelection> { Amalee }, Lydia, "Nobody");

        vm.FilteredNpcs.Should().BeEmpty();
        vm.HasExclusionNote.Should().BeFalse();
    }

    [Fact]
    public void NoExclusion_NeverShowsTheNote()
    {
        var vm = MakeSelector(new List<VM_NpcsMenuSelection> { Amalee, Lydia }, null, "Lydia");

        vm.FilteredNpcs.Should().ContainSingle().Which.Should().BeSameAs(Lydia);
        vm.HasExclusionNote.Should().BeFalse();
    }

    // ---- The note honours the active search type -------------------------------------

    [Theory]
    [InlineData(NpcSearchType.EditorID, "Lydia")]
    [InlineData(NpcSearchType.FormKey, "0A2C94")]
    public void NoteAppearsForEverySearchType(NpcSearchType searchType, string searchText)
    {
        var vm = MakeSelector(new List<VM_NpcsMenuSelection> { Amalee }, Lydia, searchText, searchType);

        vm.FilteredNpcs.Should().BeEmpty();
        vm.HasExclusionNote.Should().BeTrue();
    }

    [Fact]
    public void NoteDoesNotAppear_WhenTheSearchTypeDoesNotMatchTheExcludedNpc()
    {
        // The FormKey search text matches nothing at all, including the excluded NPC.
        var vm = MakeSelector(new List<VM_NpcsMenuSelection> { Amalee }, Lydia, "Lydia",
            NpcSearchType.FormKey);

        vm.FilteredNpcs.Should().BeEmpty();
        vm.HasExclusionNote.Should().BeFalse();
    }
}
