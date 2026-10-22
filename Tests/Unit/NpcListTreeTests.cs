using FluentAssertions;
using NPC_Plugin_Chooser_2.View_Models;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// The NPCs tab's template tree (<see cref="NpcListTree"/>, Settings.GroupTemplatedNpcsInList) and
/// the hide rule behind Settings.HideInheritingTemplatedNpcs
/// (<see cref="VM_NpcSelectionBar.ShouldHideInheritingNpc(bool, bool, bool)"/>). The parent relation
/// is supplied by the caller, so these pin the layout rules alone.
/// </summary>
public class NpcListTreeTests
{
    private sealed class Node
    {
        public Node(string name, Node? parent = null) { Name = name; Parent = parent; }
        public string Name { get; }
        public Node? Parent { get; set; }
        public bool Expanded { get; set; } = true;
        public override string ToString() => Name;
    }

    private static List<string> Layout(IReadOnlyList<Node> sortedMatches) =>
        NpcListTree.Build(sortedMatches, n => n.Parent, n => n.Expanded)
            .Select(r => new string(' ', r.Depth * 2) + r.Node.Name + (r.IsContext ? " (context)" : ""))
            .ToList();

    [Fact]
    public void TemplatedNpcs_SitUnderTheirTemplate_NestedForChains_InSortOrder()
    {
        var guard = new Node("Guard");
        var b = new Node("B", guard);
        var a = new Node("A", guard);
        var a1 = new Node("A1", a);
        var lone = new Node("Lone");

        // Incoming order is the user's sort; siblings and roots keep it.
        Layout(new[] { a, a1, b, guard, lone }).Should().Equal(
            "Guard",
            "  A",
            "    A1",
            "  B",
            "Lone");
    }

    [Fact]
    public void AMatchWhoseTemplateDidNotMatch_KeepsItsPlace_UnderADimmedContextRow()
    {
        var guard = new Node("Guard");
        var bandit = new Node("Bandit", guard);

        var rows = NpcListTree.Build(new[] { bandit }, n => n.Parent, n => n.Expanded);

        rows.Select(r => (r.Node.Name, r.Depth, r.IsContext)).Should().Equal(
            ("Guard", 0, true),
            ("Bandit", 1, false));
        rows[0].HasChildren.Should().BeTrue();
    }

    [Fact]
    public void AContextRow_SortsWhereItsEarliestDescendantWould()
    {
        var late = new Node("LateTemplate");
        var child = new Node("Child", late);
        var other = new Node("Other");

        Layout(new[] { child, other }).Should().Equal(
            "LateTemplate (context)",
            "  Child",
            "Other");
    }

    [Fact]
    public void ACollapsedNode_HidesItsDescendants_ButKeepsItsChildrenFlag()
    {
        var guard = new Node("Guard") { Expanded = false };
        var bandit = new Node("Bandit", guard);

        var rows = NpcListTree.Build(new[] { guard, bandit }, n => n.Parent, n => n.Expanded);

        rows.Should().ContainSingle().Which.HasChildren.Should().BeTrue("the arrow must stay to re-expand it");
    }

    [Fact]
    public void ATemplateLoop_DoesNotHang_AndEveryNodeStillAppears()
    {
        var x = new Node("X");
        var y = new Node("Y", x);
        x.Parent = y;
        var z = new Node("Z", y);

        var rows = NpcListTree.Build(new[] { x, y, z }, n => n.Parent, n => n.Expanded);

        rows.Select(r => r.Node.Name).Should().BeEquivalentTo(new[] { "X", "Y", "Z" });
        NpcListTree.FindChainTerminus(z, n => n.Parent).Should().BeNull("a looping chain has no face owner");
    }

    [Fact]
    public void ChainTerminus_IsTheTopOfTheChain_OrNullWithoutATemplate()
    {
        var top = new Node("Top");
        var mid = new Node("Mid", top);
        var leaf = new Node("Leaf", mid);

        NpcListTree.FindChainTerminus(leaf, n => n.Parent).Should().BeSameAs(top);
        NpcListTree.FindChainTerminus(top, n => n.Parent).Should().BeNull();
    }

    // ---- Hide rule -------------------------------------------------------------------------

    [Fact]
    public void Hide_OnlyWhenTheFaceIsAlreadyDecided()
    {
        VM_NpcSelectionBar.ShouldHideInheritingNpc(terminusOwnsFace: true, terminusHasSelection: true,
            chainWillBeFlattened: false).Should().BeTrue();

        VM_NpcSelectionBar.ShouldHideInheritingNpc(true, terminusHasSelection: false, false)
            .Should().BeFalse("the template NPC has no appearance assigned yet");
        VM_NpcSelectionBar.ShouldHideInheritingNpc(true, true, chainWillBeFlattened: true)
            .Should().BeFalse("under 'Give each NPC its own copy' this NPC's own pick matters");
        VM_NpcSelectionBar.ShouldHideInheritingNpc(terminusOwnsFace: false, true, false)
            .Should().BeFalse("a chain topping out in a levelled list has no fixed face");
    }
}
