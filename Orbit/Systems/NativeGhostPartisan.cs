using EFT;
using UnityEngine;

namespace Orbit.Systems;

/// <summary>Partizan's mine approach remains native; placing a tripwire still requires an active body.</summary>
internal static class NativeGhostPartisan
{
    public static string Layer(BotOwner bot) => bot?.Brain?.BaseBrain?.CurLayerInfo?.GetType().Name;

    public static bool IsPartisan(BotOwner bot)
        => bot?.Profile?.Info?.Settings?.Role == WildSpawnType.bossPartisan;

    public static bool IsMineLayer(string layer)
        => layer is "PartisanPlantingTargetLayer" or "PartisanPlantingTargetManyLayer";

    public static bool Supports(BotOwner bot, string decision)
        => IsPartisan(bot) && bot.Boss?.BossLogic is BossPartisan && IsMineLayer(Layer(bot))
            && decision is "goToPointTactical" or "goToCoverPointTactical";

    // BSG can record a quarry through its tracking logic without seeing or fighting it.
    // This exception is confined to mine preparation and never clears that original memory.
    public static bool CanRetainEnemy(BotOwner bot)
    {
        if (!IsPartisan(bot) || bot.Boss?.BossLogic is not BossPartisan || !IsMineLayer(Layer(bot))) return false;
        return IsDistantMemory(bot);
    }

    internal static bool IsDistantMemory(BotOwner bot)
    {
        var enemy = bot?.Memory?.GoalEnemy;
        if (enemy?.Person == null || bot.Memory.IsUnderFire || enemy.IsVisible || enemy.CanShoot) return false;
        if (Time.time - enemy.PersonalLastSeenTime < 30f || Time.time - enemy.TimeLastSeenReal < 30f) return false;
        const float minimumSqr = 150f * 150f;
        return (bot.Position - enemy.Person.Position).sqrMagnitude > minimumSqr
            && (bot.Position - enemy.CurrPosition).sqrMagnitude > minimumSqr;
    }

    public static string BodyReason(BotOwner bot)
    {
        if (!IsPartisan(bot)) return null;
        if (!NativeGhostSystem.ReachOrderReady) return "partisan-reach-bridge";
        if (bot.Boss?.BossLogic is not BossPartisan boss || boss._period == null) return "partisan-controller";
        if (bot.MinesData == null || bot.MinesData.Planting) return "partisan-planting";
        if (boss._listOfPrewarms == null || boss._listOfPrewarms.Count > 0) return "partisan-prewarm";
        var weapons = bot.WeaponManager;
        if (weapons == null || weapons.IsMelee || !weapons.HaveBullets || weapons.Reload == null || weapons.Reload.Reloading
            || weapons.Reload.BulletCount < weapons.Reload.MaxBulletCount * 0.5f
            || weapons.UnderbarrelLauncherController == null
            || weapons.UnderbarrelLauncherController.NeedToReload()) return "partisan-weapon-interaction";
        if (bot.VoxelesPersonalData?.CurVoxel == null) return "partisan-voxel-data";
        return null;
    }

    public static void UpdateTracking(BotOwner bot)
    {
        if (IsPartisan(bot) && bot.Boss?.BossLogic is BossPartisan boss)
        {
            boss._period.Update();
        }
        // Do not call BossLogicUpdate: it also consumes prewarm mines through InventoryController.
        // The caller rechecks BodyReason after the tracking timer and wakes before placing anything.
    }

    internal static bool ReleaseInvalidCover(BotOwner bot, NativeGhostNavigation navigation)
    {
        if (!navigation.PersistentlyInvalid || !IsPartisan(bot)
            || !NativeGhostSystem.OwnsInactiveMovement(bot) || NativeGhostSystem.MovementPinned(bot)
            || bot.IsDead || bot.BotState != EBotState.Active
            || bot.Brain.LastDecision != BotLogicDecision.goToCoverPointTactical
            || bot.Brain.BaseBrain?.CurLayerInfo is not PartisanPlantingTargetManyLayer layer
            || bot.Mover.Pause && bot.Mover.RemainPause > 0f
            || bot.Mover.ActualPathController.HavePath || bot.Memory.IsInCover || bot.Memory.IsUnderFire
            || bot.Memory.GoalEnemy != null && !IsDistantMemory(bot) || BodyReason(bot) != null)
            return false;
        var cover = bot.Memory.CurCustomCoverPoint;
        if (cover == null || !ReferenceEquals(layer._cachePoint, cover)
            || layer._cachePoints == null || !layer._cachePoints.Contains(cover)
            || !navigation.Target.HasValue || (navigation.Target.Value - cover.Position).sqrMagnitude > 0.01f
            || !navigation.ConfirmInvalidPath()) return false;

        // EndGoToCoverPointTactical ends when its cover is absent. The next native GetDecision
        // skips this already visited cover and chooses the mine approach itself. Keep its mine,
        // visited-cover history and enemy memory intact; never fabricate arrival or plant a mine.
        layer._cachePoint = null;
        bot.Memory.SetCoverPoints(null);
        navigation.Cancel();
        bot.Mover.ActualPathController.Stop();
        bot.Mover.IsMoving = false;
        Log.Info($"NATIVE GHOST: {bot.Profile.Nickname} partisan cover retry: released unreachable cover={cover.Position}; native selection resumes");
        return true;
    }
}
