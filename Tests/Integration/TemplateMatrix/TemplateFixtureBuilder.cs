using System.IO;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;

namespace NPC_Plugin_Chooser_2.Tests.Integration.TemplateMatrix;

/// <summary>
/// The eight specimen NPCs of the templated-NPC matrix (plus the supporting records they need),
/// authored synthetically so the awkward shapes — two NPCs sharing one terminus, a levelled
/// terminus, a template cycle — exist on demand instead of by luck of a real load order.
/// Roles are referenced by these constants throughout the suite.
/// </summary>
internal static class SpecimenRole
{
    /// <summary>#1 Plain-Self — untemplated, own record. Control: template handling must not touch it.</summary>
    public const string PlainSelf = "S1_PlainSelf";

    /// <summary>#2 Plain-Shared — untemplated, appearance taken from a different NPC (guest donor).</summary>
    public const string PlainShared = "S2_PlainShared";

    /// <summary>#2's donor. Not itself selected.</summary>
    public const string PlainSharedDonor = "S2_Donor";

    /// <summary>#3 Templated-A — Traits-templated to <see cref="Terminus"/>, selected to Mod X.</summary>
    public const string TemplatedA = "S3_TemplatedA";

    /// <summary>#4 Templated-B — Traits-templated to the SAME terminus, selected to Mod Y.</summary>
    public const string TemplatedB = "S4_TemplatedB";

    /// <summary>#5 Terminus — untemplated; 3 and 4's terminus, with its own selection (Mod Z).</summary>
    public const string Terminus = "S5_Terminus";

    /// <summary>#6 Templated-Shared — templated, and its donor is itself templated.</summary>
    public const string TemplatedShared = "S6_TemplatedShared";

    /// <summary>#6's donor: templated to <see cref="TemplatedSharedDonorTerminus"/>.</summary>
    public const string TemplatedSharedDonor = "S6_Donor";

    /// <summary>The end of #6's donor's chain.</summary>
    public const string TemplatedSharedDonorTerminus = "S6_DonorTerminus";

    /// <summary>#7 Templated-Leveled — chain ends in a Leveled NPC. Must never flatten.</summary>
    public const string TemplatedLeveled = "S7_TemplatedLeveled";

    /// <summary>#8 Templated-Unfollowable — two-NPC template cycle. Must never flatten; the ladder aborts it.</summary>
    public const string TemplatedUnfollowable = "S8_TemplatedUnfollowable";

    /// <summary>#8's cycle partner (templates back to #8).</summary>
    public const string UnfollowableCyclePartner = "S8_CyclePartner";
}

/// <summary>One synthetic appearance mod: a plugin plus the mod folder holding its FaceGen.</summary>
internal sealed class TemplateFixtureMod
{
    public required string DisplayName { get; init; }
    public required ModKey Key { get; init; }
    public required string Folder { get; init; }
    public required string PluginPath { get; init; }
}

/// <summary>Everything the matrix suite needs to know about the fixture it authored.</summary>
internal sealed class TemplateFixture
{
    public required string Root { get; init; }
    public required ModKey BaseKey { get; init; }
    public required string BasePluginPath { get; init; }
    public required TemplateFixtureMod ModX { get; init; }
    public required TemplateFixtureMod ModY { get; init; }
    public required TemplateFixtureMod ModZ { get; init; }
    public required IReadOnlyDictionary<string, FormKey> NpcsByRole { get; init; }
    /// <summary>EditorID of each role's record, for identifying output records after FormKey remapping.</summary>
    public required IReadOnlyDictionary<string, string> EditorIdsByRole { get; init; }

    public IEnumerable<TemplateFixtureMod> Mods => new[] { ModX, ModY, ModZ };

    public FormKey Npc(string role) => NpcsByRole[role];
    public string EditorId(string role) => EditorIdsByRole[role];

