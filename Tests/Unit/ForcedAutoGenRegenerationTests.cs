using System.Collections.Generic;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using NPC_Plugin_Chooser_2.View_Models;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// The forced-re-render latch behind the AG mugshot-source button
/// (VM_NpcSelectionBar.ArmForcedAutoGenRegeneration and friends).
///
/// The regression these pin: clicking AG only promoted AutoGeneration in the
/// source priority, and the renderer skips a PNG the staleness checker calls
/// fresh. Freshness is judged from stamped render SETTINGS, which say nothing
/// about the mod's asset scope — so after a user fixed a mod by adding the
/// folder holding its missing meshes/textures, AG reused (or faithfully
/// re-rendered) the same asset-less image, and only a restart picked the assets
/// up. The latch is what makes the click mean "render this now".
///
/// Two properties matter and neither is obvious from the call sites:
/// ONE-SHOT — the tile rebuild the override triggers must get its forced render,
/// but the re-kicks TriggerAsyncMugshotGeneration fires at the SAME tile objects
/// must not each cost another ~5s render; and CONSUME-ON-SUCCESS — a render
/// cancelled by startup/layout churn must NOT burn the tile's one attempt, or
/// the re-kick quietly serves the stale PNG again and the bug is back.
///
/// The latch touches only its own three fields, so the VM is built uninitialised
/// with those injected — the real constructor drags in a game install.
/// </summary>
public class ForcedAutoGenRegenerationTests
{
    private static VM_NpcSelectionBar MakeBar()
    {
        var bar = Reflect.Uninitialized<VM_NpcSelectionBar>();
        Reflect.SetField(bar, "_forcedAutoGenLock", new object());
        Reflect.SetField(bar, "_forcedAutoGenTilesServed", new HashSet<VM_NpcsMenuMugshot>());
        Reflect.SetField(bar, "_forcedAutoGenPending", false);
        return bar;
    }

    // Tiles are used purely as identity keys (VM_NpcsMenuMugshot doesn't override
    // Equals), so an uninitialised instance is a faithful stand-in.
    private static VM_NpcsMenuMugshot MakeTile() => Reflect.Uninitialized<VM_NpcsMenuMugshot>();

    // Arming runs the VM_Mods -> Settings.ModSettings sync, which throws on this
    // uninitialised instance and is swallowed by design (a sync failure must not
    // block the render). Set the field directly to isolate the latch semantics.
    private static void Arm(VM_NpcSelectionBar bar) =>
        Reflect.SetField(bar, "_forcedAutoGenPending", true);

    [Fact]
    public void NotPending_UntilArmed()
    {
        var bar = MakeBar();
        bar.IsForcedAutoGenRegenerationPending(MakeTile()).Should().BeFalse();
    }

    [Fact]
    public void Armed_IsPendingForEveryTile()
    {
        var bar = MakeBar();
        Arm(bar);

        // The whole rebuilt row must force, not just the first tile consulted.
        bar.IsForcedAutoGenRegenerationPending(MakeTile()).Should().BeTrue();
        bar.IsForcedAutoGenRegenerationPending(MakeTile()).Should().BeTrue();
    }

    [Fact]
    public void Probing_DoesNotConsume()
    {
        var bar = MakeBar();
        Arm(bar);
        var tile = MakeTile();

        // LoadInitialImageAsync probes before TryAutoGenerationSourceAsync does;
        // if the probe consumed, the render itself would never see the force.
        bar.IsForcedAutoGenRegenerationPending(tile).Should().BeTrue();
        bar.IsForcedAutoGenRegenerationPending(tile).Should().BeTrue();
    }

    [Fact]
    public void ServedTile_StopsForcing_ButOtherTilesStillDo()
    {
        var bar = MakeBar();
        Arm(bar);
        var rendered = MakeTile();
        var other = MakeTile();

        bar.MarkForcedAutoGenRegenerationServed(rendered);

        bar.IsForcedAutoGenRegenerationPending(rendered).Should().BeFalse();
        bar.IsForcedAutoGenRegenerationPending(other).Should().BeTrue();
    }

    [Fact]
    public void UnservedTile_StillForces_AfterACancelledRender()
    {
        var bar = MakeBar();
        Arm(bar);
        var tile = MakeTile();

        // A cancelled render reports Generated=false, so the caller never marks
        // it served — the re-kick must still force.
        bar.IsForcedAutoGenRegenerationPending(tile).Should().BeTrue();
        bar.IsForcedAutoGenRegenerationPending(tile).Should().BeTrue();
    }

    [Fact]
    public void Clearing_DropsPendingAndServeHistory()
    {
        var bar = MakeBar();
        Arm(bar);
        var tile = MakeTile();
        bar.MarkForcedAutoGenRegenerationServed(tile);

        Reflect.InvokeVoid(bar, "ClearForcedAutoGenRegeneration");

        bar.IsForcedAutoGenRegenerationPending(tile).Should().BeFalse();

        // Re-arming after a clear (AG toggled off, then on again) must give the
        // same tile object a fresh forced render rather than remembering it.
        Arm(bar);
        bar.IsForcedAutoGenRegenerationPending(tile).Should().BeTrue();
    }

