using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.Models;

namespace NPC_Plugin_Chooser_2.Tests.Integration.GoldenOutput;

/// <summary>
/// Builds the <see cref="Settings"/> the headless patcher consumes for one golden combo. The per-NPC
/// selections, ModSetting flags and the synthetic "Base Game" entry mirror the live configuration that
/// generated the reference set (resolved from the user's Settings.json); only the per-combo knobs
/// (PatchingMode / override mode / SkyPatcher) and the temp output directory vary.
/// </summary>
internal static class GoldenComboSettingsBuilder
{
    private static readonly ModKey Skyrim = ModKey.FromFileName("Skyrim.esm");

    /// <summary>The 8 selections: target NPC -&gt; (ModSetting DisplayName, appearance donor NPC).</summary>
    public static readonly (string Target, string Mod, string Donor)[] Selections =
    {
        // target          appearance mod (DisplayName)                                  donor (appearance source)
        ("013475:Skyrim.esm", "Nordic Faces - FaceGen - With FaceTint",        "013475:Skyrim.esm"), // Alvor    (self, FaceGen-only)
        ("013476:Skyrim.esm", "The Ordinary Women SSE",                        "013476:Skyrim.esm"), // Sigrid   (self)
        ("013477:Skyrim.esm", "RS Children Overhaul",                          "013477:Skyrim.esm"), // Dorthe   (self; ChildClothes01 case)
        ("013478:Skyrim.esm", "The Ordinary Women SSE",                        "01347C:Skyrim.esm"), // shared from Ordinary Women's Gerdur
        ("013479:Skyrim.esm", "Nordic Faces - FaceGen - With FaceTint",        "01347D:Skyrim.esm"), // shared, FaceGen-only
        ("01347A:Skyrim.esm", "Base Game",                                     "01327E:Skyrim.esm"), // shared from Base Game (BSA extraction)
        ("01347B:Skyrim.esm", "WICO - Windsong Immersive Chracter Overhaul",   "013480:Skyrim.esm"), // shared, different race+gender donor
        ("01347C:Skyrim.esm", "Nordic Faces - FaceGen - With FaceTint",        "01B1D2:Skyrim.esm"), // shared, beast-race donor (Kharjo)
    };

    public const string SelGroupName = "sel";

    public static Settings Build(GoldenOutputConfig config, GoldenCombo combo, string outputDirectory)
    {
        var settings = new Settings
        {
            SkyrimRelease = SkyrimRelease.SkyrimSE,
            OutputPluginName = string.Empty,            // -> "NPC" (DefaultPluginName)
            OutputDirectory = outputDirectory,          // rooted temp dir; assets + NPC.esp + token land here
            AppendTimestampToOutputDirectory = false,
            ModsFolder = string.Empty,
            SplitOutput = false,
            AutoEslIfy = true,                          // reference plugins are ESL-flagged
            AutoSplitOutput = false,                    // pin the plain single-file write for golden determinism (fixtures never exceed the master limit)
            PatchingMode = combo.PatchingMode,
            UseSkyPatcherMode = combo.UseSkyPatcher,
            DefaultRecordOverrideHandlingMode = combo.OverrideMode,
            DefaultMaxNestedIntervalDepth = 2,
            DefaultIncludeAllOverrides = false,
            LocalizationLanguage = null,
            BatFilePreCommands = string.Empty,
            BatFilePostCommands = "tai\r\nfw 10e1f2",
        };

        settings.ModSettings = BuildModSettings(config);
        settings.SelectedAppearanceMods = BuildSelections();

        // Put all 8 targets in a "sel" group, for the spawn-batch flow test.
        settings.NpcGroupAssignments = Selections
            .ToDictionary(s => FormKey.Factory(s.Target),
                          _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { SelGroupName });

        return settings;
    }

    public static Dictionary<FormKey, (string ModName, FormKey NpcFormKey)> BuildSelections() =>
        Selections.ToDictionary(s => FormKey.Factory(s.Target),
                                s => (s.Mod, FormKey.Factory(s.Donor)));

    private static List<ModSetting> BuildModSettings(GoldenOutputConfig config)
    {
        var list = new List<ModSetting>();

        foreach (var (displayName, entry) in config.AppearanceMods)
        {
            list.Add(new ModSetting
            {
                DisplayName = displayName,
                CorrespondingModKeys = entry.Plugins.Select(p => ModKey.FromFileName(p)).ToList(),
                CorrespondingFolderPaths = entry.Folders.ToList(),
                IsFaceGenOnlyEntry = entry.IsFaceGenOnly,
                MergeInDependencyRecords = true,
                IncludeOutfits = false,
                CopyAssets = true,
                // null => follow the per-combo Settings.DefaultRecordOverrideHandlingMode (ctor default is Ignore).
                ModRecordOverrideHandlingMode = null,
            });
        }

        // Synthetic "Base Game" entry (the appearance source for Lucan <- General Tullius). Vanilla assets
        // live in BSAs, so CorrespondingFolderPaths is intentionally empty (resolved from the Data folder).
        list.Add(new ModSetting
        {
            DisplayName = "Base Game",
            IsAutoGenerated = true,
            CorrespondingModKeys = new List<ModKey>
            {
                Skyrim,
                ModKey.FromFileName("Update.esm"),
                ModKey.FromFileName("Dawnguard.esm"),
                ModKey.FromFileName("HearthFires.esm"),
                ModKey.FromFileName("Dragonborn.esm"),
            },
            CorrespondingFolderPaths = new List<string>(),
            MergeInDependencyRecords = false,
            CopyAssets = true,
            // Pinned to Ignore (NOT following the per-combo default), mirroring the live config. The base
            // masters have no meaningful "overrides to include", and running Include/IncludeAsNew discovery
            // over them would traverse Update/DLC navmesh-override graphs (which reset the interval-depth
            // guard on every hit) into a stack overflow. The reference set was generated with this pinned.
            ModRecordOverrideHandlingMode = RecordOverrideHandlingMode.Ignore,
        });

        return list;
    }
}
