using EFT;

namespace Orbit.Systems;

internal static class NativeGhostMarksman
{
    internal static bool IsPeacefulLayer(BotOwner bot)
        => bot?.Profile?.Info?.Settings?.Role == WildSpawnType.marksman
            && bot.Memory is { IsUnderFire: false, GoalEnemy: null }
            && NativeGhostPartisan.Layer(bot) == "MarksmanTargetLayer";

    // The native lay node can keep watching from cover once its physical posture is settled.
    // Its combat sibling uses the same action, so permission must also match the peaceful layer.
    internal static bool SupportsLay(BotOwner bot, BotLogicDecision decision)
        => decision == BotLogicDecision.lay && IsPeacefulLayer(bot) && bot.Memory.IsInCover
            && bot.GetPlayer?.MovementContext?.IsInPronePose == true && bot.GetPlayer.PoseLevel <= 0f;
}