    /// <summary>Plugin file paths in load order (base first), for injection into the environment.</summary>
    public IEnumerable<(ModKey Key, string Path)> PluginsInLoadOrder()
    {
        yield return (BaseKey, BasePluginPath);
        foreach (var m in Mods) yield return (m.Key, m.PluginPath);
    }
}

/// <summary>
/// Authors the fixture: one base plugin defining the specimens (as <c>Skyrim.esm</c> defines real
/// NPCs) and three appearance-mod plugins overriding them, each with its own mod folder of
/// placeholder FaceGen files.
///
/// <para><b>Appearance is encoded in Height/Weight/HeadParts</b> so an output record can be traced to
/// the mod it came from by inspection. Base = 1.00/10/HP_Base; Mod X = 1.10/11/HP_ModX;
/// Mod Y = 1.20/22/HP_ModY; Mod Z = 1.30/33/HP_ModZ + Female.</para>
///
/// <para>Only Mod Z overrides the terminus, so the terminus's load-order winner is unambiguous. Note
/// what that means for the flatten path: <c>Patcher.ResolveAppearanceTerminusRecord</c> reads the
/// terminus from the SELECTED MOD's plugins and falls back to the load order only when that mod has no
/// record for it — and neither Mod X nor Mod Y overrides the terminus, so every cell here takes the
/// fallback. The mod-first branch is therefore NOT exercised by this fixture; it has its own test in
/// <see cref="TemplateTerminusSourceTests"/>.</para>
///
/// <para>Placeholder <c>.nif</c>/<c>.dds</c> files are safe: both NIF-parsing paths a copied mesh
/// reaches wrap parsing in try/catch and only log on failure. Their bytes differ per (mod, subject)
/// so a content hash identifies which mod's file landed where.</para>
/// </summary>
internal static class TemplateFixtureBuilder
{
    public const string ModXName = "Fixture Mod X";
    public const string ModYName = "Fixture Mod Y";
    public const string ModZName = "Fixture Mod Z";

    private static readonly ModKey[] BaseMasters =
        Implicits.Get(GameRelease.SkyrimSE).BaseMasters.ToArray();

    private const float BaseHeight = 1.00f;
    private const ushort BaseWeight = 10;
    public const float ModXHeight = 1.10f;
    public const ushort ModXWeight = 11;
    public const float ModYHeight = 1.20f;
    public const ushort ModYWeight = 22;
    public const float ModZHeight = 1.30f;
    public const ushort ModZWeight = 33;

    public const string HeadPartBase = "NPC2Tpl_HP_Base";
    public const string HeadPartModX = "NPC2Tpl_HP_ModX";
    public const string HeadPartModY = "NPC2Tpl_HP_ModY";
    public const string HeadPartModZ = "NPC2Tpl_HP_ModZ";

    /// <summary>Which subject FormKeys each mod ships FaceGen for. Measured at the SUBJECT's path —
    /// the end of the donor's Traits chain — because that is where the ladder looks.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> FaceGenSubjectsByMod =
        new Dictionary<string, string[]>
        {
            // Mod X supplies: its own two plain specimens, the terminus (for #3), and #6's donor terminus.
            [ModXName] = new[]
            {
                SpecimenRole.PlainSelf, SpecimenRole.PlainSharedDonor,
                SpecimenRole.Terminus, SpecimenRole.TemplatedSharedDonorTerminus,
            },
            // Mod Y supplies the terminus only — #4 is its only specimen, and #4's subject IS the terminus.
            [ModYName] = new[] { SpecimenRole.Terminus },
            // Mod Z supplies the terminus, which is its own specimen (#5).
            [ModZName] = new[] { SpecimenRole.Terminus },
        };

