using NPC_Plugin_Chooser_2.Models;

namespace NPC_Plugin_Chooser_2.Tests.Integration.TemplateMatrix;

/// <summary>
/// One cell of the matrix: an output mode paired with a template-handling setting.
///
/// <para><c>RecordOverrideHandlingMode</c> is pinned to <see cref="RecordOverrideHandlingMode.Ignore"/>
/// throughout — it is orthogonal to template handling and the golden suite already covers all three
/// values across its 12 combos, so varying it here would triple the runtime for no new signal.</para>
/// </summary>
internal sealed record TemplateMatrixCell(
    int Index,
    PatchingMode PatchingMode,
    bool UseSkyPatcher,
    TemplateHandlingMode TemplateMode)
{
    /// <summary>The output mode, without the template setting — the two cells that share this string
    /// are the pair the report puts side by side.</summary>
    public string ModeName => PatchingMode + (UseSkyPatcher ? " + SkyPatcher" : string.Empty);

    public string Name => $"{ModeName} / {TemplateMode}";

    public string FolderName =>
        $"{Index:00} - {PatchingMode}{(UseSkyPatcher ? "-SkyPatcher" : string.Empty)} - {TemplateMode}";
}

internal static class TemplateMatrixCells
{
    /// <summary>The 8 cells: 2 patching modes x SkyPatcher on/off x 2 template settings.
    /// Ordered so the two template settings of a mode are adjacent (indices 1/2, 3/4, ...).</summary>
    public static readonly IReadOnlyList<TemplateMatrixCell> All = Build();

    /// <summary>The 4 output modes, each paired with its two template-setting cells.</summary>
    public static IEnumerable<(string ModeName, TemplateMatrixCell Inherit, TemplateMatrixCell OwnCopy)> Pairs =>
        All.GroupBy(c => c.ModeName)
            .Select(g => (
                g.Key,
                g.First(c => c.TemplateMode == TemplateHandlingMode.InheritFromTemplate),
                g.First(c => c.TemplateMode == TemplateHandlingMode.GiveEachNpcOwnCopy)));

    private static List<TemplateMatrixCell> Build()
    {
        var cells = new List<TemplateMatrixCell>();
        int index = 1;
        foreach (var patchingMode in new[] { PatchingMode.Create, PatchingMode.CreateAndPatch })
        {
            foreach (var skyPatcher in new[] { false, true })
            {
                foreach (var templateMode in new[]
                         {
                             TemplateHandlingMode.InheritFromTemplate,
                             TemplateHandlingMode.GiveEachNpcOwnCopy,
                         })
                {
                    cells.Add(new TemplateMatrixCell(index++, patchingMode, skyPatcher, templateMode));
                }
            }
        }

        return cells;
    }
}
