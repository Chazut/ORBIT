using System.Threading;
using System.Threading.Tasks;

namespace Orbit.Looting;

// EFT/Fika owns these tasks. Cancelling our waiter does not complete the native transaction.
internal sealed class LootTransactionGate
{
    private int _pending;
    internal bool Busy => Volatile.Read(ref _pending) != 0;

    internal void Track(Task task)
    {
        if (task.IsCompleted) return;
        Interlocked.Increment(ref _pending);
        _ = task.ContinueWith((completed, state) =>
        {
            if (completed.IsFaulted) _ = completed.Exception;
            Interlocked.Decrement(ref ((LootTransactionGate)state)._pending);
        }, this, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
