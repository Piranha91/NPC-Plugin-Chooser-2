using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.Models;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// The scope key a manual wig designation is stored and read under.
///
/// Under the default <c>ManualWigBlockScope = AllNpcs</c> the key is ignored entirely, which is why
/// this went unnoticed: only <c>SpecificNpc</c> compares it, and then by plain equality. The UI
/// stores a designation against the NPC the user has open
/// (<c>VM_InternalMugshotPreview.PopulateWigSelector</c> is called with the preview's own
/// <c>formKey</c>) and the patcher reads it back under the donor's key, so any third reader has to
/// use the donor's key too. The 3D preview's hair-hiding used the TERMINUS's, because its NPC record
/// arrives via <c>NpcMeshResolver.ResolveAppearanceNpcKey</c> — for a templated NPC that is a
/// different record, and the lookup missed.
///
/// These tests pin the semantics that make the donor key the right answer. The wiring itself
/// (<c>Resolve</c> threading <c>designationScopeKey</c> down to <c>ComputeWigHideHeadShapeNames</c>)
/// needs a link cache and a render context, so it is not reachable from a unit test.
/// </summary>
public class ManualWigDesignationScopeTests
{
    private const string ModName = "High Poly NPC Overhaul";
    private const string ArmaEditorId = "HighPoly_WigAA_HairFemaleNord03";

    private static readonly FormKey Donor = FormKey.Factory("0D0573:Skyrim.esm");
    private static readonly FormKey Terminus = FormKey.Factory("0132A1:Skyrim.esm");

    private static Settings NewSettings(AntlerBlockScope scope)
    {
        var settings = new Settings { ManualWigBlockScope = scope };
        settings.AddManualWigArmature(ArmaEditorId, ModName, Donor, isWig: true);
        return settings;
    }

    [Fact]
    public void SpecificNpcScope_FindsTheDesignationUnderTheKeyItWasStoredWith()
    {
        var settings = NewSettings(AntlerBlockScope.SpecificNpc);

        settings.IsManualWigArmature(ArmaEditorId, ModName, Donor).Should().BeTrue();
    }

    [Fact]
    public void SpecificNpcScope_MissesItUnderTheTerminusKey()
    {
        // This is the whole defect: same designation, same mod, looked up under the NPC the
        // renderer had advanced to. The user's marking silently does nothing in the preview while
        // still applying in the patch.
        var settings = NewSettings(AntlerBlockScope.SpecificNpc);

        settings.IsManualWigArmature(ArmaEditorId, ModName, Terminus).Should().BeFalse();
    }

    [Fact]
    public void AllNpcsScope_IgnoresTheKeyEntirely()
    {
        // Why this stayed invisible: on the default setting both keys answer the same.
        var settings = NewSettings(AntlerBlockScope.AllNpcs);

        settings.IsManualWigArmature(ArmaEditorId, ModName, Donor).Should().BeTrue();
        settings.IsManualWigArmature(ArmaEditorId, ModName, Terminus).Should().BeTrue();
    }

    [Fact]
    public void SameModScope_AlsoIgnoresTheKey()
    {
        var settings = NewSettings(AntlerBlockScope.SameMod);

        settings.IsManualWigArmature(ArmaEditorId, ModName, Terminus).Should().BeTrue();
        settings.IsManualWigArmature(ArmaEditorId, "Some Other Mod", Terminus).Should().BeFalse();
    }

    [Fact]
    public void SpecificNpcScope_AppliesToTheVetoListTheSameWay()
    {
        // The not-a-wig list shares IsInManualWigList, so a scope-key mismatch would resurrect a
        // vetoed false positive rather than merely dropping a designation.
        var settings = new Settings { ManualWigBlockScope = AntlerBlockScope.SpecificNpc };
        settings.AddManualWigArmature(ArmaEditorId, ModName, Donor, isWig: false);

        settings.IsManualNonWigArmature(ArmaEditorId, ModName, Donor).Should().BeTrue();
        settings.IsManualNonWigArmature(ArmaEditorId, ModName, Terminus).Should().BeFalse();
    }
}
