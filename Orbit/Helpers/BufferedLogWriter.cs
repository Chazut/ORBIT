using System;
using System.Collections.Generic;
using System.Threading;

namespace Orbit.Helpers;

internal sealed class BufferedLogWriter
{
    private readonly object _gate = new();
    private readonly Queue<string> _pending = new();
    private readonly AutoResetEvent _ready = new(false);
    private readonly Action<string> _write;
    private readonly Thread _thread;
    private readonly int _maxMessages, _maxCharacters;
    private int _characters, _dropped, _failures;
    private bool _stopping;
    private long _accepted, _processed;

    internal BufferedLogWriter(Action<string> write, int maxMessages = 8192, int maxCharacters = 2 * 1024 * 1024)
    {
        _write = write ?? throw new ArgumentNullException(nameof(write));
        if (maxMessages < 1 || maxCharacters < 1) throw new ArgumentOutOfRangeException(nameof(maxMessages));
        _maxMessages = maxMessages;
        _maxCharacters = maxCharacters;
        _thread = new Thread(Run) { IsBackground = true, Name = "ORBIT verbose logs" };
        _thread.Start();
    }

    internal bool Enqueue(string message)
    {
        if (message == null) return true;
        lock (_gate)
        {
            if (_stopping) return false;
            if (_pending.Count >= _maxMessages || message.Length > _maxCharacters - _characters)
            {
                Interlocked.Increment(ref _dropped);
                return false;
            }
            _pending.Enqueue(message);
            _accepted++;
            _characters += message.Length;
            if (_pending.Count == 1) _ready.Set();
            return true;
        }
    }

    internal int TakeDroppedCount() => Interlocked.Exchange(ref _dropped, 0);
    internal int TakeFailureCount() => Interlocked.Exchange(ref _failures, 0);

    // Called only by debug-export workers, never by the Unity thread. Wait for messages
    // already accepted, rather than for an empty queue that active logging may never leave.
    internal bool Drain(int timeoutMilliseconds)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        lock (_gate)
        {
            var target = _accepted;
            while (_processed < target)
            {
                var remaining = timeoutMilliseconds - (int)timer.ElapsedMilliseconds;
                if (remaining <= 0) return false;
                Monitor.Wait(_gate, remaining);
            }
            return true;
        }
    }

    internal bool Stop(int timeoutMilliseconds)
    {
        lock (_gate)
        {
            if (!_stopping)
            {
                _stopping = true;
                _ready.Set();
            }
        }
        return _thread.Join(timeoutMilliseconds);
    }

    private void Run()
    {
        var batch = new string[128];
        try
        {
            while (true)
            {
                int count;
                bool stopping;
                lock (_gate)
                {
                    count = Math.Min(batch.Length, _pending.Count);
                    for (var i = 0; i < count; i++)
                    {
                        batch[i] = _pending.Dequeue();
                        _characters -= batch[i].Length;
                    }
                    stopping = _stopping;
                }
                if (count == 0)
                {
                    if (stopping) return;
                    _ready.WaitOne();
                    continue;
                }
                for (var i = 0; i < count; i++)
                {
                    try { _write(batch[i]); }
                    catch { Interlocked.Increment(ref _failures); }
                    batch[i] = null;
                }
                lock (_gate) { _processed += count; Monitor.PulseAll(_gate); }
                // BepInEx already buffers disk writes. Keep individual events and their normal prefix.
                if (count == batch.Length) Thread.Yield();
            }
        }
        finally { _ready.Dispose(); }
    }
}
