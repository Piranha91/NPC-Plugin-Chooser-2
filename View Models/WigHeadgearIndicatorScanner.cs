using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;

namespace NPC_Plugin_Chooser_2.View_Models;

/// <summary>
/// Everything the wig-and-headgear answer for one row of the NPC list depends on. Two keys are
/// equal exactly when a stored answer may be reused.
/// </summary>
/// <param name="Npc">The NPC of the row.</param>
/// <param name="ModName">The selected appearance mod.</param>
/// <param name="SourceNpc">The NPC whose appearance was selected (differs for a guest appearance).</param>
/// <param name="WigMode">The mod's effective wig mode when the question was put.</param>
/// <param name="SettingsSignature">The settings that decide which outfit the NPC wears and whether
/// a wig is forwarded into it, as one string (built by the NPC list).</param>
public readonly record struct WigConflictQueryKey(
    FormKey Npc, string ModName, FormKey SourceNpc, WigHandlingMode WigMode, string SettingsSignature);

/// <summary>One question for the scanner: the key, and the mod as a snapshot the worker thread
/// may read without touching the view models.</summary>
public sealed record WigConflictQuery(WigConflictQueryKey Key, ModSetting ModSetting);

/// <summary>
/// Answers "does this row have a wig-and-headgear conflict?" for the NPC list, off the UI
/// thread. The record work behind the answer resolves an outfit per NPC and reads plugin files,
/// so it must never run where the list is drawn: rows ask through <see cref="Request"/> and are
/// told through the callback when their answer is in.
///
/// <para><b>What is never asked.</b> A query whose wig mode is not 'Forward to Outfit' is
/// dropped on entry: under every other mode nothing can be forwarded into an outfit.</para>
///
/// <para><b>Cache.</b> One answer per NPC, kept with the key it was asked under. A row whose
/// key is unchanged is answered from the cache and the back end is not called again; a changed
/// key (another mod, another wig mode, other outfit-affecting settings) replaces the entry.</para>
///
/// <para><b>Threading.</b> One worker at a time drains the queue on the thread pool and stops
/// when it is empty. <see cref="Reset"/> and <see cref="Dispose"/> cancel it: the query in
/// flight finishes, its answer is discarded, nothing further starts.</para>
/// </summary>
public sealed class WigHeadgearIndicatorScanner : IDisposable
{
    private static readonly IReadOnlyList<WigHeadgearConflict> NoConflicts = Array.Empty<WigHeadgearConflict>();

    private readonly Func<WigConflictQuery, IReadOnlyList<WigHeadgearConflict>?> _query;
    private readonly Action<IReadOnlyList<FormKey>> _onAnswered;
    private readonly int _batchSize;
    private readonly TimeSpan _batchInterval;

    private readonly object _gate = new();
    private readonly Dictionary<FormKey, (WigConflictQueryKey Key, IReadOnlyList<WigHeadgearConflict> Conflicts)> _answers = new();
    private readonly Dictionary<FormKey, WigConflictQuery> _pending = new();
    private readonly Queue<FormKey> _order = new();
    private CancellationTokenSource _cts = new();
    private bool _workerRunning;
    private Task _worker = Task.CompletedTask;
    private bool _disposed;

    private long _queriesRun;
    private long _queryTicks;

    /// <param name="query">The back-end question
    /// (<see cref="WigForwarder.FindHeadgearConflictsForSelection"/>). Called on a thread-pool
    /// thread, one call at a time. A null result means "cannot say right now": it is not stored
    /// and not reported, and the row may ask again.</param>
    /// <param name="onAnswered">Told which NPCs have a new answer, in batches, on the worker
    /// thread. The receiver marshals to the UI thread itself.</param>
    /// <param name="batchSize">Answers gathered before the receiver is told.</param>
    /// <param name="batchInterval">The longest a gathered answer waits to be reported while
    /// the worker is still busy. Defaults to a quarter of a second.</param>
    public WigHeadgearIndicatorScanner(
        Func<WigConflictQuery, IReadOnlyList<WigHeadgearConflict>?> query,
        Action<IReadOnlyList<FormKey>> onAnswered,
        int batchSize = 64,
        TimeSpan? batchInterval = null)
    {
        _query = query;
        _onAnswered = onAnswered;
        _batchSize = Math.Max(1, batchSize);
        _batchInterval = batchInterval ?? TimeSpan.FromMilliseconds(250);
    }

    /// <summary>How many times the back end was asked since construction.</summary>
    public long QueriesRun => Interlocked.Read(ref _queriesRun);

