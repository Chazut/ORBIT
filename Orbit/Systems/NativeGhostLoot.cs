using System;
using System.Linq.Expressions;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace Orbit.Systems;

// Native loot travel is peaceful. Item operations still need the physical body.
internal static class NativeGhostLoot
{
    private static readonly Func<PatrolLootPointsData, bool> Looting = CreateLootingReader();

    private static Func<PatrolLootPointsData, bool> CreateLootingReader()
    {
        try
        {
            var field = AccessTools.Field(typeof(PatrolLootPointsData), "_lootingNow");
            if (field?.FieldType != typeof(bool)) return null;
            var data = Expression.Parameter(typeof(PatrolLootPointsData), "data");
            return Expression.Lambda<Func<PatrolLootPointsData, bool>>(Expression.Field(data, field), data).Compile();
        }
        catch { return null; }
    }

    internal static bool Supports(BotOwner bot, BotLogicDecision decision)
        => decision == BotLogicDecision.goToLootPointNode && Looting != null
            && bot?.PatrollingData?.LootData?.CachedLootPoint != null;

    private static bool RealLoot(BotOwner bot)
        => bot?.Settings?.FileSettings?.Patrol?.USE_REAL_LOOTING == true;

    internal static string BodyReason(BotOwner bot)
    {
        var data = bot?.PatrollingData?.LootData;
        if (data == null || !RealLoot(bot)) return null;
        if (Looting == null || Looting(data)) return "native-loot-operation";
        var point = data.CachedLootPoint;
        if (bot.Brain?.LastDecision != BotLogicDecision.goToLootPointNode || point == null || point.Empty()) return null;
        var delta = point.Position - bot.Position;
        // Same arrival gate as the native go-to-loot node, before it consumes its arrival state.
        return Mathf.Abs(delta.y) < 1f && delta.x * delta.x + delta.z * delta.z < 3f
            ? "native-loot-arrival" : null;
    }

    internal static bool DeferArrival(BotOwner bot)
    {
        if (!NativeGhostSystem.RetainsNativeState(bot)) return false;
        if (!NativeGhostSystem.OwnsInactiveMovement(bot)) return true;
        var point = bot.PatrollingData?.LootData?.CachedLootPoint;
        return RealLoot(bot) && point != null && !point.Empty()
            && NativeGhostSystem.DeferBodyOperation(bot, "native loot arrival");
    }

    internal static bool DeferUpdate(BotOwner bot)
    {
        if (!NativeGhostSystem.RetainsNativeState(bot)) return false;
        if (!NativeGhostSystem.OwnsInactiveMovement(bot)) return true;
        var data = bot.PatrollingData?.LootData;
        return data != null && RealLoot(bot) && (Looting == null || Looting(data))
            && NativeGhostSystem.DeferBodyOperation(bot, "native loot item operation");
    }
}
