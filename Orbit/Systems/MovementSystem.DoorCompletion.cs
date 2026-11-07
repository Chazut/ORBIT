using EFT;
using EFT.Interactive;
using Orbit.Entities;

namespace Orbit.Systems;

public partial class MovementSystem
{
    private static void FinishWatchedDoorInteraction(Agent agent, Door door)
    {
        var player = agent?.Player;
        var context = player?.MovementContext;
        // The watch has already waited for the animation and verified its door
        // revision. Never stop a newer interaction, loot, medicine or SAIN work.
        if (context == null || player.CurrentState?.Name != EPlayerState.DoorInteraction
            || door.DoorState is not (EDoorState.Open or EDoorState.Breaching)) return;
        var refusal = agent.IsActive != true || agent.IsDormant || agent.Bot == null || agent.Bot.IsDead ? "not-awake-orbit"
            : agent.Bot.Memory.IsUnderFire || agent.Bot.Memory.GoalEnemy != null ? "combat"
            : agent.Bot.Medecine.Using ? "medicine"
            : !ReferenceEquals(context.InteractionInfo.WorldInteractiveObject, door) ? "interaction-changed" : null;
        if (refusal != null)
        {
            Log.Debug($"DOOR COMPLETION: {agent} door={door.Id} action=release-skipped reason={refusal}");
            return;
        }
        try
        {
            context.StopAnyInteractions();
            Log.Info($"DOOR COMPLETION: {agent} door={door.Id} action=release-owned-animation state={door.DoorState}");
        }
        catch (System.Exception ex)
        {
            Log.Warning($"DOOR COMPLETION: {agent} door={door.Id} action=release-failed error={ex.Message}");
        }
    }
}
