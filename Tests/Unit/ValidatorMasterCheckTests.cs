using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// The two env-free seams of <see cref="Validator"/>'s pre-run master check.
///
/// <para>Both existed to stop a selection that the patcher cannot actually write, and both had a
/// hole that let the motivating failure through:</para>
///
/// <list type="bullet">
/// <item><c>ResolvePatcherSourcePlugin</c> — screening used to vet the FIRST plugin carrying the
/// record while the patcher uses the LAST, so with several candidates it could clear a selection
/// whose real source has a missing master.</item>
/// <item><c>IsMasterSatisfied</c> — a master belonging to the same mod entry was accepted
/// unconditionally. That is only safe when that plugin's records are MERGED into the output; left
/// as references they point at a plugin absent from the load order, and Mutagen fails the whole
/// save at the end of the run.</item>
/// </list>
/// </summary>
public class ValidatorMasterCheckTests
{
    private static readonly ModKey Base = MutagenFixtures.Mk("BanditWar.esp");
    private static readonly ModKey Patch = MutagenFixtures.Mk("BanditWar - ProjectJaKhaJay.esp");
    private static readonly ModKey Resource = MutagenFixtures.Mk("ProjectJaKhaJay.esp");
    private static readonly ModKey Vanilla = MutagenFixtures.Mk("Skyrim.esm");
    private static readonly FormKey SomeNpc = FormKey.Factory("000801:BanditWar.esp");

    private static ModSetting Mod(bool mergeIn, ModKey[] plugins, ModKey[]? resourceOnly = null,
        Dictionary<ModKey, bool>? overrides = null) => new()
    {
        DisplayName = "Lawless",
        MergeInDependencyRecords = mergeIn,
        CorrespondingModKeys = plugins.ToList(),
        ResourceOnlyModKeys = new HashSet<ModKey>(resourceOnly ?? new ModKey[0]),
        NpcFormKeys = new HashSet<FormKey> { SomeNpc },
        PluginMergeInOverrides = overrides ?? new Dictionary<ModKey, bool>(),
    };

    private static ModKey? ResolveSource(ModSetting mod, params ModKey[] available) =>
        Reflect.InvokeStatic<ModKey?>(typeof(Validator), "ResolvePatcherSourcePlugin", mod, available.ToList());

    private static (bool Ok, string Detail) IsSatisfied(ModKey master, ModSetting mod,
        IEnumerable<ModKey>? loadOrder = null, IEnumerable<ModKey>? implicits = null,
        IReadOnlyDictionary<ModKey, ModSetting>? index = null)
    {
        var args = new object?[]
        {
            master, mod,
            (loadOrder ?? Enumerable.Empty<ModKey>()).ToList(),
            new HashSet<ModKey>(implicits ?? Enumerable.Empty<ModKey>()),
            index ?? new Dictionary<ModKey, ModSetting>(),
            null,
        };
        var ok = Reflect.InvokeStatic<bool>(typeof(Validator), "IsMasterSatisfied", args);
        return (ok, (string)args[5]!);
    }

    // ── ResolvePatcherSourcePlugin ───────────────────────────────────────────────────────

    [Fact]
    public void SourcePlugin_MatchesThePatchersLastWinsWalk()
    {
        var mod = Mod(mergeIn: false, new[] { Base, Patch });

        // Both carry the record; the patcher walks CorrespondingModKeys bottom-up, so Patch wins.
        ResolveSource(mod, Base, Patch).Should().Be(Patch);
    }

    [Fact]
    public void SourcePlugin_IsIndependentOfTheAvailableListOrder()
    {
        var mod = Mod(mergeIn: false, new[] { Base, Patch });

        // Priority comes from CorrespondingModKeys, not from however the analysis happened to
        // append to AvailablePluginsForNpcs.
        ResolveSource(mod, Patch, Base).Should().Be(Patch);
    }

    [Fact]
    public void SourcePlugin_SkipsResourceOnlyPluginsLikeThePatcherDoes()
    {
        var mod = Mod(mergeIn: false, new[] { Base, Patch, Resource }, resourceOnly: new[] { Resource });

        ResolveSource(mod, Base, Patch, Resource).Should().Be(Patch);
    }

    [Fact]
    public void SourcePlugin_OnlyConsidersPluginsThatCarryTheRecord()
    {
        var mod = Mod(mergeIn: false, new[] { Base, Patch });

        ResolveSource(mod, Base).Should().Be(Base, "Patch doesn't carry this NPC's record");
    }

    [Fact]
    public void SourcePlugin_FallsBackWhenAnalysisDataIsStale()
    {
        // Candidate isn't listed in CorrespondingModKeys at all — check something rather than
        // silently skipping the master check.
        var mod = Mod(mergeIn: false, new[] { Base });

        ResolveSource(mod, Patch).Should().Be(Patch);
    }

    [Fact]
    public void SourcePlugin_ReturnsTheNullKeyWhenNothingCarriesTheRecord()
    {
        // ModKey is a struct, so the fallback yields ModKey.Null rather than a null ModKey?.
        // The caller gates on `!sourcePlugin.Value.IsNull`, so this reads as "no plugin to check"
        // and the master check is skipped — the same handling as before this method existed.
        var resolved = ResolveSource(Mod(mergeIn: false, new[] { Base }));

        resolved.HasValue.Should().BeTrue();
        resolved!.Value.IsNull.Should().BeTrue();
    }

