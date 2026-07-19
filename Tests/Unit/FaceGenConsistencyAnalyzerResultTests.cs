using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// Pure, deterministic surface of <see cref="FaceGenConsistencyAnalyzer"/>: the nested
/// <c>HeadPartRef</c> record-struct, the <c>Result</c> class (<c>HasMismatch</c> truth table
/// + <c>BuildReason</c> string formatting/truncation), and the private static
/// <c>IsGenericNode</c> classifier (exercised via <see cref="Reflect"/>).
///
/// These types are built directly in memory — no NIF parsing, no Skyrim install, no clock or
/// network. The <see cref="FaceGenConsistencyAnalyzer.Analyze"/> entry point and the private
/// <c>GetSurvey</c> path are deliberately out of scope here (they require a real FaceGen .nif
/// and a wired <c>NifMeshBuilder</c>); see the NOTE at the bottom of this file.
/// </summary>
public class FaceGenConsistencyAnalyzerResultTests
{
    private static readonly FormKey HpA = FormKey.Factory("000801:HeadParts.esp");
    private static readonly FormKey HpB = FormKey.Factory("000802:HeadParts.esp");
    private static readonly FormKey HpC = FormKey.Factory("000803:Other.esp");

    // ---- HeadPartRef (readonly record struct) -----------------------------------------------

