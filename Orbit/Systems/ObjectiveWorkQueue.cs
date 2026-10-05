using System.Collections.Generic;

namespace Orbit.Systems;

internal interface IObjectiveWork { void ResumeObjectiveWork(); }

// Carry denied strategy work into subsequent frames in FIFO order. A travelling
// squad must never reserve the calculation slot while it only waits to arrive.
internal sealed class ObjectiveWorkQueue
{
    private readonly List<IObjectiveWork> _pending = new();
    private int _frame = -1;
    private IObjectiveWork _running;
    internal bool CanPump(int frame) => _frame != frame && _pending.Count != 0;

    internal bool TryTake(IObjectiveWork owner, int frame)
    {
        if (ReferenceEquals(_running, owner)) return true;
        if (_frame != frame && _pending.Count == 0) { _frame = frame; return true; }
        if (!_pending.Contains(owner)) _pending.Add(owner);
        return false;
    }

    internal void Cancel(IObjectiveWork owner) => _pending.Remove(owner);

    internal void Pump(int frame)
    {
        if (!CanPump(frame)) return;
        var owner = _pending[0];
        _pending.RemoveAt(0);
        _frame = frame;
        _running = owner;
        try { owner.ResumeObjectiveWork(); }
        finally { _running = null; }
    }
}
