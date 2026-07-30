using System.Collections.Generic;
using System.Linq;
using CharacterViewer.Rendering;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// Asset-resolution scope chain construction (NpcMeshResolver.BuildResolutionScopes).
///
/// The regression these pin: the origin scope — the mod that ORIGINALLY added the
/// NPC — only materialises when the caller passes the NPC's FormKey, and that
/// parameter is optional. Three of the four render paths silently omitted it, so
/// the live preview resolved against vanilla + the previewed mod only and lost
/// every asset owned by the originating mod (face tints, outfit meshes) while the
/// mugshot of the same NPC found them. An appearance overhaul layered on a
/// content mod (an overhaul for Glenmoril's NPCs, say) is exactly that shape.
///
/// BuildResolutionScopes touches only _settings and _env, and the
/// ActiveSelection wrapper adds _consistency, so the resolver is built
/// uninitialised with just those three fields injected — the other five
/// constructor dependencies would drag in a game install.
/// </summary>
public class NpcMeshResolverScopeTests
{
    private const string DataFolder = @"C:\Game\Data";
    private const string OriginFolder = @"S:\mods\Content Mod";
    private const string OverhaulFolder = @"S:\mods\Appearance Overhaul";

    private static readonly ModKey ContentKey = ModKey.FromNameAndExtension("Content.esm");
    private static readonly ModKey OverhaulKey = ModKey.FromNameAndExtension("Overhaul.esp");

    // An NPC added by the content mod, whose appearance the overhaul replaces.
    private static readonly FormKey NpcInContentMod = FormKey.Factory("2D49CE:Content.esm");

    private sealed class Fixture
    {
        public NpcMeshResolver Resolver = null!;
        public Settings Settings = null!;
        public ModSetting Origin = null!;
        public ModSetting Overhaul = null!;
        public Dictionary<FormKey, (string ModName, FormKey NpcFormKey)> Selections = null!;
    }

    private static Fixture BuildFixture()
    {
        var origin = new ModSetting
        {
            DisplayName = "Content Mod",
            CorrespondingModKeys = new List<ModKey> { ContentKey },
            CorrespondingFolderPaths = new List<string> { OriginFolder },
        };
        var overhaul = new ModSetting
        {
            DisplayName = "Appearance Overhaul",
            CorrespondingModKeys = new List<ModKey> { OverhaulKey },
            CorrespondingFolderPaths = new List<string> { OverhaulFolder },
        };

        var settings = new Settings
        {
            ModSettings = new List<ModSetting> { origin, overhaul },
        };

        // Uninitialised environment: DataFolderPath is a private-set property and
        // CreationClubPlugins has no initialiser without a constructor run.
        // BaseGamePlugins derives from SkyrimVersion via Mutagen Implicits, which
        // needs no game install.
        var env = Reflect.Uninitialized<EnvironmentStateProvider>();
        Reflect.SetField(env, "DataFolderPath", new Noggog.DirectoryPath(DataFolder));
        Reflect.SetField(env, "SkyrimVersion", SkyrimRelease.SkyrimSE);
        env.CreationClubPlugins = new HashSet<ModKey>();

        var selections = new Dictionary<FormKey, (string ModName, FormKey NpcFormKey)>();
        var consistency = Reflect.Uninitialized<NpcConsistencyProvider>();
        Reflect.SetField(consistency, "_selectedMods", selections);

        var resolver = Reflect.Uninitialized<NpcMeshResolver>();
        Reflect.SetField(resolver, "_settings", settings);
        Reflect.SetField(resolver, "_env", env);
        Reflect.SetField(resolver, "_consistency", consistency);

        return new Fixture
        {
            Resolver = resolver,
            Settings = settings,
            Origin = origin,
            Overhaul = overhaul,
            Selections = selections,
        };
    }

    private static IReadOnlyList<string> FoldersOf(IEnumerable<RenderScope> scopes)
        => scopes.Select(s => s.FolderPath).ToList();

    [Fact]
    public void SourceKey_AddsTheOriginModScope_BelowTheSelectedMod()
    {
        var f = BuildFixture();

        var scopes = f.Resolver.BuildResolutionScopes(f.Overhaul, NpcInContentMod);

        // Iteration is last-to-first, so this order means: previewed mod wins,
        // then the mod that added the NPC, then vanilla.
        FoldersOf(scopes).Should().Equal(DataFolder, OriginFolder, OverhaulFolder);
    }

    [Fact]
    public void OmittingTheSourceKey_LosesTheOriginModScope()
    {
        var f = BuildFixture();

        var scopes = f.Resolver.BuildResolutionScopes(f.Overhaul);

        // This is the shape every non-mugshot render path used to get. Kept as a
        // test so the optional overload's cost stays visible rather than looking
        // like a harmless default.
        FoldersOf(scopes).Should().Equal(DataFolder, OverhaulFolder);
    }

    [Fact]
    public void ActiveSelectionPreview_IncludesTheOriginModScope()
    {
        var f = BuildFixture();
        // The user picked the overhaul for this NPC; no guest appearance, so the
        // donor key equals the target key.
        f.Selections[NpcInContentMod] = ("Appearance Overhaul", NpcInContentMod);

        var scopes = f.Resolver.BuildResolutionScopesForActiveSelection(NpcInContentMod);

        FoldersOf(scopes).Should().Equal(DataFolder, OriginFolder, OverhaulFolder);
    }

    [Fact]
    public void ActiveSelectionPreview_KeysTheOriginOnTheDonor_NotThePatchTarget()
    {
        var f = BuildFixture();
        // Guest appearance: a vanilla NPC wearing the face of an NPC that the
        // content mod added. The origin scope must follow the donor, since the
        // donor's mod is what owns the assets being drawn.
        var vanillaTarget = FormKey.Factory("0013BA:Skyrim.esm");
        f.Selections[vanillaTarget] = ("Appearance Overhaul", NpcInContentMod);

        var scopes = f.Resolver.BuildResolutionScopesForActiveSelection(vanillaTarget);

        FoldersOf(scopes).Should().Equal(DataFolder, OriginFolder, OverhaulFolder);
    }

    [Fact]
    public void SelectedModOwningTheOriginPlugin_AddsNoDuplicateScope()
    {
        var f = BuildFixture();

        // Previewing the content mod itself: it IS the origin, so the origin scope
        // must not repeat its folder. This is why threading the key through is a
        // no-op for the common single-mod case.
        var scopes = f.Resolver.BuildResolutionScopes(f.Origin, NpcInContentMod);

        FoldersOf(scopes).Should().Equal(DataFolder, OriginFolder);
    }
}
