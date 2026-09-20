using System;
using System.Runtime.CompilerServices;
using EFT;
using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Systems;

// Keep the requested goal alongside the exact native path it produced. A partial path's last
// corner is not its goal. Weak keys also release despawned movers that never enter Ghost.
internal static class NativeGhostOrders
{
    internal sealed class GoalScope
    {
        internal BotMover Mover;
        internal Vector3 Target;
        internal GoalScope Parent;
    }

    [ThreadStatic] private static GoalScope _goal;

    internal static GoalScope BeginGoal(BotMover mover, Vector3 target)
        => _goal = new GoalScope { Mover = mover, Target = target, Parent = _goal };

    internal static void EndGoal(GoalScope scope)
    {
        if (scope != null) _goal = scope.Parent;
    }

    internal static Vector3 WayGoal(BotMover mover, Vector3 endpoint, out bool original)
    {
        for (var scope = _goal; scope != null; scope = scope.Parent)
            if (ReferenceEquals(scope.Mover, mover))
            {
                original = true;
                return scope.Target;
            }
        original = false;
        return endpoint;
    }

    private sealed class Order
    {
        internal object Path;
        internal Vector3 Target;
        internal NavMeshPathStatus Status;
        internal string Source;
    }

    private static ConditionalWeakTable<BotMover, Order> Orders = new();

    internal static void Clear() => Orders = new();
    internal static void Forget(BotMover mover) => Orders.Remove(mover);

    internal static bool Valid(Vector3 point)
        => !float.IsNaN(point.x) && !float.IsNaN(point.y) && !float.IsNaN(point.z)
            && !float.IsInfinity(point.x) && !float.IsInfinity(point.y) && !float.IsInfinity(point.z);

    internal static bool ValidWay(Vector3[] way)
    {
        if (way == null || way.Length == 0) return false;
        foreach (var point in way) if (!Valid(point)) return false;
        return true;
    }

    internal static void Record(BotMover mover, Vector3 target, NavMeshPathStatus status, string source)
    {
        var path = mover.ActualPathController?.CurPath;
        if (path == null || !Valid(target) || status == NavMeshPathStatus.PathInvalid) { Forget(mover); return; }
        if (!Orders.TryGetValue(mover, out var order)) Orders.Add(mover, order = new Order());
        order.Path = path;
        order.Target = target;
        order.Status = status;
        order.Source = source;
    }

    internal static void AdoptOnSleep(BotMover mover, NativeGhostNavigation navigation)
    {
        var path = mover.ActualPathController?.CurPath;
        if (path == null) { Forget(mover); return; }
        if (Orders.TryGetValue(mover, out var order) && ReferenceEquals(path, order.Path))
            navigation.Retain(order.Target, path.ReachDist, order.Status, "sleep-" + order.Source);
        else if (path.TargetPoint != null)
            // Unknown author: retain only its explicit path endpoint, never infer a patrol goal.
            navigation.Retain(path.TargetPoint.Position, path.ReachDist, NavMeshPathStatus.PathComplete, "sleep-path-end");
        Forget(mover);
    }
}
