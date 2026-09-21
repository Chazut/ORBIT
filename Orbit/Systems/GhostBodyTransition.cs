using EFT;

namespace Orbit.Systems;

internal static class GhostBodyTransition
{
    internal static bool Busy(Player player)
        => player?.InventoryController?.IsChangingWeapon == true
           || player?.HandsController?.IsInInteraction() == true
           || player?.MovementContext?.IsAnimatorInteractionOn == true
           || player?.MovementContext?.GetHandProgress() > 0f
           || player?.CurrentState?.Name == EPlayerState.Pickup;
}
