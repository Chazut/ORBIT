using System.Diagnostics;

namespace Orbit.Systems;

// Global across every group. A body activation is indivisible, so the time limit is soft.
internal sealed class GhostWakeBudget
{
    internal const int MaxBodiesPerFrame = 2;
    internal const double MaxMilliseconds = 2;
    private int _frame = -1, _bodies;
    private long _started;

    internal bool TryBegin(int frame, long now)
    {
        if (_frame != frame) { _frame = frame; _bodies = 0; _started = now; }
        if (_bodies >= MaxBodiesPerFrame || _bodies > 0
            && (now - _started) * 1000d / Stopwatch.Frequency >= MaxMilliseconds) return false;
        _bodies++;
        return true;
    }

    internal void Urgent(int frame) { _frame = frame; _bodies = MaxBodiesPerFrame; }
}