    // ── IsMasterSatisfied ────────────────────────────────────────────────────────────────

    [Fact]
    public void Master_InLoadOrder_IsSatisfied()
    {
        var mod = Mod(mergeIn: false, new[] { Base });

        IsSatisfied(Vanilla, mod, loadOrder: new[] { Vanilla }).Ok.Should().BeTrue();
    }

    [Fact]
    public void Master_ImplicitlyActive_IsSatisfied()
    {
        var mod = Mod(mergeIn: false, new[] { Base });

        IsSatisfied(Vanilla, mod, implicits: new[] { Vanilla }).Ok.Should().BeTrue();
    }

    [Fact]
    public void Master_InSameModEntryAndMerging_IsSatisfied()
    {
        // The resource plugin is absent from the load order, but its records are copied into the
        // output, so nothing is left referencing it.
        var mod = Mod(mergeIn: false, new[] { Base, Resource }, resourceOnly: new[] { Resource });

        IsSatisfied(Resource, mod).Ok.Should().BeTrue();
    }

    [Fact]
    public void Master_InSameModEntryButNotMerging_IsRejectedWithAnActionableReason()
    {
        // This is the hole: previously accepted purely for being a sibling plugin, then the save
        // failed at the end of the run with an opaque Mutagen error.
        var mod = Mod(mergeIn: false, new[] { Base, Resource }, resourceOnly: new[] { Resource },
            overrides: new Dictionary<ModKey, bool> { [Resource] = false });

        var (ok, detail) = IsSatisfied(Resource, mod);

        ok.Should().BeFalse();
        detail.Should().Contain("Merge In").And.Contain(Resource.FileName.String);
    }

    [Fact]
    public void Master_ThatIsANonMergingNpcSibling_IsRejected()
    {
        // A sibling NPC plugin with merge off is the same hazard: not in the load order, records
        // not copied.
        var mod = Mod(mergeIn: false, new[] { Base, Patch });

        IsSatisfied(Patch, mod).Ok.Should().BeFalse();
    }

    [Fact]
    public void Master_ThatIsAMergingNpcSibling_IsSatisfied()
    {
        var mod = Mod(mergeIn: true, new[] { Base, Patch });

        IsSatisfied(Patch, mod).Ok.Should().BeTrue();
    }

    [Fact]
    public void Master_UnknownEntirely_IsRejected()
    {
        var mod = Mod(mergeIn: true, new[] { Base });

        var (ok, detail) = IsSatisfied(Resource, mod);

        ok.Should().BeFalse();
        detail.Should().BeEmpty("an unrelated missing master needs no per-plugin merge advice");
    }

    [Fact]
    public void Master_InheritedMergeFromOwningModEntry_DecidesTheVerdict()
    {
        var mod = Mod(mergeIn: false, new[] { Base, Resource }, resourceOnly: new[] { Resource });

        var mergingOwner = new ModSetting
        {
            DisplayName = "Project ja-Kha'jay",
            MergeInDependencyRecords = true,
            CorrespondingModKeys = new List<ModKey> { Resource },
            NpcFormKeys = new HashSet<FormKey> { FormKey.Factory("0008C4:ProjectJaKhaJay.esp") },
        };
        var nonMergingOwner = new ModSetting
        {
            DisplayName = "Project ja-Kha'jay",
            MergeInDependencyRecords = false,
            CorrespondingModKeys = new List<ModKey> { Resource },
            NpcFormKeys = new HashSet<FormKey> { FormKey.Factory("0008C4:ProjectJaKhaJay.esp") },
        };

        IsSatisfied(Resource, mod,
            index: MergeEligibility.BuildNpcProvidingOwnerIndex(new[] { mergingOwner })).Ok.Should().BeTrue();

        IsSatisfied(Resource, mod,
            index: MergeEligibility.BuildNpcProvidingOwnerIndex(new[] { nonMergingOwner })).Ok.Should().BeFalse();
    }

    [Fact]
    public void MotivatingCase_NowScreensClean()
    {
        // Lawless: NPC plugins in the load order, resource plugin not, owning entry merges it.
        var lawless = Mod(mergeIn: false, new[] { Base, Patch, Resource }, resourceOnly: new[] { Resource });
        var jaKhajay = new ModSetting
        {
            DisplayName = "Project ja-Kha'jay- Khajiit Diversity Overhaul",
            MergeInDependencyRecords = true,
            CorrespondingModKeys = new List<ModKey> { Resource },
            NpcFormKeys = new HashSet<FormKey> { FormKey.Factory("0008C4:ProjectJaKhaJay.esp") },
        };
        var index = MergeEligibility.BuildNpcProvidingOwnerIndex(new[] { lawless, jaKhajay });
        var loadOrder = new[] { Vanilla, Base, Patch };

        ResolveSource(lawless, Base, Patch).Should().Be(Patch);
        IsSatisfied(Vanilla, lawless, loadOrder, index: index).Ok.Should().BeTrue();
        IsSatisfied(Resource, lawless, loadOrder, index: index).Ok.Should().BeTrue();
    }
}
