using EFT;
using Orbit.Navigation;
using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Systems;

// Only a travelling, ordinary patrol can ask its own chooser for another point. Followers,
// reserve scripts, checkpoint jobs and boss-specific controllers retain their own policies.
internal static class NativeGhostPatrolRecovery
{
    internal static void Update(BotOwner bot, string decision, NativeGhostNavigation navigation)
    {
        if (!navigation.HasUnhandledFailure || !navigation.Target.HasValue || decision != "simplePatrol"
            || !NativeGhostSystem.OwnsInactiveMovement(bot) || NativeGhostSystem.MovementPinned(bot)
            || bot.IsDead || bot.BotState != EBotState.Active || bot.Profile.Info.Settings.Role == WildSpawnType.marksman
            || bot.BotFollower?.BossToFollow != null || bot.Memory.GoalEnemy != null || bot.Memory.IsUnderFire
            || bot.Mover.Pause && bot.Mover.RemainPause > 0f) return;
        var patrol = bot.PatrollingData;
        var chooser = patrol?.PointChooser;
        if (patrol?.Status != PatrolStatus.go || patrol.CurPatrolPoint?.TargetPoint == null
            || patrol.PointControl?.Way?.PatrolType != PatrolType.patrolling
            || patrol.PatrolMove?.GetType() != typeof(PatrolMoveSimple)
            || chooser == null) return;
        var type = chooser.GetType();
        if (type != typeof(PatrolPointChooserBasic) && type != typeof(PatrolPointChooserByData)
            && type != typeof(PatrolPointChooserBoss)) return;
        var failed = navigation.Target.Value;
        if (Vector3.Distance(patrol.CurTargetPoint, failed) > 0.5f
            || PatrolMoveSimple.IsCome(bot, patrol.CurTargetPoint, false, out _)
            || !NativeGhostNavigation.TakeQuery() || !navigation.TakeFailure()) return;

        // No arrival callback, timer reset or made-up point. The native chooser keeps its
        // faction/way/ownership restrictions. Reject its fallback if it ignores the filter.
        var next = chooser.FindNextPoint(false, true, canCut: false, pointFilter: point =>
            NativeGhostOrders.Valid(point.Position) && Vector3.Distance(point.Position, failed) > 1f);
        var reason = "no-alternative";
        if (next?.TargetPoint != null && NativeGhostOrders.Valid(next.Position)
            && next.TargetPoint.Owner == null && next.TargetPoint.IsFreeFor(bot)
            && (type == typeof(PatrolPointChooserBasic) || next.TargetPoint.SubPointsCount > bot.Boss.Followers.Count)
            && Vector3.Distance(next.Position, failed) > 1f && Vector3.Distance(next.Position, bot.Position) > 1f
            && !DangerZones.IsInside(next.Position))
        {
            var path = new NavMeshPath();
            var reach = Mathf.Max(0.1f, bot.Settings.FileSettings.Move.REACH_DIST);
            var calculated = NavMesh.CalculatePath(bot.Position, next.Position, NavMesh.AllAreas, path);
            var corners = path.corners;
            reason = "unusable-alternative";
            if (calculated && path.status == NavMeshPathStatus.PathComplete && corners.Length >= 2
                && NativeGhostOrders.ValidWay(corners)
                && Vector3.Distance(corners[corners.Length - 1], next.Position) < reach
                && NativeGhostNavigation.CanStartRoute(bot.Position, corners))
            {
                // SetTarget invokes the original PointSetted/GoToPoint sequence. A new order
                // replaces the failed one through the same controller as every other request.
                patrol.PointControl.SetTarget(next);
                Log.Info($"NATIVE GHOST: {bot.Profile.Nickname} native patrol retry: from={failed} to={next.Position} chooser={type.Name}; native selection resumes");
                return;
            }
        }
        Log.Info($"NATIVE GHOST: {bot.Profile.Nickname} native patrol retry deferred: reason={reason} target={failed}; original order retained");
    }
}
