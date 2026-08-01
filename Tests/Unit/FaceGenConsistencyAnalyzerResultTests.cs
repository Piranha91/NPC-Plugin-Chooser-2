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
        // Regression guard: the flag is forward-direction only. A .nif carrying a shape with no
        // matching head part was observed in game WITHOUT the dark-face bug (2026-07-24), and the
        // reference detector for this bug (the "Dark Face Issue Reporter" xEdit script) likewise
        // only checks that every HeadPart in the record is present in the .nif. Orphans stay
        // corroborating detail — raising the flag on them would report NPCs that render fine.
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

    // ---- Result.Kind (cause classification) -------------------------------------------------
    //
    // The classifier is what keeps the message honest: "generated against a different version"
    // is only plausible for a single-slot difference. Anything broader is a different-source
    // mismatch (a lost plugin conflict or a lost FaceGen file conflict).

    [Fact]
    public void Kind_EmptyResult_IsNone()
    {
        new FaceGenConsistencyAnalyzer.Result().Kind.Should().Be(FaceGenConsistencyAnalyzer.MismatchKind.None);
    }

    [Fact]
    public void Kind_SingleMissingNoOrphans_IsSingleHeadPartDifference()
    {
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            MissingBakedShapes = new[] { new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "Brows") },
        };
        r.Kind.Should().Be(FaceGenConsistencyAnalyzer.MismatchKind.SingleHeadPartDifference);
    }

    [Fact]
    public void Kind_SingleMissingWithSingleOrphan_IsStillSingleHeadPartDifference()
    {
        // One slot swapped (missing X / baked Y) is still a one-part difference.
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            MissingBakedShapes = new[] { new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "Brows") },
            OrphanBakedShapes = new[] { "SomeOtherBrows" },
        };
        r.Kind.Should().Be(FaceGenConsistencyAnalyzer.MismatchKind.SingleHeadPartDifference);
    }

    [Fact]
    public void Kind_SingleMissingWithManyOrphans_IsDifferentSource()
    {
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            MissingBakedShapes = new[] { new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "Brows") },
            OrphanBakedShapes = new[] { "PAN_Hair", "PAN_Hairline" },
        };
        r.Kind.Should().Be(FaceGenConsistencyAnalyzer.MismatchKind.DifferentSource);
    }

    [Fact]
    public void Kind_MultipleMissing_IsDifferentSource()
    {
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            MissingBakedShapes = new[]
            {
                new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "Hair"),
                new FaceGenConsistencyAnalyzer.HeadPartRef(HpB, "HairLine"),
            },
        };
        r.Kind.Should().Be(FaceGenConsistencyAnalyzer.MismatchKind.DifferentSource);
    }

    [Fact]
    public void Kind_UnresolvedOrNullOnly_IsBrokenHeadPartLinks()
    {
        new FaceGenConsistencyAnalyzer.Result { UnresolvedHeadParts = new[] { HpC } }
            .Kind.Should().Be(FaceGenConsistencyAnalyzer.MismatchKind.BrokenHeadPartLinks);
        new FaceGenConsistencyAnalyzer.Result { NullHeadPartLinks = 1 }
            .Kind.Should().Be(FaceGenConsistencyAnalyzer.MismatchKind.BrokenHeadPartLinks);
    }

    [Fact]
    public void Kind_OrphansOnly_IsExtraBakedShapesOnly()
    {
        new FaceGenConsistencyAnalyzer.Result { OrphanBakedShapes = new[] { "Floater" } }
            .Kind.Should().Be(FaceGenConsistencyAnalyzer.MismatchKind.ExtraBakedShapesOnly);
    }

    // ---- Result.BuildReason -----------------------------------------------------------------

    [Fact]
    public void BuildReason_NothingToReport_ReturnsEmptyString()
    {
        new FaceGenConsistencyAnalyzer.Result().BuildReason().Should().BeEmpty();
    }

    [Fact]
    public void BuildReason_OrphansOnly_ReportsNothingAtAll()
    {
        // Validation reports only what the user can see in game. A purely additive .nif produces
        // no visible defect (see HasMismatch_OrphanBakedShapesOnly_IsFalse for the evidence), so
        // it must produce no row and no tooltip — not a softened one.
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            OrphanBakedShapes = new[] { "Floater", "AnotherFloater" },
        };
        r.BuildReason().Should().BeEmpty();
    }

    [Fact]
    public void BuildReason_MixedMissingAndOrphans_StillWarnsAboutDarkFace()
    {
        // Guard the boundary: once a head part the record needs IS missing, the dark-face warning
        // and the remedies come back, orphans or not.
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            MissingBakedShapes = new[]
            {
                new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "Hair"),
                new FaceGenConsistencyAnalyzer.HeadPartRef(HpB, "HairLine"),
            },
            OrphanBakedShapes = new[] { "Floater" },
        };
        var reason = r.BuildReason();

        reason.Should().Contain("dark-face bug");
        reason.Should().Contain("Likely cause(s)");
        reason.Should().Contain("Floater");
    }

    [Fact]
    public void BuildReason_SingleMissingBakedShape_MentionsEditorIdFormKeyAndVersionMismatch()
    {
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            MissingBakedShapes = new[] { new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "MaleHeadNord") },
        };
        var reason = r.BuildReason();

        reason.Should().Contain("'MaleHeadNord'");
        reason.Should().Contain(HpA.ToString());
        reason.Should().Contain(HpA.ModKey.FileName.ToString());
        reason.Should().Contain("uses one head part that isn't in the FaceGen .nif");
        reason.Should().Contain("Version mismatch");
        // No "you can probably ignore this" reassurance — we can't tell whether the game will
        // tolerate the mismatch, so the message must not imply that it will.
        reason.Should().NotContain("ignore this");
    }

    [Fact]
    public void BuildReason_MultipleMissing_BlamesConflictsNotPluginVersion()
    {
        // Regression guard for the reported case: several unmatched head parts plus foreign
        // baked shapes must be reported as a source mismatch (lost plugin / file conflict),
        // never as "the FaceGen was generated against a different version of Skyrim.esm".
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            MissingBakedShapes = new[]
            {
                new FaceGenConsistencyAnalyzer.HeadPartRef(FormKey.Factory("051148:Skyrim.esm"), "HairFemaleNord07"),
                new FaceGenConsistencyAnalyzer.HeadPartRef(FormKey.Factory("0510BB:Skyrim.esm"), "HairLineFemaleNord07"),
            },
            OrphanBakedShapes = new[] { "PAN_AbeloneHair", "PAN_AbeloneHairline" },
        };
        var reason = r.BuildReason();

        reason.Should().NotContain("different version");
        reason.Should().Contain("different set of head parts than the NPC's .esp plugin record");
        reason.Should().Contain("Plugin conflict");
        reason.Should().Contain("Asset conflict");
        // The load-order remedies must never name the plugins that DEFINE the unmatched head
        // parts: that is usually a resource master (High Poly Head.esm), not the record winner,
        // so it read as a plugin conflict that wasn't happening. Winning Source names the winner.
        // (The headline legitimately says "not coming from the same appearance mod", so this is
        // asserted against the remedy section alone.)
        var remedies = reason[reason.IndexOf("Likely cause(s)", StringComparison.Ordinal)..];
        remedies.Should().NotContain("coming from");
        remedies.Should().NotContain("Skyrim.esm");
    }

    [Fact]
    public void BuildReason_LoadOrderRemedies_NameNoPlugins()
    {
        // The two conflict remedies are about NPC2's own output winning; naming plugins here was
        // noise at best and misleading at worst (see the regression guard above).
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            MissingBakedShapes = new[]
            {
                new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "Hair"),
                new FaceGenConsistencyAnalyzer.HeadPartRef(HpB, "HairLine"),
            },
        };
        var reason = r.BuildReason();

        reason.Should().Contain("Plugin conflict");
        reason.Should().Contain("Asset conflict");
        // 'HeadParts.esp' may still appear in the EVIDENCE list (the FormKeys), but never in the
        // remedies. Check the remedy section alone.
        var remedies = reason[reason.IndexOf("Likely cause(s)", StringComparison.Ordinal)..];
        remedies.Should().NotContain("HeadParts.esp");
        remedies.Should().NotContain("vanilla Skyrim");
    }

    [Fact]
    public void BuildReason_SelectedModScope_GivesModScopedRemedies()
    {
        // The mugshot path resolves head parts mod-scoped, so load-order remedies would be wrong.
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            MissingBakedShapes = new[]
            {
                new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "Hair"),
                new FaceGenConsistencyAnalyzer.HeadPartRef(HpB, "HairLine"),
            },
        };
        var reason = r.BuildReason(scope: FaceGenConsistencyAnalyzer.ReasonScope.SelectedMod);

        reason.Should().Contain("The mod's own .esp and .nif don't match");
        reason.Should().Contain("Run Validate Output");
        reason.Should().NotContain("load order");
    }

    [Fact]
    public void BuildReason_SelectedModScope_AllVanillaParts_PointsAtTheMissingRecord()
    {
        // Mod-scoped resolution landing on vanilla head parts means the mod supplied the mesh
        // but no matching NPC record — a version-mismatch remedy would be misleading here.
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            MissingBakedShapes = new[]
            {
                new FaceGenConsistencyAnalyzer.HeadPartRef(FormKey.Factory("051148:Skyrim.esm"), "HairFemaleNord07"),
                new FaceGenConsistencyAnalyzer.HeadPartRef(FormKey.Factory("0510BB:Skyrim.esm"), "HairLineFemaleNord07"),
            },
        };
        var reason = r.BuildReason(scope: FaceGenConsistencyAnalyzer.ReasonScope.SelectedMod);

        reason.Should().Contain("all vanilla Skyrim ones");
        reason.Should().Contain("no matching plugin record");
    }

    // ---- DeliveryFidelity: proven-faithful delivery replaces the conflict remedies ----------

    [Fact]
    public void HeadPartRef_FromRaceDefaults_DefaultsFalse_AndParticipatesInEquality()
    {
        var own = new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "ChildMouth");
        var raceDefault = new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "ChildMouth") { FromRaceDefaults = true };

        own.FromRaceDefaults.Should().BeFalse();
        raceDefault.FromRaceDefaults.Should().BeTrue();
        own.Should().NotBe(raceDefault);
    }

    [Fact]
    public void BuildReason_RaceDefaultParts_AreAnnotatedInTheEvidenceList()
    {
        // The annotation is evidence, not a remedy, so it appears regardless of fidelity.
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            MissingBakedShapes = new[]
            {
                new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "Hair"),
                new FaceGenConsistencyAnalyzer.HeadPartRef(HpB, "ChildMouth") { FromRaceDefaults = true },
            },
        };
        var reason = r.BuildReason();

        reason.Should().Contain("'ChildMouth' (" + HpB + ") (race default)");
        reason.Should().NotContain("'Hair' (" + HpA + ") (race default)");
    }

    [Fact]
    public void BuildReason_FaithfulDelivery_DropsConflictRemedies_AndBlamesTheModsOwnData()
    {
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            MissingBakedShapes = new[]
            {
                new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "Hair"),
                new FaceGenConsistencyAnalyzer.HeadPartRef(HpB, "HairLine"),
            },
        };
        var reason = r.BuildReason(
            fidelity: FaceGenConsistencyAnalyzer.DeliveryFidelity.SelectedModOwnData);

        // The delivery is proven faithful: no conflict accusations, and no "different mods"
        // inference in the headline — the mod's own files simply disagree.
        reason.Should().NotContain("may be overwritten");
        reason.Should().NotContain("not coming from the same appearance mod");
        reason.Should().Contain("Not a deployment problem");
        reason.Should().Contain("The selected mod's own files disagree with each other");
    }

    [Fact]
    public void BuildReason_FaithfulDelivery_VanillaData_NamesTheBaseGamesOwnBug()
    {
        // The verified Dawnguard-vampire case: record and mesh are both vanilla's own, and they
        // genuinely mismatch in an unmodded game. Single difference (Demon vs Vampire eyes).
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            MissingBakedShapes = new[]
            {
                new FaceGenConsistencyAnalyzer.HeadPartRef(
                    FormKey.Factory("02425E:Skyrim.esm"), "MaleEyesHumanDemon"),
            },
            OrphanBakedShapes = new[] { "MaleEyesHumanVampire" },
        };
        var reason = r.BuildReason(
            fidelity: FaceGenConsistencyAnalyzer.DeliveryFidelity.VanillaOwnData);

        reason.Should().Contain("base game's own files");
        reason.Should().Contain("unmodded game");
        reason.Should().NotContain("may be overwritten");
        reason.Should().NotContain("Version mismatch"); // the generic single-diff guess is replaced
    }

    [Fact]
    public void BuildReason_FaithfulDelivery_MeshOnly_ExplainsTheOriginRecordPairing()
    {
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            MissingBakedShapes = new[]
            {
                new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "HairMaleNord01"),
                new FaceGenConsistencyAnalyzer.HeadPartRef(HpB, "HumanBeard02"),
            },
        };
        var reason = r.BuildReason(
            fidelity: FaceGenConsistencyAnalyzer.DeliveryFidelity.SelectedModMeshOnly);

        reason.Should().Contain("supplies only FaceGen files");
        reason.Should().Contain("original plugin");
        reason.Should().NotContain("may be overwritten");
    }

    [Fact]
    public void BuildReason_FaithfulDelivery_AllRaceDefaults_NamesBothRaceSlotCauses()
    {
        // Two verified-in-the-wild patterns share this shape: RS Children (the mod's race edit is
        // merged away, so race defaults roll back to vanilla under its mesh) and Women of Unslaad
        // (the mod's record stopped listing parts its mesh was baked with, so the vanilla race
        // defaults apply). One resolution context cannot tell them apart, so the row must present
        // both — and must not accuse the load order, which is proven innocent.
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            MissingBakedShapes = new[]
            {
                new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "MaleHeadChild") { FromRaceDefaults = true },
                new FaceGenConsistencyAnalyzer.HeadPartRef(HpB, "ChildMouth") { FromRaceDefaults = true },
            },
            OrphanBakedShapes = new[] { "0RCOChildMouth" },
        };
        var reason = r.BuildReason(
            fidelity: FaceGenConsistencyAnalyzer.DeliveryFidelity.SelectedModOwnData);

        reason.Should().Contain("Every mismatched part is a RACE default");
        reason.Should().Contain("race edits are not carried over");
        reason.Should().Contain("record no longer lists");
        reason.Should().NotContain("may be overwritten");
    }

    [Fact]
    public void BuildReason_FaithfulDelivery_SingleDifference_KeepsTheRenameHint()
    {
        // One flipped slot can be a head part RENAMED by an overriding plugin (the NPC record
        // still points at the same FormKey) — that cause survives proven-faithful delivery.
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            MissingBakedShapes = new[]
            {
                new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "HairLineMaleRedguard3"),
            },
            OrphanBakedShapes = new[] { "HairLineMaleRedguard4" },
        };
        var reason = r.BuildReason(
            fidelity: FaceGenConsistencyAnalyzer.DeliveryFidelity.SelectedModOwnData);

        reason.Should().Contain("renamed head part");
        reason.Should().Contain("Not a deployment problem");
    }

    [Fact]
    public void BuildReason_Fidelity_IsIgnoredOutsideLoadOrderScope()
    {
        // The mugshot path resolves mod-scoped; fidelity is a load-order concept and must not
        // change the mod-scoped remedies.
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            MissingBakedShapes = new[]
            {
                new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "Hair"),
                new FaceGenConsistencyAnalyzer.HeadPartRef(HpB, "HairLine"),
            },
        };
        var reason = r.BuildReason(
            scope: FaceGenConsistencyAnalyzer.ReasonScope.SelectedMod,
            fidelity: FaceGenConsistencyAnalyzer.DeliveryFidelity.SelectedModOwnData);

        reason.Should().Contain("The mod's own .esp and .nif don't match");
        reason.Should().NotContain("Not a deployment problem");
    }

    [Fact]
    public void BuildReason_NullLinks_ReportsCountAndFix()
    {
        var r = new FaceGenConsistencyAnalyzer.Result { NullHeadPartLinks = 4 };
        var reason = r.BuildReason();
        reason.Should().Contain("4 empty entry(s)");
        reason.Should().Contain("clean it in xEdit");
    }

    [Fact]
    public void BuildReason_UnresolvedHeadPart_ReportsFormKeyAndNamesThePluginToInstall()
    {
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            UnresolvedHeadParts = new[] { HpC },
        };
        var reason = r.BuildReason();
        reason.Should().Contain(HpC.ToString());
        reason.Should().Contain("no plugin in your load order has them");
        reason.Should().Contain("Install and enable the mod that owns these head parts");
        reason.Should().Contain(HpC.ModKey.FileName.ToString());
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

        reason.Should().Contain("'Brows'");
        reason.Should().Contain("2 empty entry(s)");
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

    // Orphans only render as corroborating detail, so these two need a missing head part to
    // get past the "nothing the user can see" guard.
    private static readonly FaceGenConsistencyAnalyzer.HeadPartRef[] OneMissing =
        { new(HpA, "Brows") };

    [Fact]
    public void BuildReason_OrphanBakedShapes_TruncatesWithPlusNMore()
    {
        // Orphans use a different truncation suffix: ", +N more".
        var orphans = Enumerable.Range(0, 7).Select(i => "Orphan" + i).ToArray();
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            MissingBakedShapes = OneMissing,
            OrphanBakedShapes = orphans,
        };

        var reason = r.BuildReason(maxPerCategory: 2);

        reason.Should().Contain("Orphan0");
        reason.Should().Contain("Orphan1");
        reason.Should().Contain(", +5 more."); // the orphan section closes with a period
    }

    [Fact]
    public void BuildReason_OrphanBakedShapes_JoinedByCommas()
    {
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            MissingBakedShapes = OneMissing,
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
        reason.Should().StartWith("Broken head part references (a common cause of the in-game dark-face bug):");
    }

    [Fact]
    public void BuildReason_OrdersSections_EvidenceThenRemedies()
    {
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            MissingBakedShapes = new[]
            {
                new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "Hair"),
                new FaceGenConsistencyAnalyzer.HeadPartRef(HpB, "HairLine"),
            },
            OrphanBakedShapes = new[] { "Orphan1" },
        };
        var reason = r.BuildReason();

        reason.IndexOf("in the .esp but not the .nif", StringComparison.Ordinal)
            .Should().BeLessThan(reason.IndexOf("in the .nif but not the .esp", StringComparison.Ordinal));
        reason.IndexOf("in the .nif but not the .esp", StringComparison.Ordinal)
            .Should().BeLessThan(reason.IndexOf("Likely cause(s), most common first:", StringComparison.Ordinal));
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
