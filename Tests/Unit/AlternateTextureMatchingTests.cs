using System.Collections.Generic;
using System.Linq;
using CharacterViewer.Rendering;
using FluentAssertions;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// Pins the AlternateTextures (MODS) shape-matching contract the renderer uses
/// (CharacterViewer.Rendering.AlternateTextureMatching): 3D Name first, and only
/// entries whose name matches NO shape of the mesh fall back to 3D-Index
/// matching. Name-only matching silently lost variants on BodySlide-rebuilt
/// meshes (renamed shapes → "black skirt" that renders fine in game/CK, which
/// key on the index); index-primary matching would mis-target shapes in
/// block-resorted files (an [Index == 1] entry whose named shape is the third
/// geometry in the file). The two failure modes are disjoint — renamers keep
/// order, re-sorters keep names — so name-first + dangling-only index fallback
/// covers both.
/// </summary>
public class AlternateTextureMatchingTests
{
    private static AlternateTextureSpec Spec(string name, int idx, params (int Slot, string Path)[] slots)
        => new()
        {
            ShapeName = name,
            ShapeIndex = idx,
            Textures = slots.ToDictionary(s => s.Slot, s => s.Path),
        };

    private static Dictionary<int, string>? Match(
        IReadOnlyList<AlternateTextureSpec> specs, IEnumerable<string> shapeNames,
        string shapeName, int shapeOrdinal,
        ISet<AlternateTextureSpec>? consumed = null,
        ICollection<AlternateTextureSpec>? viaIndex = null)
    {
        var pool = AlternateTextureMatching.DanglingNameEntries(specs, shapeNames);
        return AlternateTextureMatching.MatchForShape(
            specs, pool, shapeName, shapeOrdinal, consumed, viaIndex);
    }

    [Fact]
    public void NameMatch_Applies_AndIgnoresIndex()
    {
        // Index deliberately points at a different ordinal — name wins.
        var spec = Spec("Skirt.001:001", 5, (0, "textures\\white_d.dds"));
        var specs = new[] { spec };
        var consumed = new HashSet<AlternateTextureSpec>();
        var viaIndex = new List<AlternateTextureSpec>();

        var result = Match(specs, new[] { "Skirt.001:001" }, "Skirt.001:001", 0, consumed, viaIndex);

        result.Should().NotBeNull();
        result![0].Should().Be("textures\\white_d.dds");
        consumed.Should().ContainSingle().Which.Should().BeSameAs(spec);
        viaIndex.Should().BeEmpty();
    }

    [Fact]
    public void NameMatch_IsCaseInsensitive()
    {
        var specs = new[] { Spec("SKIRT.001:001", 0, (0, "textures\\white_d.dds")) };

        var result = Match(specs, new[] { "Skirt.001:001" }, "Skirt.001:001", 0);

        result.Should().NotBeNull();
    }

    [Fact]
    public void BodySlideRename_AppliesByIndexFallback()
    {
        // The field case: record authored against shape "Skirt.001:001" at
        // index 0; BodySlide output renamed it "SKiRt1.001" but kept order.
        var spec = Spec("Skirt.001:001", 0, (0, "textures\\skirtw_d.dds"), (1, "textures\\skirtw_n.dds"));
        var specs = new[] { spec };
        var shapeNames = new[] { "SKiRt1.001", "Virtual.Body.1", "VirtualGround" };
        var consumed = new HashSet<AlternateTextureSpec>();
        var viaIndex = new List<AlternateTextureSpec>();

        var skirt = Match(specs, shapeNames, "SKiRt1.001", 0, consumed, viaIndex);
        var body = Match(specs, shapeNames, "Virtual.Body.1", 1, consumed);
        var ground = Match(specs, shapeNames, "VirtualGround", 2, consumed);

        skirt.Should().NotBeNull();
        skirt![0].Should().Be("textures\\skirtw_d.dds");
        skirt[1].Should().Be("textures\\skirtw_n.dds");
        body.Should().BeNull();
        ground.Should().BeNull();
        consumed.Should().ContainSingle().Which.Should().BeSameAs(spec);
        viaIndex.Should().ContainSingle().Which.Should().BeSameAs(spec);
    }

    [Fact]
    public void EntryNamedElsewhere_NeverIndexHijacksAnotherShape()
    {
        // Block-resorted file: the entry's index (0) no longer lines up with
        // its named shape "B" (now ordinal 2). The name still binds to B; the
        // stale index must NOT drag the texture onto shape "A" at ordinal 0.
        var spec = Spec("B", 0, (0, "textures\\b_d.dds"));
        var specs = new[] { spec };
        var shapeNames = new[] { "A", "B" };

        var a = Match(specs, shapeNames, "A", 0);
        var b = Match(specs, shapeNames, "B", 2);

        a.Should().BeNull();
        b.Should().NotBeNull();
        b![0].Should().Be("textures\\b_d.dds");
    }

    [Fact]
    public void UnmatchedEntry_StaysUnconsumed()
    {
        // Neither the name nor the index binds to any shape — the caller reads
        // this back from `consumed` to log the dangling entry.
        var spec = Spec("Ghost", 9, (0, "textures\\ghost_d.dds"));
        var specs = new[] { spec };
        var shapeNames = new[] { "A", "B" };
        var consumed = new HashSet<AlternateTextureSpec>();

        Match(specs, shapeNames, "A", 0, consumed).Should().BeNull();
        Match(specs, shapeNames, "B", 1, consumed).Should().BeNull();
        consumed.Should().BeEmpty();
    }

    [Fact]
    public void LaterEntryWinsPerSlot_OnTheSameShape()
    {
        var first = Spec("Coat", 0, (0, "textures\\blue_d.dds"), (1, "textures\\coat_n.dds"));
        var second = Spec("Coat", 0, (0, "textures\\red_d.dds"));
        var specs = new[] { first, second };
        var consumed = new HashSet<AlternateTextureSpec>();

        var result = Match(specs, new[] { "Coat" }, "Coat", 0, consumed);

        result.Should().NotBeNull();
        result![0].Should().Be("textures\\red_d.dds", "the later duplicate entry wins per slot");
        result[1].Should().Be("textures\\coat_n.dds", "slots the later entry doesn't set survive");
        consumed.Should().HaveCount(2);
    }

    [Fact]
    public void UnnamedEntry_MatchesByIndexOnly()
    {
        var specs = new[] { Spec("", 1, (0, "textures\\x_d.dds")) };
        var shapeNames = new[] { "A", "B" };

        Match(specs, shapeNames, "A", 0).Should().BeNull();
        Match(specs, shapeNames, "B", 1).Should().NotBeNull();
    }

    [Fact]
    public void UnknownOrdinalOrIndex_NeverMatchesByIndex()
    {
        // ShapeOrdinal -1 = shape built outside the indexed path; index -1 =
        // record didn't carry one. Neither side may index-match.
        var specs = new[] { Spec("Ghost", -1, (0, "textures\\x_d.dds")) };

        Match(specs, new[] { "A" }, "A", -1).Should().BeNull();
        Match(specs, new[] { "A" }, "A", 0).Should().BeNull();
    }
}
