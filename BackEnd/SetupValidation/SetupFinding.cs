namespace NPC_Plugin_Chooser_2.BackEnd.SetupValidation;

/// <summary>
/// How a setup-wizard finding gates the page it appears on: an <see cref="Error"/> blocks Next,
/// a <see cref="Warning"/> must be acknowledged before Next, an <see cref="Info"/> never blocks.
/// </summary>
public enum SetupSeverity
{
    Info,
    Warning,
    Error,
}

public sealed record SetupFinding(SetupSeverity Severity, string Message)
{
    public static SetupFinding Info(string message) => new(SetupSeverity.Info, message);
    public static SetupFinding Warning(string message) => new(SetupSeverity.Warning, message);
    public static SetupFinding Error(string message) => new(SetupSeverity.Error, message);
}

/// <summary>
/// The mod manager the user says they launch N.P.C.2 from. Wizard-only: never persisted, it only
/// tailors the hints and checks on the later pages.
/// </summary>
public enum SetupModManager
{
    MO2,
    Vortex,
    Other,
}