    [Fact]
    public void ServeHistory_IsPerTileIdentity_NotValueEquality()
    {
        var bar = MakeBar();
        Arm(bar);
        var a = MakeTile();
        var b = MakeTile();

        bar.MarkForcedAutoGenRegenerationServed(a);
        bar.MarkForcedAutoGenRegenerationServed(a); // idempotent

        bar.IsForcedAutoGenRegenerationPending(a).Should().BeFalse();
        bar.IsForcedAutoGenRegenerationPending(b).Should().BeTrue();
    }
}

/// <summary>
/// The second half of the AG force decision: the latch above only ARMS a
/// re-render — a tile also has to be one the user is plausibly trying to repair.
/// This is the predicate that decides that
/// (InternalMugshotMetadata.RecordsMissingInstallableAssets, reached from
/// VM_NpcsMenuMugshot.ShouldForceAutoGenRegeneration via a PNG metadata read).
///
/// Without it an AG click re-rendered every tile in the row at seconds each,
/// including intact mugshots it could not improve. The line it draws is
/// "installable": a missing mesh or texture appears once the user adds the mod
/// folder holding it, whereas a broken physics-config link and a FaceGen/records
/// mismatch are not absent files — treating those as re-renderable would make a
/// correct render look repairable forever.
/// </summary>
public class ForcedRegenerationScopeTests
{
    private static readonly FormKey Npc = FormKey.Factory("000D67:EMCompViljaSkyrim.esp");

    private static string Json(
        IReadOnlyList<string>? missingMeshes = null,
        IReadOnlyList<string>? missingTextures = null,
        string? faceGenMismatch = null,
        IReadOnlyList<string>? physicsNotices = null,
        IReadOnlyList<string>? missingOutfitAssets = null) =>
        InternalMugshotMetadata.Build(
            Npc, new InternalMugshotSettings(),
            effectiveIncludeDefaultOutfit: false, effectiveIncludeHeadgear: false,
            effectiveOutfitIdentity: "none",
            missingMeshes: missingMeshes,
            missingTextures: missingTextures,
            faceGenMismatch: faceGenMismatch,
            physicsConfigNotices: physicsNotices,
            missingOutfitAssets: missingOutfitAssets);

    [Fact]
    public void IntactRender_DoesNotForce()
    {
        InternalMugshotMetadata.RecordsMissingInstallableAssets(Json()).Should().BeFalse();
    }

    [Fact]
    public void MissingMeshes_Force()
    {
        var json = Json(missingMeshes: new[] { @"meshes\actors\character\Chaconne\femalebody_1.nif" });
        InternalMugshotMetadata.RecordsMissingInstallableAssets(json).Should().BeTrue();
    }

    [Fact]
    public void MissingTextures_Force()
    {
        var json = Json(missingTextures: new[] { @"textures\actors\character\Chaconne\femalebody_d.dds" });
        InternalMugshotMetadata.RecordsMissingInstallableAssets(json).Should().BeTrue();
    }

    [Fact]
    public void MissingOutfitAssets_Force()
    {
        // Same user action fixes these — the folder that was never added.
        var json = Json(missingOutfitAssets: new[] { "Outfit texture not found: textures\\armor\\steel\\gauntlets_d.dds" });
        InternalMugshotMetadata.RecordsMissingInstallableAssets(json).Should().BeTrue();
    }

    [Fact]
    public void PhysicsNotices_DoNotForce()
    {
        // The render is correct; nothing the user installs changes it.
        var json = Json(physicsNotices: new[] { "SMP config not found: physics\\wig.xml" });
        InternalMugshotMetadata.RecordsMissingInstallableAssets(json).Should().BeFalse();
    }

    [Fact]
    public void FaceGenMismatch_DoesNotForce()
    {
        // A records-vs-FaceGen disagreement, not an absent file.
        var json = Json(faceGenMismatch: "FaceGen head parts differ from the NPC record");
        InternalMugshotMetadata.RecordsMissingInstallableAssets(json).Should().BeFalse();
    }

    [Fact]
    public void AbsentOrUnreadableMetadata_DoesNotForce()
    {
        // Hand-placed PNGs (no Parameters chunk) and unparseable stamps must not
        // be dragged into a re-render loop.
        InternalMugshotMetadata.RecordsMissingInstallableAssets(null).Should().BeFalse();
        InternalMugshotMetadata.RecordsMissingInstallableAssets("").Should().BeFalse();
        InternalMugshotMetadata.RecordsMissingInstallableAssets("{not json").Should().BeFalse();
    }
}
