using System;
using System.Collections.Generic;

namespace NPC_Plugin_Chooser_2.BackEnd;

/// <summary>
/// Produces a "time remaining" estimate for a batch of roughly-sequential work
/// items (e.g. mugshot renders, FaceFinder downloads).
///
/// The old per-batch ETA used a cumulative average of work time multiplied by a
/// cumulative "fraction of items that needed work" projection. Cumulative averages
/// react very slowly: if the cheap items (cache hits, fast renders) cluster early
/// and the expensive ones come late, the estimate stays tiny and then explodes —
/// the "it said 5 minutes and finished in 2 hours" problem.
///
/// Instead this records the wall-clock cost of EVERY processed item (cheap items
/// included — the user waits for those too) and estimates from a recency-weighted
/// sliding window, so the per-item cost tracks the workload as it actually unfolds.
/// Early on, when the window is sparse, it blends toward the cumulative average so
/// the first few noisy samples don't whipsaw the number; as samples accumulate it
/// trusts the recent window, becoming increasingly accurate as the run progresses.
/// </summary>
public sealed class EtaCalculator
{
    private readonly int _windowSize;
    private readonly Queue<double> _window = new();
    private double _windowSum;
    private long _totalCount;
    private double _totalSum;

    /// <param name="windowSize">How many of the most-recent items feed the recent
    /// average. Larger = smoother but slower to react; smaller = twitchier. ~25 is a
    /// reasonable balance for batches of hundreds-to-thousands of items.</param>
    public EtaCalculator(int windowSize = 25)
    {
        _windowSize = Math.Max(1, windowSize);
    }

    /// <summary>Records the wall-clock seconds spent on one processed item.</summary>
    public void RecordItem(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;

        _window.Enqueue(seconds);
        _windowSum += seconds;
        if (_window.Count > _windowSize)
        {
            _windowSum -= _window.Dequeue();
        }

        _totalCount++;
        _totalSum += seconds;
    }

    /// <summary>
    /// Estimates time remaining for <paramref name="remainingItems"/> not-yet-processed
    /// items. Returns null when there is nothing recorded yet, nothing left to do, or
    /// the estimate is non-positive (callers treat null as "hide the ETA line").
    /// </summary>
    public TimeSpan? Estimate(int remainingItems)
    {
        if (remainingItems <= 0 || _totalCount == 0) return null;

        double windowAvg = _window.Count > 0 ? _windowSum / _window.Count : 0;
        double cumulativeAvg = _totalSum / _totalCount;

        // Trust the recent window proportionally to how full it is, so the estimate
        // is stable while the window fills and responsive once it has real history.
        double windowWeight = Math.Min(1.0, _window.Count / (double)_windowSize);
        double perItemSeconds = windowWeight * windowAvg + (1 - windowWeight) * cumulativeAvg;

        if (perItemSeconds <= 0) return null;
        return TimeSpan.FromSeconds(perItemSeconds * remainingItems);
    }
}
