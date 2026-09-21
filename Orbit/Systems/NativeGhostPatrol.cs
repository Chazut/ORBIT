using System;
using System.Linq.Expressions;
using System.Reflection;
using EFT;
using HarmonyLib;

namespace Orbit.Systems;

/// <summary>Reserve-point metadata is not itself a physical interaction.</summary>
internal static class NativeGhostPatrol
{
    private static readonly FieldInfo ResultField = AccessTools.Field(typeof(AReserveWayAction), "_cuResult");
    private static readonly Func<AReserveWayAction, bool> DropPending = PendingReader(typeof(DropItemReservWay));
    private static readonly Func<AReserveWayAction, bool> HealPending = PendingReader(typeof(DropItemAndHealReservWay));
    private static readonly Func<AReserveWayAction, bool> MedsPending = PendingReader(typeof(UseSurgeKitReservWay));

    private static Func<AReserveWayAction, bool> PendingReader(Type type)
    {
        try
        {
            var field = AccessTools.Field(type, "_shallStartInteract");
            if (field?.FieldType != typeof(bool)) return null;
            var action = Expression.Parameter(typeof(AReserveWayAction), "action");
            return Expression.Lambda<Func<AReserveWayAction, bool>>(
                Expression.Field(Expression.Convert(action, field.DeclaringType), field), action).Compile();
        }
        catch { return null; } // Changed engine contracts keep that script on the awake path.
    }

    private static AReserveWayAction Action(BotOwner bot)
        => bot?.PatrollingData?.CurPatrolPoint?.TargetPoint?.ActionData;

    private static bool Passive(AReserveWayAction action)
    {
        if (action == null) return true;
        var type = action.GetType();
        // Exact audited types only. Sit's current native update returns stay/move even with
        // ShallLoot enabled; actual loot/drop nodes remain guarded separately. Shooting
        // scripts must take the awake path before their update can return a shoot result.
        return type == typeof(WalkReservWay) || type == typeof(SitReservWay);
    }

    private static Func<AReserveWayAction, bool> Pending(AReserveWayAction action)
    {
        var type = action.GetType();
        return type == typeof(DropItemReservWay) ? DropPending
            : type == typeof(DropItemAndHealReservWay) ? HealPending
            : type == typeof(UseSurgeKitReservWay) ? MedsPending : null;
    }

    private static bool PassiveUpdate(AReserveWayAction action)
    {
        if (Passive(action)) return true;
        var pending = Pending(action);
        // Completed or unused one-shot interactions must not keep the squad awake for the
        // rest of its native wait. Inventory/medicine guards still protect in-flight work.
        return pending != null && !pending(action);
    }

    private static bool ActiveInteraction(BotOwner bot)
        => bot?.PatrollingData?.Status == PatrolStatus.stay && !PassiveUpdate(Action(bot));

    internal static string BodyReason(BotOwner bot)
        => bot?.Brain?.LastDecision == BotLogicDecision.alternativePatrol && ActiveInteraction(bot)
            ? "patrol-interaction" : null;

    internal static bool DeferUpdate(BotOwner bot)
    {
        if (!NativeGhostSystem.RetainsNativeState(bot)) return false;
        if (!NativeGhostSystem.OwnsInactiveMovement(bot)) return true;
        return ActiveInteraction(bot)
            && NativeGhostSystem.DeferBodyOperation(bot, "patrol update " + Snapshot(bot));
    }

    internal static bool DeferArrival(BotOwner bot)
    {
        if (!NativeGhostSystem.RetainsNativeState(bot)) return false;
        if (!NativeGhostSystem.OwnsInactiveMovement(bot)) return true;
        if (bot.PatrollingData?.PointControl?.Way?.PatrolType != PatrolType.reserved || Passive(Action(bot))) return false;
        // Defer the whole arrival, before ComeTo changes status/timers or consumes a scripted
        // drop/heal. The original arrival will run on the next awake update at the same point.
        return NativeGhostSystem.DeferBodyOperation(bot, "patrol arrival " + Snapshot(bot));
    }

    internal static ReserveWayResult ManualUpdate(AReserveWayAction action, BotOwner bot)
    {
        if (NativeGhostSystem.RetainsNativeState(bot))
        {
            if (!NativeGhostSystem.OwnsInactiveMovement(bot)) return ReserveWayResult.stay;
            // The native chooser may replace the point inside this same action tick. Check
            // the actual callee before it consumes drop/heal state, including unknown scripts.
            if (!PassiveUpdate(action) && NativeGhostSystem.DeferBodyOperation(bot, "patrol update " + Snapshot(bot)))
                return ReserveWayResult.stay;
        }
        return action.ManualUpdate(bot);
    }

    // Only called inside existing bounded diagnostics or a single wake request, never per frame.
    internal static string Snapshot(BotOwner bot)
    {
        var action = Action(bot);
        var pending = action == null ? null : Pending(action);
        return $"patrolAction={action?.GetType().Name ?? "none"} patrolStatus={bot?.PatrollingData?.Status}"
            + $" patrolResult={(action == null ? "none" : ResultField?.GetValue(action)?.ToString() ?? "unknown")}"
            + $" patrolPending={(pending == null ? "unknown" : pending(action).ToString())}";
    }
}
