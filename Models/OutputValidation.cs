using System.Collections.Generic;

namespace NPC_Plugin_Chooser_2.Models;

/// <summary>
/// Scope of an output-validation run: which NPCs (among those with appearance
/// selections) should be checked.
/// </summary>
public enum ValidationScopeMode
{
    /// All NPCs for which the user has made an appearance selection.
    AllSelected,

    /// Only the explicit subset of NPCs the user picked in the scope dialog.
    Subset
}

/// <summary>Severity of a single validation finding.</summary>
public enum ValidationSeverity
{
    /// Definite problem: the chosen appearance will NOT be what shows in-game.
    Error,

    /// Likely problem or something the user should look at, but not certain.
    Warning,

    /// Informational; expected behaviour or context, surfaced for transparency.
    Info
}

/// <summary>Which of the three checks produced a finding.</summary>
public enum ValidationCheckKind
{
    Environment,
    Selection,
    Record,
    Asset,
    SkyPatcher
}

/// <summary>
/// One row in the validation results table. All display fields are strings so the
/// grid binds directly and the rows export cleanly to TSV/CSV.
/// </summary>
public sealed class ValidationIssue
{
    public ValidationSeverity Severity { get; init; }
    public ValidationCheckKind Check { get; init; }

    public string NpcDisplayName { get; init; } = string.Empty;
    public string NpcFormKey { get; init; } = string.Empty;
    public string SelectedMod { get; init; } = string.Empty;

    /// Human-readable description of the finding.
    public string Issue { get; init; } = string.Empty;

    /// What is actually providing the winning record / asset / runtime override
    /// (a plugin name, a mod folder, or a SkyPatcher .ini path).
    public string WinningSource { get; init; } = string.Empty;

    /// Extra detail (differing fields, file sizes, ini priority, raw config line).
    public string Details { get; init; } = string.Empty;

    public string SeverityText => Severity.ToString();
    public string CheckText => Check.ToString();
}

/// <summary>
/// Result of an output-validation run. When <see cref="Blocked"/> is true the run
/// did not complete (e.g. the output is not deployed, or the environment could not
/// be built) and <see cref="BlockReason"/> explains why.
/// </summary>
public sealed class ValidationRunResult
{
    public bool Blocked { get; set; }
    public string? BlockReason { get; set; }

    public int NpcsChecked { get; set; }

    public List<ValidationIssue> Issues { get; } = new();

    /// Run-level notes that aren't tied to a single NPC (e.g. "N SkyPatcher lines
    /// use broad filters this tool does not evaluate").
    public List<string> Notes { get; } = new();
}
