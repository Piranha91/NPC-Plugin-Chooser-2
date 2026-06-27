using NPC_Plugin_Chooser_2.Models;

namespace NPC_Plugin_Chooser_2.Tests.Integration.GoldenOutput;

/// <summary>
/// One of the 12 reference setting combinations = {CreateAndPatch, Create} x {Ignore, Include, IncludeAsNew}
/// x {non-SkyPatcher, SkyPatcher}. <see cref="FolderName"/> matches the reference output sub-folder exactly.
/// </summary>
internal sealed record GoldenCombo(
    int Index,
    string FolderName,
    PatchingMode PatchingMode,
    RecordOverrideHandlingMode OverrideMode,
    bool UseSkyPatcher);

internal static class GoldenCombos
{
    public static readonly IReadOnlyList<GoldenCombo> All = new[]
    {
        new GoldenCombo(1,  "NPC 01 - CreateAndPatch - Ignore",                  PatchingMode.CreateAndPatch, RecordOverrideHandlingMode.Ignore,       false),
        new GoldenCombo(2,  "NPC 02 - CreateAndPatch - Include",                 PatchingMode.CreateAndPatch, RecordOverrideHandlingMode.Include,      false),
        new GoldenCombo(3,  "NPC 03 - CreateAndPatch - IncludeAsNew",            PatchingMode.CreateAndPatch, RecordOverrideHandlingMode.IncludeAsNew, false),
        new GoldenCombo(4,  "NPC 04 - Create - Ignore",                          PatchingMode.Create,         RecordOverrideHandlingMode.Ignore,       false),
        new GoldenCombo(5,  "NPC 05 - Create - Include",                         PatchingMode.Create,         RecordOverrideHandlingMode.Include,      false),
        new GoldenCombo(6,  "NPC 06 - Create - IncludeAsNew",                    PatchingMode.Create,         RecordOverrideHandlingMode.IncludeAsNew, false),
        new GoldenCombo(7,  "NPC 07 - CreateAndPatch - Ignore - SkyPatcher",     PatchingMode.CreateAndPatch, RecordOverrideHandlingMode.Ignore,       true),
        new GoldenCombo(8,  "NPC 08 - CreateAndPatch - Include - SkyPatcher",    PatchingMode.CreateAndPatch, RecordOverrideHandlingMode.Include,      true),
        new GoldenCombo(9,  "NPC 09 - CreateAndPatch - IncludeAsNew - SkyPatcher", PatchingMode.CreateAndPatch, RecordOverrideHandlingMode.IncludeAsNew, true),
        new GoldenCombo(10, "NPC 10 - Create - Ignore - SkyPatcher",            PatchingMode.Create,         RecordOverrideHandlingMode.Ignore,       true),
        new GoldenCombo(11, "NPC 11 - Create - Include - SkyPatcher",           PatchingMode.Create,         RecordOverrideHandlingMode.Include,      true),
        new GoldenCombo(12, "NPC 12 - Create - IncludeAsNew - SkyPatcher",      PatchingMode.Create,         RecordOverrideHandlingMode.IncludeAsNew, true),
    };

    /// <summary>
    /// The reference NPC 08 (CreateAndPatch/Include/SkyPatcher) and NPC 11 (Create/Include/SkyPatcher) were
    /// captured BEFORE the ChildClothes01 (0006D92C) SkyPatcher+Include fix and are therefore stale: the
    /// fixed patcher now writes that outfit-override edit, which the stale reference lacks. The golden test
    /// tolerates exactly that one known deviation for these combos until the user regenerates them.
    /// </summary>
    public static bool IsStaleForChildClothesFix(GoldenCombo combo) =>
        combo.UseSkyPatcher && combo.OverrideMode == RecordOverrideHandlingMode.Include;
}
