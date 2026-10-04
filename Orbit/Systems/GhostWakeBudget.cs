using System.Diagnostics;

namespace Orbit.Systems;

// Global across every group. Each activation finishes before its nonblocking cooldown begins.
internal sealed class GhostWakeBudget
{
    private int _frame = -1;
    private long _nextAllowed;
    private bool _active;

    internal bool TryBegin(int frame, long now)
    {
        if (_active || _frame == frame || now < _nextAllowed) return false;
        _frame = frame;
        _active = true;
        return true;
    }

    internal void Complete(long now, float delayMilliseconds)
    {
        if (float.IsNaN(delayMilliseconds) || float.IsInfinity(delayMilliseconds)) delayMilliseconds = 50f;
        delayMilliseconds = System.Math.Max(0f, System.Math.Min(500f, delayMilliseconds));
        _nextAllowed = now + (long)(delayMilliseconds * Stopwatch.Frequency / 1000d);
        _active = false;
    }

    // An urgent group bypasses the queue; ordinary work cannot follow in the same frame.
    internal void Urgent(int frame) { _frame = frame; }
    internal void Reset() { _frame = -1; _nextAllowed = 0; _active = false; }
}
