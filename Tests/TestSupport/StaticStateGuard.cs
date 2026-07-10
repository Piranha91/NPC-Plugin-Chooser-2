using System.Globalization;
using System.Reactive.Concurrency;
using NPC_Plugin_Chooser_2.BackEnd;
using ReactiveUI;
// ReactiveUI 20+ removed the static RxApp class; its (still read/write) schedulers moved to
// ReactiveUI.RxSchedulers. Alias keeps this guard's get/set of the schedulers unchanged.
using RxApp = ReactiveUI.RxSchedulers;

namespace NPC_Plugin_Chooser_2.Tests.TestSupport;

/// <summary>
/// Snapshots process-global state that NPC2 mutates (the ReactiveUI schedulers, the
/// current-thread culture, the static <see cref="NpcDiagnosticLogger"/>) and restores it
/// on dispose. Tests that touch any of these run in the integration collection (serial)
/// and wrap their body in <c>using var _ = new StaticStateGuard();</c>.
/// </summary>
public sealed class StaticStateGuard : IDisposable
{
    private readonly IScheduler _mainScheduler;
    private readonly IScheduler _taskpoolScheduler;
    private readonly CultureInfo _culture;
    private readonly CultureInfo _uiCulture;

    public StaticStateGuard(bool immediateSchedulers = true)
    {
        _mainScheduler = RxApp.MainThreadScheduler;
        _taskpoolScheduler = RxApp.TaskpoolScheduler;
        _culture = Thread.CurrentThread.CurrentCulture;
        _uiCulture = Thread.CurrentThread.CurrentUICulture;

        if (immediateSchedulers)
        {
            // Deterministic, synchronous reactive notifications for assertions.
            RxApp.MainThreadScheduler = ImmediateScheduler.Instance;
            RxApp.TaskpoolScheduler = ImmediateScheduler.Instance;
        }
    }

    /// <summary>Forces a specific culture for the duration (e.g. de-DE for invariant-format regressions).</summary>
    public StaticStateGuard WithCulture(string name)
    {
        var c = CultureInfo.GetCultureInfo(name);
        Thread.CurrentThread.CurrentCulture = c;
        Thread.CurrentThread.CurrentUICulture = c;
        return this;
    }

    public void Dispose()
    {
        try { NpcDiagnosticLogger.Configure(null); NpcDiagnosticLogger.Shutdown(); } catch { /* ignore */ }
        RxApp.MainThreadScheduler = _mainScheduler;
        RxApp.TaskpoolScheduler = _taskpoolScheduler;
        Thread.CurrentThread.CurrentCulture = _culture;
        Thread.CurrentThread.CurrentUICulture = _uiCulture;
    }
}