    public static TemplateFixture Build(string root)
    {
        Directory.CreateDirectory(root);

        var baseKey = ModKey.FromFileName("NPC2TplBase.esp");
        var xKey = ModKey.FromFileName("NPC2TplModX.esp");
        var yKey = ModKey.FromFileName("NPC2TplModY.esp");
        var zKey = ModKey.FromFileName("NPC2TplModZ.esp");
        var writeOrder = BaseMasters.Concat(new[] { baseKey, xKey, yKey, zKey }).ToArray();

        // ---------------- base plugin ----------------
        var baseMod = new SkyrimMod(baseKey, SkyrimRelease.SkyrimSE);

        var hpBase = NewHeadPart(baseMod, HeadPartBase);
        var hpX = NewHeadPart(baseMod, HeadPartModX);
        var hpY = NewHeadPart(baseMod, HeadPartModY);
        var hpZ = NewHeadPart(baseMod, HeadPartModZ);

        var leveled = baseMod.LeveledNpcs.AddNew();
        leveled.EditorID = "NPC2Tpl_LVLN_Terminus";

        var npcs = new Dictionary<string, Npc>(StringComparer.Ordinal);

        Npc Plain(string role)
        {
            var n = baseMod.Npcs.AddNew();
            n.EditorID = "NPC2Tpl_" + role;
            n.Name = role;
            n.Race.SetTo(Mutagen.Bethesda.FormKeys.SkyrimSE.Skyrim.Race.NordRace);
            n.Height = BaseHeight;
            n.Weight = BaseWeight;
            n.HeadParts.Add(hpBase.FormKey);
            npcs[role] = n;
            return n;
        }

        // Untemplated specimens and terminus records.
        Plain(SpecimenRole.PlainSelf);
        Plain(SpecimenRole.PlainShared);
        Plain(SpecimenRole.PlainSharedDonor);
        var terminus = Plain(SpecimenRole.Terminus);
        var donorTerminus = Plain(SpecimenRole.TemplatedSharedDonorTerminus);

        // Templated specimens. Traits + a template link is what makes a chain (Auxilliary.IsValidTemplatedNpc).
        void Templated(string role, FormKey templateTarget)
        {
            var n = Plain(role);
            n.Configuration.TemplateFlags |= NpcConfiguration.TemplateFlag.Traits;
            n.Template.SetTo(templateTarget);
        }

        Templated(SpecimenRole.TemplatedA, terminus.FormKey);
        Templated(SpecimenRole.TemplatedB, terminus.FormKey);
        Templated(SpecimenRole.TemplatedSharedDonor, donorTerminus.FormKey);
        Templated(SpecimenRole.TemplatedShared, terminus.FormKey);
        Templated(SpecimenRole.TemplatedLeveled, leveled.FormKey);

        // #8: a real two-NPC cycle rather than a dangling link, so the Unfollowable verdict comes from
        // the cycle branch of TryResolveAppearanceTerminus and cannot be confused with a missing master.
        var cyclePartner = Plain(SpecimenRole.UnfollowableCyclePartner);
        var unfollowable = Plain(SpecimenRole.TemplatedUnfollowable);
        unfollowable.Configuration.TemplateFlags |= NpcConfiguration.TemplateFlag.Traits;
        cyclePartner.Configuration.TemplateFlags |= NpcConfiguration.TemplateFlag.Traits;
        unfollowable.Template.SetTo(cyclePartner.FormKey);
        cyclePartner.Template.SetTo(unfollowable.FormKey);

        var basePluginPath = Path.Combine(root, "Base", baseKey.FileName);
        WriteMod(baseMod, basePluginPath, writeOrder);

        // ---------------- appearance mods ----------------
        // Mod X carries every specimen it is selected for, plus the donors those selections read.
        var modX = BuildMod(root, ModXName, xKey, writeOrder, baseMod, npcs,
            overrideRoles: new[]
            {
                SpecimenRole.PlainSelf, SpecimenRole.PlainShared, SpecimenRole.PlainSharedDonor,
                SpecimenRole.TemplatedA, SpecimenRole.TemplatedShared, SpecimenRole.TemplatedSharedDonor,
                SpecimenRole.TemplatedLeveled, SpecimenRole.TemplatedUnfollowable,
            },
            height: ModXHeight, weight: ModXWeight, headPart: hpX.FormKey, female: false);

        var modY = BuildMod(root, ModYName, yKey, writeOrder, baseMod, npcs,
            overrideRoles: new[] { SpecimenRole.TemplatedB },
            height: ModYHeight, weight: ModYWeight, headPart: hpY.FormKey, female: false);

        // Only Mod Z touches the terminus, so the terminus's load-order winner is Mod Z's copy — the
        // record the flatten path will read for #3 and #4.
        var modZ = BuildMod(root, ModZName, zKey, writeOrder, baseMod, npcs,
            overrideRoles: new[] { SpecimenRole.Terminus },
            height: ModZHeight, weight: ModZWeight, headPart: hpZ.FormKey, female: true);

        var fixture = new TemplateFixture
        {
            Root = root,
            BaseKey = baseKey,
            BasePluginPath = basePluginPath,
            ModX = modX,
            ModY = modY,
            ModZ = modZ,
            NpcsByRole = npcs.ToDictionary(kv => kv.Key, kv => kv.Value.FormKey),
            EditorIdsByRole = npcs.ToDictionary(kv => kv.Key, kv => kv.Value.EditorID!),
        };

        WriteFaceGen(fixture);
        return fixture;
    }

