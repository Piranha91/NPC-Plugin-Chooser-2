using System.Windows;
using System.Windows.Threading;

namespace NPC_Plugin_Chooser_2.Tests.Integration;

/// <summary>
/// Owns a single WPF <see cref="Application"/> on a dedicated STA thread for the whole test
/// run and marshals test work onto it. NPC2's view models are full WPF citizens (dispatcher
/// marshalling, <c>Application.Current</c>, ReactiveUI thread affinity); xUnit runs on MTA
/// worker threads, so any test that constructs a VM must run its body via
/// <see cref="RunOnStaAsync"/>. Mirrors SynthEBD.Tests' WpfApplicationFixture.
/// </summary>
public sealed class WpfStaFixture : IDisposable
{
    private readonly Thread _staThread;
    private Dispatcher _dispatcher = null!;

    public WpfStaFixture()
    {
        var ready = new ManualResetEventSlim(false);
        _staThread = new Thread(() =>
        {
            if (Application.Current == null)
            {
                _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            }
            _dispatcher = Dispatcher.CurrentDispatcher;
            _dispatcher.UnhandledException += (_, e) =>
            {
                Console.WriteLine("[WpfStaFixture] swallowed dispatcher exception: " + e.Exception.Message);
                e.Handled = true;
            };
            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "NPC2.Tests WPF Application",
        };
        _staThread.SetApartmentState(ApartmentState.STA);
        _staThread.Start();
        ready.Wait(TimeSpan.FromSeconds(30));
    }

    /// <summary>Runs a synchronous test body on the STA thread, surfacing exceptions back to the caller.</summary>
    public Task RunOnStaAsync(Action body) => RunOnStaAsync(() =>
    {
        body();
        return Task.CompletedTask;
    });

    /// <summary>Runs an async test body on the STA thread and awaits it, surfacing exceptions.</summary>
    public async Task RunOnStaAsync(Func<Task> body)
    {
        var op = _dispatcher.InvokeAsync(body);
        var inner = await op.Task.ConfigureAwait(false);
        await inner.ConfigureAwait(false);
    }

    public void Dispose()
    {
        _dispatcher?.InvokeShutdown();
        _staThread.Join(TimeSpan.FromSeconds(5));
    }
}
