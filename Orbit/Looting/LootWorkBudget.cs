using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Orbit.Looting;

// Checkpoints resume on the raid's main thread, with a shared per-frame limit across looters.
// No Unity/inventory operation is dispatched to a worker thread.
internal static class LootWorkBudget
{
    private sealed class Pending
    {
        internal readonly TaskCompletionSource<bool> Completion = new();
        internal CancellationTokenRegistration Cancellation;
    }
    private static readonly Queue<Pending> Waiting = new();
    private static int _frame = -1, _units;
    private static long _start;
    private static bool _running;

    internal static void Reset(bool running)
    {
        _running = running; _frame = -1;
        while (Waiting.Count > 0)
        { var item = Waiting.Dequeue(); item.Cancellation.Dispose(); item.Completion.TrySetCanceled(); }
    }

    private static bool Available()
    {
        if (_frame != Time.frameCount) { _frame = Time.frameCount; _units = 0; _start = Stopwatch.GetTimestamp(); }
        return _units < 8 && (Stopwatch.GetTimestamp() - _start) * 1000d / Stopwatch.Frequency < 2;
    }

    internal static Task Checkpoint(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!_running) return Task.FromCanceled(new CancellationToken(true));
        if (Waiting.Count == 0 && Available()) { _units++; return Task.CompletedTask; }
        var item = new Pending();
        item.Cancellation = token.Register(() => item.Completion.TrySetCanceled());
        Waiting.Enqueue(item);
        return item.Completion.Task;
    }

    internal static void Pump()
    {
        var count = Waiting.Count;
        // Never resume a checkpoint enqueued by a continuation during this pump.
        while (_running && count-- > 0 && Waiting.Count > 0 && Available())
        {
            var item = Waiting.Dequeue(); item.Cancellation.Dispose();
            if (item.Completion.Task.IsCompleted) continue;
            _units++; item.Completion.TrySetResult(true);
        }
    }
}
