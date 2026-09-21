using System;

namespace Orbit.Looting;

// A body-backed async operation must finish before sleep. Search delays and inventory-only work
// do not take a lease, so a long loot session can transfer to Ghost between animated operations.
internal sealed class LootBodyGate
{
    private int _operations;
    internal bool Busy => _operations != 0;
    internal Lease Enter(bool animated)
    {
        if (animated) _operations++;
        return new Lease(animated ? this : null);
    }
    internal readonly struct Lease(LootBodyGate gate) : IDisposable
    {
        public void Dispose() { if (gate != null) gate._operations--; }
    }
}
