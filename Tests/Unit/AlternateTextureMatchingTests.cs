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

    // ---- Duplicate shape names (AUD-7) ------------------------------------------------------
    //
    // A name match is one-to-many when a mesh has two shapes with the same name, and the engine
    // gives the entry to exactly one of them — it keys on the 3D Index. (Evidence for "the engine
    // keys on the index" is the BodySlide rename A/B in the class doc: a renamed shape still
    // rendered its variant in game and the CK with the name matching nothing. That an entry
    // therefore lands on ONE of several namesakes is an inference from it, not a separate
    // measurement — no NIF with duplicate names plus AlternateTextures has been tested in game.)
    //
    // So the index only breaks the tie when it actually picks out one of the same-named shapes.
    // When it matches none of them, every namesake keeps the entry: that is the block-re-sort
    // desync, where the index is the unreliable field, and an entry bound to nothing would be a
    // worse outcome than one bound twice.

    private static Dictionary<int, string>? MatchWithDuplicates(
        IReadOnlyList<AlternateTextureSpec> specs, IReadOnlyList<string> shapeNames,
        string shapeName, int shapeOrdinal,
        ICollection<AlternateTextureSpec>? skippedByAmbiguity = null)
    {
        var pool = AlternateTextureMatching.DanglingNameEntries(specs, shapeNames);
        var byName = AlternateTextureMatching.BuildShapeOrdinalsByName(shapeNames);
        return AlternateTextureMatching.MatchForShape(
            specs, pool, shapeName, shapeOrdinal, null, null, byName, skippedByAmbiguity);
    }

    [Fact]
    public void BuildShapeOrdinalsByName_IsNullWhenNothingIsDuplicated()
    {
        // Null is the "no ambiguity" signal, so a normal mesh provably cannot reach the
        // disambiguation branch at all.
        AlternateTextureMatching.BuildShapeOrdinalsByName(new[] { "A", "B", "C" }).Should().BeNull();
        AlternateTextureMatching.BuildShapeOrdinalsByName(System.Array.Empty<string>()).Should().BeNull();
    }

    [Fact]
    public void BuildShapeOrdinalsByName_KeepsOnlyTheDuplicatedNames()
    {
        var byName = AlternateTextureMatching.BuildShapeOrdinalsByName(new[] { "A", "B", "A", "C" });

        byName.Should().NotBeNull();
        byName!.Keys.Should().Equal("A");
        byName["A"].Should().Equal(0, 2);
    }

    [Fact]
    public void DuplicateNames_IndexPicksTheOneShape()
    {
        // Two shapes called "Skirt"; the entry's index names the second. Only that one changes —
        // before this, both did, and one of them was wrong.
        var specs = new[] { Spec("Skirt", 2, (0, "textures\\variant_d.dds")) };
        var shapeNames = new[] { "Body", "Skirt", "Skirt" };

        MatchWithDuplicates(specs, shapeNames, "Skirt", 1).Should().BeNull();
        MatchWithDuplicates(specs, shapeNames, "Skirt", 2).Should().NotBeNull();
    }

    [Fact]
    public void DuplicateNames_ReportsTheShapeItStoodDownFor()
    {
        // A shape quietly losing a TXST it used to get is indistinguishable from a regression in a
        // log, so the decline is recorded rather than silent.
        var specs = new[] { Spec("Skirt", 2, (0, "textures\\variant_d.dds")) };
        var shapeNames = new[] { "Body", "Skirt", "Skirt" };
        var skipped = new List<AlternateTextureSpec>();

        MatchWithDuplicates(specs, shapeNames, "Skirt", 1, skipped).Should().BeNull();
        skipped.Should().ContainSingle().Which.ShapeIndex.Should().Be(2);
    }

    [Fact]
    public void DuplicateNames_IndexMatchingNoNamesake_StillAppliesToAll()
    {
        // Block-re-sort desync: the entry names "Skirt" but its index points at the body. The index
        // is the untrustworthy field here, so neither namesake stands down — matching the behaviour
        // that shipped before this rule existed.
        var specs = new[] { Spec("Skirt", 0, (0, "textures\\variant_d.dds")) };
        var shapeNames = new[] { "Body", "Skirt", "Skirt" };

        MatchWithDuplicates(specs, shapeNames, "Skirt", 1).Should().NotBeNull();
        MatchWithDuplicates(specs, shapeNames, "Skirt", 2).Should().NotBeNull();
    }

    [Fact]
    public void DuplicateNames_EntryWithNoIndex_StillAppliesToAll()
    {
        // Nothing to disambiguate with. Dropping the entry would lose a texture the engine does
        // apply to one of them.
        var specs = new[] { Spec("Skirt", -1, (0, "textures\\variant_d.dds")) };
        var shapeNames = new[] { "Body", "Skirt", "Skirt" };

        MatchWithDuplicates(specs, shapeNames, "Skirt", 1).Should().NotBeNull();
        MatchWithDuplicates(specs, shapeNames, "Skirt", 2).Should().NotBeNull();
    }

    [Fact]
    public void DuplicateNames_DoNotDisturbAnUnrelatedUniqueName()
    {
        // The map carries only the duplicated names, so a unique shape in the same mesh matches by
        // name exactly as before even though its index disagrees.
        var specs = new[] { Spec("Body", 99, (0, "textures\\body_d.dds")) };
        var shapeNames = new[] { "Body", "Skirt", "Skirt" };

        MatchWithDuplicates(specs, shapeNames, "Body", 0).Should().NotBeNull();
    }

    [Fact]
    public void DuplicateNames_DanglingEntryStillReachesTheIndexFallback()
    {
        // The two rules compose: this entry names nothing in the mesh, so it is still eligible for
        // the plain index fallback, and duplicate names elsewhere must not block it.
        var specs = new[] { Spec("OldName", 1, (0, "textures\\variant_d.dds")) };
        var shapeNames = new[] { "Body", "Skirt", "Skirt" };

        MatchWithDuplicates(specs, shapeNames, "Skirt", 1).Should().NotBeNull();
    }
}