    [Fact]
    public void HeadPartRef_ExposesConstructorArgsAsProperties()
    {
        var r = new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "MaleHeadNord");
        r.FormKey.Should().Be(HpA);
        r.EditorId.Should().Be("MaleHeadNord");
    }

    [Fact]
    public void HeadPartRef_Deconstructs()
    {
        var r = new FaceGenConsistencyAnalyzer.HeadPartRef(HpB, "MouthHumanoidDefault");
        var (fk, edid) = r;
        fk.Should().Be(HpB);
        edid.Should().Be("MouthHumanoidDefault");
    }

    [Fact]
    public void HeadPartRef_ValueEquality_SameFieldsAreEqual()
    {
        var a = new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "Eyes");
        var b = new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "Eyes");
        a.Should().Be(b);
        (a == b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void HeadPartRef_ValueEquality_DiffersOnEitherField()
    {
        var baseline = new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "Eyes");
        var diffKey = new FaceGenConsistencyAnalyzer.HeadPartRef(HpB, "Eyes");
        var diffEdid = new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "Brows");

        baseline.Should().NotBe(diffKey);
        baseline.Should().NotBe(diffEdid);
        (baseline != diffKey).Should().BeTrue();
    }

    [Fact]
    public void HeadPartRef_EditorIdComparison_IsCaseSensitive()
    {
        // The record uses the default string comparer (ordinal, case-sensitive) for equality.
        var lower = new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "eyes");
        var upper = new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "Eyes");
        lower.Should().NotBe(upper);
    }

    // ---- Result.HasMismatch truth table -----------------------------------------------------

    [Fact]
    public void HasMismatch_EmptyResult_IsFalse()
    {
        new FaceGenConsistencyAnalyzer.Result().HasMismatch.Should().BeFalse();
    }

    [Fact]
    public void HasMismatch_MissingBakedShape_IsTrue()
    {
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            MissingBakedShapes = new[] { new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "Brows") },
        };
        r.HasMismatch.Should().BeTrue();
    }

    [Fact]
    public void HasMismatch_UnresolvedHeadPart_IsTrue()
    {
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            UnresolvedHeadParts = new[] { HpC },
        };
        r.HasMismatch.Should().BeTrue();
    }

    [Fact]
    public void HasMismatch_NullHeadPartLinks_IsTrue()
    {
        var r = new FaceGenConsistencyAnalyzer.Result { NullHeadPartLinks = 1 };
        r.HasMismatch.Should().BeTrue();
    }

    [Fact]
    public void HasMismatch_OrphanBakedShapesOnly_IsFalse()
    {
        // Regression guard: orphan baked shapes are corroborating detail only and must NOT,
        // on their own, raise the high-confidence flag (a hand-named custom shape would
        // otherwise false-positive).
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            OrphanBakedShapes = new[] { "SomeCustomShape", "AnotherOne" },
        };
        r.HasMismatch.Should().BeFalse();
    }

    [Fact]
    public void HasMismatch_NullHeadPartLinksZero_DoesNotTrigger()
    {
        var r = new FaceGenConsistencyAnalyzer.Result { NullHeadPartLinks = 0 };
        r.HasMismatch.Should().BeFalse();
    }

    [Fact]
    public void HasMismatch_AnyTriggerAmongMany_IsTrue()
    {
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            OrphanBakedShapes = new[] { "X" },
            NullHeadPartLinks = 3,
        };
        r.HasMismatch.Should().BeTrue();
    }

    // ---- Result default property values -----------------------------------------------------

    [Fact]
    public void Result_Defaults_AreEmptyNonNullCollections()
    {
        var r = new FaceGenConsistencyAnalyzer.Result();
        r.NifParsed.Should().BeFalse();
        r.NifError.Should().BeNull();
        r.BakedShapeCount.Should().Be(0);
        r.ResolvedHeadPartCount.Should().Be(0);
        r.NullHeadPartLinks.Should().Be(0);
        r.MissingBakedShapes.Should().NotBeNull().And.BeEmpty();
        r.OrphanBakedShapes.Should().NotBeNull().And.BeEmpty();
        r.UnresolvedHeadParts.Should().NotBeNull().And.BeEmpty();
    }

    // ---- Result.BuildReason -----------------------------------------------------------------

    [Fact]
    public void BuildReason_NothingToReport_ReturnsEmptyString()
    {
        new FaceGenConsistencyAnalyzer.Result().BuildReason().Should().BeEmpty();
    }

    [Fact]
    public void BuildReason_OrphansOnly_StillProducesText()
    {
        // BuildReason emits even when !HasMismatch, as long as there are orphan baked shapes.
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            OrphanBakedShapes = new[] { "Floater" },
        };
        var reason = r.BuildReason();
        reason.Should().NotBeEmpty();
        reason.Should().StartWith("FaceGen / plugin mismatch");
        reason.Should().Contain("baked shape(s) with no matching head part");
        reason.Should().Contain("Floater");
    }

    [Fact]
    public void BuildReason_MissingBakedShape_MentionsEditorIdFormKeyAndPlugin()
    {
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            MissingBakedShapes = new[] { new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "MaleHeadNord") },
        };
        var reason = r.BuildReason();

        reason.Should().Contain("Head part 'MaleHeadNord'");
        reason.Should().Contain(HpA.ToString());
        reason.Should().Contain(HpA.ModKey.FileName.ToString());
        reason.Should().Contain("no matching shape in the FaceGen mesh");
    }

    [Fact]
    public void BuildReason_NullLinks_ReportsCount()
    {
        var r = new FaceGenConsistencyAnalyzer.Result { NullHeadPartLinks = 4 };
        r.BuildReason().Should().Contain("4 null head part reference(s)");
    }

    [Fact]
    public void BuildReason_UnresolvedHeadPart_ReportsFormKeyAndContext()
    {
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            UnresolvedHeadParts = new[] { HpC },
        };
        var reason = r.BuildReason();
        reason.Should().Contain(HpC.ToString());
        reason.Should().Contain("does not resolve in the current load order");
    }

    [Fact]
    public void BuildReason_AllCategories_AppearTogether()
    {
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            MissingBakedShapes = new[] { new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "Brows") },
            NullHeadPartLinks = 2,
            UnresolvedHeadParts = new[] { HpC },
            OrphanBakedShapes = new[] { "Orphan1" },
        };
        var reason = r.BuildReason();

        reason.Should().Contain("Head part 'Brows'");
        reason.Should().Contain("2 null head part reference(s)");
        reason.Should().Contain(HpC.ToString());
        reason.Should().Contain("Orphan1");
    }

    [Fact]
    public void BuildReason_MissingBakedShapes_TruncatesWithAndNMore()
    {
        // 10 missing parts, cap of 3 -> 3 shown + an "…and 7 more" tail line.
        var missing = Enumerable.Range(0, 10)
            .Select(i => new FaceGenConsistencyAnalyzer.HeadPartRef(
                FormKey.Factory($"00{i:D4}:HeadParts.esp"), "HP_" + i))
            .ToArray();
        var r = new FaceGenConsistencyAnalyzer.Result { MissingBakedShapes = missing };

        var reason = r.BuildReason(maxPerCategory: 3);

        reason.Should().Contain("HP_0");
        reason.Should().Contain("HP_2");
        reason.Should().NotContain("HP_3"); // beyond the cap, only summarized
        reason.Should().Contain("…and 7 more head part(s) with no baked shape.");
    }

    [Fact]
    public void BuildReason_MissingBakedShapes_ExactlyAtCap_NoTruncationTail()
    {
        // Count == cap: every entry shown, no "…and N more" tail (the tail uses strict >).
        var missing = Enumerable.Range(0, 3)
            .Select(i => new FaceGenConsistencyAnalyzer.HeadPartRef(
                FormKey.Factory($"00{i:D4}:HeadParts.esp"), "HP_" + i))
            .ToArray();
        var r = new FaceGenConsistencyAnalyzer.Result { MissingBakedShapes = missing };

        var reason = r.BuildReason(maxPerCategory: 3);

        reason.Should().Contain("HP_0").And.Contain("HP_1").And.Contain("HP_2");
        reason.Should().NotContain("more head part(s) with no baked shape");
    }

    [Fact]
    public void BuildReason_UnresolvedHeadParts_TruncatesWithAndNMore()
    {
        var unresolved = Enumerable.Range(0, 6)
            .Select(i => FormKey.Factory($"00{i:D4}:Missing.esp"))
            .ToArray();
        var r = new FaceGenConsistencyAnalyzer.Result { UnresolvedHeadParts = unresolved };

        var reason = r.BuildReason(maxPerCategory: 2);

        reason.Should().Contain("…and 4 more unresolved head part(s).");
    }

    [Fact]
    public void BuildReason_OrphanBakedShapes_TruncatesWithPlusNMore()
    {
        // Orphans use a different truncation suffix: ", +N more".
        var orphans = Enumerable.Range(0, 7).Select(i => "Orphan" + i).ToArray();
        var r = new FaceGenConsistencyAnalyzer.Result { OrphanBakedShapes = orphans };

        var reason = r.BuildReason(maxPerCategory: 2);

        reason.Should().Contain("Orphan0");
        reason.Should().Contain("Orphan1");
        reason.Should().Contain(", +5 more");
        reason.Should().EndWith("."); // the orphan section closes with a period
    }

    [Fact]
    public void BuildReason_OrphanBakedShapes_JoinedByCommas()
    {
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            OrphanBakedShapes = new[] { "A", "B", "C" },
        };
        // Under the default cap (8) all three are shown, comma-separated, no "+N more".
        var reason = r.BuildReason();
        reason.Should().Contain("A, B, C.");
        reason.Should().NotContain("+");
    }

    [Fact]
    public void BuildReason_HeaderAlwaysPrecedesDetail()
    {
        var r = new FaceGenConsistencyAnalyzer.Result { NullHeadPartLinks = 1 };
        var reason = r.BuildReason();
        reason.Should().StartWith("FaceGen / plugin mismatch (a common cause of the in-game dark-face bug):");
    }

    [Fact]
    public void BuildReason_DefaultCap_IsEight()
    {
        // 9 missing parts with the DEFAULT cap (8) -> "…and 1 more".
        var missing = Enumerable.Range(0, 9)
            .Select(i => new FaceGenConsistencyAnalyzer.HeadPartRef(
                FormKey.Factory($"00{i:D4}:HeadParts.esp"), "HP_" + i))
            .ToArray();
        var r = new FaceGenConsistencyAnalyzer.Result { MissingBakedShapes = missing };

        r.BuildReason().Should().Contain("…and 1 more head part(s) with no baked shape.");
    }

    // ---- IsGenericNode (private static, via Reflect) ----------------------------------------

    private static bool IsGenericNode(string shapeName, string? primaryHeadShapeName) =>
        Reflect.InvokeStatic<FaceGenConsistencyAnalyzer, bool>(
            "IsGenericNode", shapeName, primaryHeadShapeName);

    [Fact]
    public void IsGenericNode_MatchesPrimaryHeadShapeName_CaseInsensitive()
    {
        IsGenericNode("MyHeadShape", "myheadshape").Should().BeTrue();
    }

    [Fact]
    public void IsGenericNode_NpcHeadSubstring_IsGeneric()
    {
        IsGenericNode("NPC Head [Head]", null).Should().BeTrue();
        IsGenericNode("Some NPC Head marker", null).Should().BeTrue();
        IsGenericNode("npc head", null).Should().BeTrue(); // substring match is case-insensitive
    }

    [Fact]
    public void IsGenericNode_BsFaceGenPrefix_IsGeneric()
    {
        IsGenericNode("BSFaceGenNiNodeSkinned", null).Should().BeTrue();
        IsGenericNode("bsfacegenfoo", null).Should().BeTrue(); // prefix match is case-insensitive
    }

    [Fact]
    public void IsGenericNode_FaceGenPrefix_IsGeneric()
    {
        IsGenericNode("FaceGenSomething", null).Should().BeTrue();
        IsGenericNode("facegen", null).Should().BeTrue();
    }

    [Fact]
    public void IsGenericNode_OrdinaryHeadPartName_IsNotGeneric()
    {
        IsGenericNode("MaleHeadNord", null).Should().BeFalse();
        IsGenericNode("Brows", "MaleHeadNord").Should().BeFalse();
    }

    [Fact]
    public void IsGenericNode_NullPrimary_FallsThroughToPrefixChecks()
    {
        // A null primaryHeadShapeName must not throw; classification proceeds on the name alone.
        IsGenericNode("OrdinaryShape", null).Should().BeFalse();
        IsGenericNode("FaceGenHead", null).Should().BeTrue();
    }

    [Fact]
    public void IsGenericNode_EmptyPrimary_DoesNotMatchEmptyShape()
    {
        // Guard: an empty primary name must not make an arbitrary shape "primary" by equality.
        IsGenericNode("RealHeadPart", "").Should().BeFalse();
    }

    [Theory]
    [InlineData("NPC Head", null, true)]
    [InlineData("BSFaceGenNiNode", null, true)]
    [InlineData("FaceGenNode", null, true)]
    [InlineData("Hair_Long", null, false)]
    [InlineData("Eyes", "Eyes", true)]   // matches primary
    [InlineData("Eyes", "Brows", false)] // does not match primary, no prefix/substring
    public void IsGenericNode_TruthTable(string shapeName, string? primary, bool expected)
    {
        IsGenericNode(shapeName, primary).Should().Be(expected);
    }

    // ---- CollectShapeNamesOfType (public static; in-memory Mutagen records) -----------------
    //
    // Feeds ResolvedNpcMeshPaths.EyeShapeNames — the renderer's authoritative IsEye input.
    // Motivating case: FoxGlove Auri's eyeball is an ENVMAP-typed shape named "FoxGloveEyeMesh"
    // (singular), which evades the renderer's plural-"Eyes" name heuristic and received
    // eye-socket SSAO until classified via its HeadPart record here.

    private static Func<FormKey, IHeadPartGetter?> Resolver(params HeadPart[] parts)
    {
        var map = parts.ToDictionary(p => p.FormKey, p => (IHeadPartGetter)p);
        return fk => map.TryGetValue(fk, out var hp) ? hp : null;
    }

    private static HeadPart NewHeadPart(
        SkyrimMod mod, string editorId, HeadPart.TypeEnum? type)
    {
        var hp = mod.HeadParts.AddNew();
        hp.EditorID = editorId;
        hp.Type = type;
        return hp;
    }

    [Fact]
    public void CollectShapeNamesOfType_CollectsTypedPartAndItsExtraParts_ExcludesOtherTypes()
    {
        var mod = MutagenFixtures.NewMod("Test.esp");
        var eyes = NewHeadPart(mod, "FoxGloveEyeMesh", HeadPart.TypeEnum.Eyes);
        var extra = NewHeadPart(mod, "FoxGloveEyeExtra", null); // Extra Parts are typically untyped
        var hair = NewHeadPart(mod, "HairShape", HeadPart.TypeEnum.Hair);
        eyes.ExtraParts.Add(extra.FormKey.ToLink<IHeadPartGetter>());

        var npc = MutagenFixtures.NewNpc(mod);
        npc.HeadParts.Add(eyes.FormKey.ToLink<IHeadPartGetter>());
        npc.HeadParts.Add(hair.FormKey.ToLink<IHeadPartGetter>());

        var names = FaceGenConsistencyAnalyzer.CollectShapeNamesOfType(
            npc, Resolver(eyes, extra, hair), _ => null, HeadPart.TypeEnum.Eyes);

        names.Should().BeEquivalentTo(new[] { "FoxGloveEyeMesh", "FoxGloveEyeExtra" });
        names.Contains("foxgloveeyemesh").Should().BeTrue("shape-name reconciliation is case-insensitive");
    }

    [Fact]
    public void CollectShapeNamesOfType_FallsBackToRaceDefault_WhenNpcLacksSlot()
    {
        var mod = MutagenFixtures.NewMod("Test.esp");
        var raceEyes = NewHeadPart(mod, "RaceEyesDefault", HeadPart.TypeEnum.Eyes);
        var race = MutagenFixtures.NewRace(mod, "TestRace");
        var headData = new HeadData();
        var hpRef = new HeadPartReference();
        hpRef.Head.SetTo(raceEyes.FormKey);
        headData.HeadParts.Add(hpRef);
        race.HeadData = new GenderedItem<HeadData?>(headData, null);

        var npc = MutagenFixtures.NewNpc(mod, race: race); // male by default, no own eyes

        var names = FaceGenConsistencyAnalyzer.CollectShapeNamesOfType(
            npc, Resolver(raceEyes), _ => race, HeadPart.TypeEnum.Eyes);

        names.Should().BeEquivalentTo(new[] { "RaceEyesDefault" });
    }

    [Fact]
    public void CollectShapeNamesOfType_SkipsRaceDefault_WhenNpcOccupiesSlot()
    {
        var mod = MutagenFixtures.NewMod("Test.esp");
        var npcEyes = NewHeadPart(mod, "NpcEyes", HeadPart.TypeEnum.Eyes);
        var raceEyes = NewHeadPart(mod, "RaceEyesDefault", HeadPart.TypeEnum.Eyes);
        var race = MutagenFixtures.NewRace(mod, "TestRace");
        var headData = new HeadData();
        var hpRef = new HeadPartReference();
        hpRef.Head.SetTo(raceEyes.FormKey);
        headData.HeadParts.Add(hpRef);
        race.HeadData = new GenderedItem<HeadData?>(headData, null);

        var npc = MutagenFixtures.NewNpc(mod, race: race);
        npc.HeadParts.Add(npcEyes.FormKey.ToLink<IHeadPartGetter>());

        var names = FaceGenConsistencyAnalyzer.CollectShapeNamesOfType(
            npc, Resolver(npcEyes, raceEyes), _ => race, HeadPart.TypeEnum.Eyes);

        names.Should().BeEquivalentTo(new[] { "NpcEyes" });
    }

    [Fact]
    public void CollectShapeNamesOfType_CircularExtraParts_Terminates()
    {
        var mod = MutagenFixtures.NewMod("Test.esp");
        var eyes = NewHeadPart(mod, "LoopingEyes", HeadPart.TypeEnum.Eyes);
        eyes.ExtraParts.Add(eyes.FormKey.ToLink<IHeadPartGetter>()); // self-referencing Extra Part

        var npc = MutagenFixtures.NewNpc(mod);
        npc.HeadParts.Add(eyes.FormKey.ToLink<IHeadPartGetter>());

        var names = FaceGenConsistencyAnalyzer.CollectShapeNamesOfType(
            npc, Resolver(eyes), _ => null, HeadPart.TypeEnum.Eyes);

        names.Should().BeEquivalentTo(new[] { "LoopingEyes" });
    }

    [Fact]
    public void CollectShapeNamesOfType_UnresolvableAndNullLinks_AreSkipped()
    {
        var mod = MutagenFixtures.NewMod("Test.esp");
        var eyes = NewHeadPart(mod, "GoodEyes", HeadPart.TypeEnum.Eyes);

        var npc = MutagenFixtures.NewNpc(mod);
        npc.HeadParts.Add(eyes.FormKey.ToLink<IHeadPartGetter>());
        npc.HeadParts.Add(MutagenFixtures.Fk("0DEAD0:Missing.esp").ToLink<IHeadPartGetter>());

        var names = FaceGenConsistencyAnalyzer.CollectShapeNamesOfType(
            npc, Resolver(eyes), _ => null, HeadPart.TypeEnum.Eyes);

        names.Should().BeEquivalentTo(new[] { "GoodEyes" });
    }

    // NOTE: FaceGenConsistencyAnalyzer.Analyze / GetSurvey / CachedSurvey not covered:
    // they require a real FaceGen .nif parsed by NifMeshBuilder (from CharacterViewer.Rendering)
    // and a constructed CharacterPreviewCache — i.e. live rendering assets unavailable offline.
    // Those belong to the integration wave. The pure Result/HeadPartRef/IsGenericNode surface
    // exercised above carries the deterministic logic.
}
