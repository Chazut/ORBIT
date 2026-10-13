using Orbit.Entities;
using Orbit.Systems;

namespace Orbit.Looting;

public partial class OrbitLootHandler
{
    private readonly LootBodyGate _bodyGate = new();
    private readonly LootTransactionGate _inventoryGate = new();
    internal bool CanEnterGhost => !_bodyGate.Busy && !_inventoryGate.Busy && !GhostBodyTransition.Busy(_bot?.GetPlayer);

    internal void TrackInventoryTransaction(System.Threading.Tasks.Task task) => _inventoryGate.Track(task);

    internal void EnterGhost(Agent agent)
    {
        if (LootTaskRunning && !DormantMode)
        {
            // Complete the same session, preserving its target, cancellation token and pickup totals.
            // Release the session's live mover hold before dormancy takes ownership of the body.
            UnfreezeBotAfterLootSession();
            if (_swappedWeaponIds.Count != 0) agent.GhostHandsResync = true;
            Log.Info($"{agent} loot handoff to Ghost: active session retained");
        }
        DormantMode = true;
    }
}
