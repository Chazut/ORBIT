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

    internal static bool SupportsStandBy(BotOwner bot, BotLogicDecision decision)
        => decision == BotLogicDecision.standBy
            && bot?.Profile?.Info?.Settings?.Role == WildSpawnType.marksman
            && bot.Memory is { IsUnderFire: false, GoalEnemy: null }
            && bot.StandBy != null
            && NativeGhostPartisan.Layer(bot) == "StandByLogicLayer";

    // Disabling CanDoStandBy alone leaves the native layer selected forever: ShallUseNow reads
    // StandByType, not CanDoStandBy. Release its state while the body is still active. Activate
    // clears the standby cover target without moving the bot or running UpdateNode's teleport fallback.
    internal static void ReleaseStandBy(BotOwner bot)
    {
        if (bot.Brain.LastDecision is not { } decision || !SupportsStandBy(bot, decision)) return;
        var previous = bot.StandBy.StandByType;
        bot.StandBy.Activate();
        Log.Info($"NATIVE GHOST: {bot.Profile.Nickname} sniper standby handoff from={previous}; native decisions resumed");
    }

    // A cached standby decision may survive until the next layer selection. Its node is harmless only
    // after the handoff; never let a newly armed standby run physical cover/teleport operations asleep.
    internal static bool CanKeepStandByDecision(BotOwner bot, BotLogicDecision decision)
        => SupportsStandBy(bot, decision)
            && bot.StandBy is { CanDoStandBy: false, StandByType: BotStandByType.active };
}
