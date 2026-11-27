using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.Models;

namespace NPC_Plugin_Chooser_2.View_Models;

/// <summary>
/// The one place the UI stores, forgets and counts wig-and-headgear answers
/// (<see cref="Settings.WigHeadgearConflictChoices"/>), so that the three views that care (the
/// run, the Settings button, the NPC list) agree and hear about each other's changes. Settings is
/// a plain model and raises nothing.
///
/// <para><b>Answers for one run only.</b> The patcher reads answers from Settings and nowhere
/// else, so an answer the user did not ask to have remembered is still written there for the
/// run, and taken out again when the run ends (<see cref="ApplyForRun"/>). While it is there it
/// is not a remembered answer: it is left out of <see cref="RememberedCount"/> and
/// <see cref="ClearRemembered"/> does not touch it.</para>
///
/// <para>What this cannot do: Settings is saved on a timer as well as at exit, so if the process
/// is killed in the middle of a run a run-only answer can have reached Settings.json. A normal
/// end of the run, by success, failure or cancel, removes it.</para>
/// </summary>
public sealed class WigHeadgearChoiceCoordinator
{
    private readonly Settings _settings;
    private readonly object _gate = new();
    private readonly HashSet<FormKey> _runOnly = new();
    private readonly Subject<Unit> _changed = new();
    private int _activeRuns;

    public WigHeadgearChoiceCoordinator(Settings settings)
    {
        _settings = settings;
    }

    /// <summary>Fires after answers were stored or removed, and when a run starts or ends. May
    /// fire on any thread; UI subscribers observe on the main thread.</summary>
    public IObservable<Unit> Changed => _changed.AsObservable();

    /// <summary>How many NPCs have a remembered answer (answers held for a run in progress are
    /// not remembered ones).</summary>
    public int RememberedCount
    {
        get
        {
            lock (_gate)
            {
                var choices = _settings.WigHeadgearConflictChoices;
                if (choices == null) return 0;
                return _runOnly.Count == 0 ? choices.Count : choices.Keys.Count(k => !_runOnly.Contains(k));
            }
        }
    }

    /// <summary>True between <see cref="BeginRun"/> and the disposal of what it returned. The
    /// patcher reads the answers on its own threads during a run, so nothing else may change
    /// them then.</summary>
    public bool IsRunActive
    {
        get { lock (_gate) return _activeRuns > 0; }
    }

    /// <summary>Marks a patch run as in progress until the returned handle is disposed.</summary>
    public IDisposable BeginRun()
    {
        lock (_gate) _activeRuns++;
        _changed.OnNext(Unit.Default);
        return new Releaser(() =>
        {
            lock (_gate) _activeRuns--;
            _changed.OnNext(Unit.Default);
        });
    }

    /// <summary>
    /// Stores <paramref name="choice"/> for every NPC in <paramref name="targetNpcFormKeys"/>.
    /// With <paramref name="remember"/> the answers stay (and persist with the settings) and the
    /// returned handle does nothing. Without it they are this run's only: disposing the handle
    /// removes them again, which the caller must do however the run ends.
    ///
    /// <para>An NPC that already has an answer keeps being a remembered NPC even when a run-only
    /// answer is written over it; its earlier answer is put back when the run ends. (The scan
    /// only lists NPCs with no answer, so this is a safeguard, not a path the window takes.)</para>
    /// </summary>
    public IDisposable ApplyForRun(IEnumerable<FormKey> targetNpcFormKeys, WigHeadgearChoice choice, bool remember)
    {
        var keys = targetNpcFormKeys.Distinct().ToList();

        if (remember)
        {
            lock (_gate)
            {
                foreach (var key in keys) _runOnly.Remove(key);
                _settings.SetWigHeadgearChoice(keys, choice);
            }
            _changed.OnNext(Unit.Default);
            return new Releaser(() => { });
        }

        var added = new List<FormKey>();
        var replaced = new Dictionary<FormKey, WigHeadgearChoice>();
        lock (_gate)
        {
            foreach (var key in keys)
            {
                var earlier = _settings.GetWigHeadgearChoice(key);
                if (earlier is { } remembered && !_runOnly.Contains(key))
                {
                    replaced[key] = remembered;
                }
                else
                {
                    added.Add(key);
                    _runOnly.Add(key);
                }
            }
            _settings.SetWigHeadgearChoice(keys, choice);
        }
        _changed.OnNext(Unit.Default);

        return new Releaser(() =>
        {
            lock (_gate)
            {
                _settings.RemoveWigHeadgearChoices(added);
                foreach (var key in added) _runOnly.Remove(key);
                foreach (var (key, earlier) in replaced)
                {
                    _settings.SetWigHeadgearChoice(new[] { key }, earlier);
                }
            }
            _changed.OnNext(Unit.Default);
        });
    }

    /// <summary>
    /// Forgets every remembered answer. Returns false, having changed nothing, while a run is in
    /// progress. Answers held for a run are never touched here.
    /// </summary>
    public bool ClearRemembered()
    {
        lock (_gate)
        {
            if (_activeRuns > 0) return false;

            if (_runOnly.Count == 0)
            {
                _settings.ClearWigHeadgearChoices();
            }
            else
            {
                var remembered = _settings.WigHeadgearConflictChoices?.Keys
                    .Where(k => !_runOnly.Contains(k)).ToList() ?? new List<FormKey>();
                _settings.RemoveWigHeadgearChoices(remembered);
            }
        }
        _changed.OnNext(Unit.Default);
        return true;
    }

    private sealed class Releaser : IDisposable
    {
        private Action? _release;
        public Releaser(Action release) => _release = release;
        public void Dispose() => System.Threading.Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