    private static HeadPart NewHeadPart(SkyrimMod mod, string editorId)
    {
        var hp = mod.HeadParts.AddNew();
        hp.EditorID = editorId;
        hp.Type = HeadPart.TypeEnum.Face;
        return hp;
    }

    private static TemplateFixtureMod BuildMod(string root, string displayName, ModKey key,
        ModKey[] writeOrder, SkyrimMod baseMod, Dictionary<string, Npc> npcs, string[] overrideRoles,
        float height, ushort weight, FormKey headPart, bool female)
    {
        var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        foreach (var role in overrideRoles)
        {
            var ovr = mod.Npcs.GetOrAddAsOverride(npcs[role]);
            ovr.Height = height;
            ovr.Weight = weight;
            ovr.HeadParts.Clear();
            ovr.HeadParts.Add(headPart);
            if (female) ovr.Configuration.Flags |= NpcConfiguration.Flag.Female;
        }

        var folder = Path.Combine(root, SafeFolderName(displayName));
        var pluginPath = Path.Combine(folder, key.FileName);
        WriteMod(mod, pluginPath, writeOrder);

        return new TemplateFixtureMod
        {
            DisplayName = displayName,
            Key = key,
            Folder = folder,
            PluginPath = pluginPath,
        };
    }

    private static void WriteMod(SkyrimMod mod, string path, ModKey[] writeOrder)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        mod.BeginWrite
            .ToPath(path)
            .WithLoadOrder(writeOrder)
            .WithNoDataFolder()
            .Write();
    }

    private static void WriteFaceGen(TemplateFixture fixture)
    {
        foreach (var mod in fixture.Mods)
        {
            foreach (var role in FaceGenSubjectsByMod[mod.DisplayName])
            {
                var subject = fixture.Npc(role);
                var (meshRel, texRel) = Auxilliary.GetFaceGenSubPathStrings(subject, regularized: true);
                WriteText(Path.Combine(mod.Folder, meshRel), FaceGenContent(mod.DisplayName, role, "nif"));
                WriteText(Path.Combine(mod.Folder, texRel), FaceGenContent(mod.DisplayName, role, "dds"));
            }
        }
    }

    /// <summary>The bytes of a placeholder FaceGen file — unique per (mod, subject, half) so a content
    /// hash says which mod's copy landed at a given destination.</summary>
    public static string FaceGenContent(string modDisplayName, string role, string half) =>
        $"NPC2-fixture-facegen|{modDisplayName}|{role}|{half}";

    private static void WriteText(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string SafeFolderName(string displayName) => displayName.Replace(" ", "");
}
