using EFT;

namespace Orbit.Systems;

internal static class NativeGhostZryachiy
{
    internal static bool Supports(BotOwner bot, string decision)
        => decision == "lay" && NativeGhostPartisan.Layer(bot) == "ZryachiyPatrolLayer"
            && bot.Profile.Info.Settings.Role == WildSpawnType.bossZryachiy
            && bot.Memory is { IsInCover: true, IsUnderFire: false, GoalEnemy: null }
            && bot.GetPlayer?.MovementContext?.IsInPronePose == true && bot.GetPlayer.PoseLevel <= 0f;
}
