using EFT;
using Orbit.Patches;

namespace Orbit.Systems;

internal static class NativeGhostCover
{
    // These layers use the same movement nodes as combat layers, but choose patrol/guard covers.
    internal static bool Supports(BotOwner bot, string decision, NativeGhostAdapters adapter = null)
    {
        if (!NativeGhostBodyPatches.Ready || !NativeGhostSystem.ReachOrderReady
            || bot.VoxelesPersonalData?.CurVoxel == null
            || decision is not ("goToCoverPoint" or "runToCover")) return false;
        return NativeGhostPartisan.Layer(bot) is
            "BirdEyePatrolLayer" or "PatrolStayAtPositionLayer" or "PatrolStayAtPositionLayerWithIndoor" or
            "BoarClosePatrolLayer" or "BoarPatrolLayer" or "BossBoarPatrolLayer" or
            "HoldNearBossLayer" or "KolontayHoldNearBossLayer" or
            "PatrolAssaultLayer" or "FullMapPatrolLayer" or "FollowerPatrolLayer"
            || SupportsCheckpointTarget(bot, adapter)
            || NativeGhostMarksman.IsPeacefulLayer(bot)
            || NativeGhostPartisan.IsPartisan(bot) && NativeGhostPartisan.IsMineLayer(NativeGhostPartisan.Layer(bot));
    }

    internal static bool SupportsCheckpointTarget(BotOwner bot, NativeGhostAdapters adapter)
        => adapter?.Checkpoint == "RUAF" && adapter.Valid()
            && NativeGhostPartisan.Layer(bot) == "AdvAssaultTargetLayer"
            && bot.Memory != null && bot.Memory.GoalEnemy == null && !bot.Memory.IsUnderFire;

    internal static void UpdateVoxel(BotOwner bot)
    {
        var voxels = bot.VoxelesPersonalData;
        if (voxels != null) voxels.SetCurrectVoxel(voxels.GetVoxelSafe(bot.Position));
    }
}
