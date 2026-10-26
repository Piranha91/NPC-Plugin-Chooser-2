using System;
using System.Threading;
using System.Threading.Tasks;

namespace NPC_Plugin_Chooser_2.BackEnd;

/// <summary>
/// A concurrency limit that can change while work is in flight, used for the renderers'
/// Max parallel renders setting. Raising the limit admits waiters immediately; lowering it
/// reclaims free slots at once and the rest as in-flight work releases them, so nothing
/// running is interrupted and the new limit holds once enough work has finished.
/// <para>Backed by a <see cref="SemaphoreSlim"/> with no upper bound plus a "debt" of permits
/// still to reclaim: a release while in debt pays it off instead of freeing a slot.
/// Callers pair every successful <see cref="WaitAsync"/> with exactly one <see cref="Release"/>.</para>
/// </summary>
public sealed class ResizableConcurrencyLimiter
{
    private readonly SemaphoreSlim _semaphore;
    private readonly object _lock = new();
    private int _limit;
    private int _debt;

    public ResizableConcurrencyLimiter(int limit)
    {
        _limit = Math.Max(1, limit);
        _semaphore = new SemaphoreSlim(_limit, int.MaxValue);
    }

    public int Limit { get { lock (_lock) return _limit; } }

    public Task WaitAsync(CancellationToken token = default) => _semaphore.WaitAsync(token);

    public void Release()
    {
        lock (_lock)
        {
            if (_debt > 0) { _debt--; return; }
        }
        _semaphore.Release();
    }

    /// <summary>Values below 1 are treated as 1, so a bad setting can never stall every render.</summary>
    public void SetLimit(int limit)
    {
        limit = Math.Max(1, limit);
        lock (_lock)
        {
            int delta = limit - _limit;
            _limit = limit;
            if (delta > 0)
            {
                int repaid = Math.Min(delta, _debt);
                _debt -= repaid;
                delta -= repaid;
                if (delta > 0) _semaphore.Release(delta);
            }
            else
            {
                int shrink = -delta;
                while (shrink > 0 && _semaphore.Wait(0)) shrink--;   // free slots first
                _debt += shrink;                                     // the rest as work finishes
            }
        }
    }
}
