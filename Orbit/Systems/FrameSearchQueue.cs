using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Orbit.Systems;

internal interface IFrameSearch { bool Step(); }

// A step performs at most one uncached path query. Round-robin scheduling and a shared
// elapsed-time budget apply across every caller in the frame, including immediate requests.
internal sealed class FrameSearchQueue
{
    private readonly List<IFrameSearch> _pending = new();
    private readonly Func<double> _clock;
    private readonly double _budgetMs;
    private readonly int _maxSteps;
    private int _frame = -1, _steps;
    private double _spentMs;
    internal FrameSearchQueue(double budgetMs = 2, int maxSteps = 4, Func<double> clock = null)
    { _budgetMs = budgetMs; _maxSteps = maxSteps; _clock = clock ?? (() => Stopwatch.GetTimestamp() * 1000d / Stopwatch.Frequency); }
    internal int Count => _pending.Count;
    internal void Add(IFrameSearch search) { if (!_pending.Contains(search)) _pending.Add(search); }
    internal void Remove(IFrameSearch search) => _pending.Remove(search);
    internal void Pump(int frame)
    {
        if (_frame != frame) { _frame = frame; _steps = 0; _spentMs = 0; }
        while (_pending.Count > 0 && _steps < _maxSteps && _spentMs < _budgetMs)
        {
            var search = _pending[0]; _pending.RemoveAt(0);
            var start = _clock(); _steps++;
            try { if (search.Step()) _pending.Add(search); }
            finally { _spentMs += _clock() - start; }
        }
    }
}
