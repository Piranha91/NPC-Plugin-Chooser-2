using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.Models;

namespace NPC_Plugin_Chooser_2.Tests.Integration.TemplateMatrix;

/// <summary>
/// Builds the <see cref="Settings"/> the headless patcher consumes for one matrix cell. Modelled on
/// <c>GoldenComboSettingsBuilder</c>; only the per-cell knobs (patching mode / SkyPatcher / template
/// handling) and the output directory vary.
/// </summary>
internal static class TemplateMatrixSettingsBuilder
{
    /// <summary>
    /// target role -> (mod display name, appearance donor role).
    ///
    /// <para>#3 and #4 share a terminus but are given DIFFERENT mods; that pairing is what makes the
    /// decisive per-NPC-independence assertion possible, and #5 (the terminus) gets a third mod so
    /// neither of them can be mistaken for it.</para>
    /// </summary>
    public static readonly (string TargetRole, string Mod, string DonorRole)[] Selections =
    {
        (SpecimenRole.PlainSelf,             TemplateFixtureBuilder.ModXName, SpecimenRole.PlainSelf),
        (SpecimenRole.PlainShared,           TemplateFixtureBuilder.ModXName, SpecimenRole.PlainSharedDonor),
        (SpecimenRole.TemplatedA,            TemplateFixtureBuilder.ModXName, SpecimenRole.TemplatedA),
        (SpecimenRole.TemplatedB,            TemplateFixtureBuilder.ModYName, SpecimenRole.TemplatedB),
        (SpecimenRole.Terminus,              TemplateFixtureBuilder.ModZName, SpecimenRole.Terminus),
        (SpecimenRole.TemplatedShared,       TemplateFixtureBuilder.ModXName, SpecimenRole.TemplatedSharedDonor),
        (SpecimenRole.TemplatedLeveled,      TemplateFixtureBuilder.ModXName, SpecimenRole.TemplatedLeveled),
        (SpecimenRole.TemplatedUnfollowable, TemplateFixtureBuilder.ModXName, SpecimenRole.TemplatedUnfollowable),
    };

    /// <summary>The eight specimen roles, in the order the report should present them.</summary>
    public static IReadOnlyList<string> SpecimenRoles => Selections.Select(s => s.TargetRole).ToList();

    /// <param name="perModTemplateOverride">
    /// Optional per-mod <c>ModSetting.ModTemplateHandlingMode</c> values. The resolution logic itself is
    /// unit-tested; what this exercises is that the PATCHER reads the effective mode
    /// (<c>Settings.GetEffectiveTemplateHandlingMode</c>) rather than the global one.
    /// </param>
    public static Settings Build(TemplateFixture fixture, TemplateMatrixCell cell, string outputDirectory,
        IReadOnlyDictionary<string, TemplateHandlingMode>? perModTemplateOverride = null)
    {
        var settings = new Settings
        {
            SkyrimRelease = SkyrimRelease.SkyrimSE,
            OutputPluginName = string.Empty,     // -> "NPC"
            OutputDirectory = outputDirectory,
            AppendTimestampToOutputDirectory = false,
            ModsFolder = string.Empty,
            SplitOutput = false,
            // Off so output FormKeys stay in the ordinary range and the written plugin reads back
            // without compaction in the way; the fixture is far too small to need ESL-ification.
            AutoEslIfy = false,
            AutoSplitOutput = false,
            PatchingMode = cell.PatchingMode,
            UseSkyPatcherMode = cell.UseSkyPatcher,
            TemplateHandlingMode = cell.TemplateMode,
            DefaultRecordOverrideHandlingMode = RecordOverrideHandlingMode.Ignore,
            DefaultMaxNestedIntervalDepth = 2,
            DefaultIncludeAllOverrides = false,
            LocalizationLanguage = null,
            BatFilePreCommands = string.Empty,
            BatFilePostCommands = string.Empty,
        };

        settings.ModSettings = fixture.Mods.Select(m => new ModSetting
        {
            DisplayName = m.DisplayName,
            CorrespondingModKeys = new List<ModKey> { m.Key },
            CorrespondingFolderPaths = new List<string> { m.Folder },
            // Nothing to merge: every record the fixture references lives in the base plugin, which
            // stays in the load order as a master of the output.
            MergeInDependencyRecords = false,
            IncludeOutfits = false,
            // FaceGen is copied regardless of this flag (it gates only the non-FaceGen asset section
            // of ScheduleCopyNpcAssets), so the §3b assertions hold with it off and the output stays small.
            CopyAssets = false,
            ModRecordOverrideHandlingMode = RecordOverrideHandlingMode.Ignore,
            ModTemplateHandlingMode = perModTemplateOverride != null
                                      && perModTemplateOverride.TryGetValue(m.DisplayName, out var mode)
                ? mode
                : null,
        }).ToList();

        settings.SelectedAppearanceMods = Selections.ToDictionary(
            s => fixture.Npc(s.TargetRole),
            s => (s.Mod, fixture.Npc(s.DonorRole)));

        return settings;
    }
}
