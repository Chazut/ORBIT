using System.Collections.Generic;

namespace Orbit.Helpers;

// Pure sampling policy: bounded by actor count, interval and reports per frame.
internal sealed class RotationProbeWindow
{
    internal sealed class Window
    {
        internal float NextSample, NextReport;
    }

    private readonly Dictionary<string, Window> _windows = new();
    private int _frame = -1, _reports;
    internal int Count => _windows.Count;

    internal Window Get(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        if (_windows.TryGetValue(id, out var value)) return value;
        if (_windows.Count >= 128) return null;
        return _windows[id] = new Window();
    }

    internal bool Sample(Window window, float now)
    {
        if (window == null || now < window.NextSample) return false;
        window.NextSample = now + 1f;
        return true;
    }

    internal bool Report(Window window, float now, int frame)
    {
        if (window == null || now < window.NextReport) return false;
        if (_frame != frame) { _frame = frame; _reports = 0; }
        if (_reports >= 2) return false;
        _reports++;
        window.NextReport = now + 15f;
        return true;
    }

    internal void Clear()
    {
        _windows.Clear();
        _frame = -1;
        _reports = 0;
    }
}