    /// <summary>Time spent inside the back-end question, summed over <see cref="QueriesRun"/>.</summary>
    public TimeSpan TotalQueryTime => Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _queryTicks));

    /// <summary>Completes when the worker that is running now (if any) has stopped. For tests
    /// and diagnostics; a later <see cref="Request"/> starts a new worker.</summary>
    public Task Idle
    {
        get { lock (_gate) return _worker; }
    }

    /// <summary>The answer stored for <paramref name="key"/>, if the row was already answered
    /// under exactly this key.</summary>
    public bool TryGetAnswer(WigConflictQueryKey key, out IReadOnlyList<WigHeadgearConflict> conflicts)
    {
        lock (_gate)
        {
            if (_answers.TryGetValue(key.Npc, out var entry) && entry.Key == key)
            {
                conflicts = entry.Conflicts;
                return true;
            }
        }

        conflicts = NoConflicts;
        return false;
    }

    /// <summary>
    /// Asks for an answer in the background. Does nothing when the wig mode cannot forward into
    /// an outfit, when the answer for this key is already stored, or when the same question is
    /// already waiting. A newer question for the same NPC replaces the one waiting.
    /// </summary>
    /// <returns>True when a question was queued.</returns>
    public bool Request(WigConflictQuery query)
    {
        if (!NpcSelectionIndicatorRules.WigConflictQueryApplies(query.Key.WigMode)) return false;

        CancellationToken token;
        lock (_gate)
        {
            if (_disposed) return false;
            if (_answers.TryGetValue(query.Key.Npc, out var entry) && entry.Key == query.Key) return false;
            if (_pending.TryGetValue(query.Key.Npc, out var waiting))
            {
                if (waiting.Key == query.Key) return false;
                _pending[query.Key.Npc] = query; // keeps its place in the queue
                return true;
            }

            _pending[query.Key.Npc] = query;
            _order.Enqueue(query.Key.Npc);

            if (_workerRunning) return true;
            _workerRunning = true;
            token = _cts.Token;
            // Chained behind the previous worker rather than started beside it: that one may
            // still be handing over its last answers, or (after a Reset) finishing the query it
            // was cancelled in. So the back end is only ever asked by one thread at a time, and
            // Idle covers everything started so far.
            _worker = _worker.ContinueWith(_ => Drain(token), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default);
        }

        return true;
    }

    /// <summary>Forgets the answer and any waiting question of one NPC (its selection was
    /// removed).</summary>
    public void Forget(FormKey npc)
    {
        lock (_gate)
        {
            _answers.Remove(npc);
            _pending.Remove(npc);
        }
    }

    /// <summary>Cancels the work in progress and forgets every answer: the records the answers
    /// were read from are no longer the ones in use (the environment was rebuilt).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            // The worker of the old token finishes the query it is in and leaves by itself; from
            // here on it is no longer "the" worker, so the next Request starts a fresh one.
            _cts.Cancel();
            _cts = new CancellationTokenSource();
            _workerRunning = false;
            _answers.Clear();
            _pending.Clear();
            _order.Clear();
        }
    }

    private void Drain(CancellationToken token)
    {
        var answered = new List<FormKey>();
        long lastReport = Stopwatch.GetTimestamp();

        void Report()
        {
            if (answered.Count == 0) return;
            var batch = answered.ToArray();
            answered.Clear();
            lastReport = Stopwatch.GetTimestamp();
            try { _onAnswered(batch); }
            catch (Exception ex) { Debug.WriteLine($"WigHeadgearIndicatorScanner: receiver failed: {ex.Message}"); }
        }

        while (true)
        {
            WigConflictQuery? next = null;
            lock (_gate)
            {
                while (!token.IsCancellationRequested && _order.Count > 0 && next == null)
                {
                    var npc = _order.Dequeue();
                    if (_pending.Remove(npc, out var waiting)) next = waiting;
                }

                if (next == null)
                {
                    // Nothing left for THIS worker. After a Reset the flag belongs to the worker
                    // of the new token, so a cancelled worker leaves it alone.
                    if (!token.IsCancellationRequested) _workerRunning = false;
                    break;
                }
            }

            IReadOnlyList<WigHeadgearConflict>? conflicts;
            long started = Stopwatch.GetTimestamp();
            try
            {
                conflicts = _query(next);
            }
            catch (Exception ex)
            {
                // The indicator only informs; a row that cannot be checked shows no conflict.
                Debug.WriteLine($"WigHeadgearIndicatorScanner: {next.Key.Npc} could not be checked: {ex.Message}");
                conflicts = NoConflicts;
            }
            Interlocked.Add(ref _queryTicks, Stopwatch.GetTimestamp() - started);
            Interlocked.Increment(ref _queriesRun);

            // "Cannot say right now" (the caller's records are in use elsewhere): nothing is
            // stored, so the row asks again at its next refresh.
            if (conflicts == null) continue;

            lock (_gate)
            {
                // Cancelled while the back end was working: the answer may describe records that
                // are no longer in use, so it is dropped.
                if (token.IsCancellationRequested) continue;
                _answers[next.Key.Npc] = (next.Key, conflicts);
            }

            answered.Add(next.Key.Npc);
            if (answered.Count >= _batchSize || Stopwatch.GetElapsedTime(lastReport) >= _batchInterval) Report();
        }

        Report();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _cts.Cancel();
            _pending.Clear();
            _order.Clear();
        }
    }
}
