using System;
using EFT.Interactive;
using Orbit.Entities;
using Orbit.Navigation;
using Orbit.Systems;
using UnityEngine;
using UnityEngine.AI;

internal static class ExfilLookaheadTests
{
    private sealed class Trip
    {
        internal readonly Scenario Scene = new();
        internal readonly Agent Bot;
        internal readonly ExfilApproach Approach;
        internal readonly Vector3 First = new(-90, 0, 0);
        internal readonly Vector3 Second = new(-50, 0, 0);
        internal int PreviewQueries;
        internal bool Miss;

        internal Trip(bool ghost = false)
        {
            Scene.Car.Settings.ExfiltrationType = EExfiltrationType.Individual;
            Scene.Car.Status = EExfiltrationStatus.RegularMode;
            Bot = Scene.Add(true, new(-130, 0, 0), ObjectiveStatus.None);
            Bot.IsDormant = ghost;
            Approach = new(Bot, Scene.Movement, Scene.Waypoints);
            NavMesh.Sampler = (point, radius) => point;
            NavMesh.Route = (from, to, path) =>
            {
                if (from.Equals(First))
                {
                    PreviewQueries++;
                    if (Miss) return false;
                }
                var boundary = from.x < -100 ? First : Second;
                var partial = from.x < -60 && to.x > boundary.x;
                path.status = partial ? NavMeshPathStatus.PathPartial : NavMeshPathStatus.PathComplete;
                path.corners = new[] { from, partial ? boundary : to };
                return true;
            };
            Tick(20);
        }

        internal void Tick(int count = 1)
        {
            for (var i = 0; i < count; i++)
            {
                Time.time += .02f;
                Approach.Tick(true);
            }
        }
    }

    internal static void Run(Action<bool, string> check)
    {
        foreach (var ghost in new[] { false, true })
        {
            var t = new Trip(ghost);
            check(t.Scene.Movement.Destination.Equals(t.First) && t.PreviewQueries == 0,
                "a distant staged exit orders only the first leg");
            t.Bot.Position = new(-99, 0, 0);
            var active = t.Bot.Movement.Path;
            t.Scene.Waypoints.WorkAvailable = false;
            t.Tick(3);
            check(t.PreviewQueries == 0, "speculation respects the shared navigation budget");
            t.Scene.Waypoints.WorkAvailable = true;
            t.Tick(20);
            var queries = NavMesh.PathQueries;
            t.Bot.Position = new(-92.1f, 0, 0);
            t.Tick(5);
            check(t.PreviewQueries > 0 && queries == NavMesh.PathQueries && t.Scene.Movement.Orders == 1
                && ReferenceEquals(active, t.Bot.Movement.Path), "a ready route cannot replace or skip the current waypoint");
            t.Bot.Position = new(-91.9f, 0, 0);
            t.Tick();
            check(t.Scene.Movement.Destination.Equals(t.Second) && t.Bot.Movement.Path[0].Equals(t.First)
                && t.Bot.Movement.Status == MovementStatus.Moving && t.Bot.Movement.Sprint
                && t.Scene.Movement.Orders == 2 && NavMesh.PathQueries == queries && t.Bot.Bot.Mover.Stops == 0,
                "crossing the original arrival radius hands off the verified path in one tick without a new query");
            check(t.Bot.Objective.Location == t.Scene.Exit && t.Bot.Objective.Status == ObjectiveStatus.Moving,
                "a prepared leg never completes the extraction objective");
        }

        var pending = new Trip();
        pending.Bot.Position = new(-96, 0, 0);
        pending.Tick();
        pending.Bot.Position = pending.First;
        var pendingQueries = NavMesh.PathQueries;
        pending.Scene.Waypoints.WorkAvailable = false;
        pending.Tick(2);
        check(pending.Scene.Movement.Orders == 1 && NavMesh.PathQueries == pendingQueries,
            "arrival cannot spend extra work while the navigation budget is busy");
        pending.Scene.Waypoints.WorkAvailable = true;
        pending.Tick(20);
        check(pending.Scene.Movement.Destination.Equals(pending.Second) && pending.Scene.Movement.Orders == 2,
            "a sliced search can finish after arrival and dispatch once");

        var failed = new Trip();
        failed.Miss = true;
        failed.Bot.Position = new(-96, 0, 0);
        failed.Tick(20);
        var attempts = failed.PreviewQueries;
        failed.Tick(20);
        check(attempts > 0 && attempts == failed.PreviewQueries && failed.Scene.Movement.Orders == 1,
            "an unsuccessful preview is attempted only once while walking");
        failed.Bot.Position = new(-91, 0, 0);
        failed.Tick(20);
        check(failed.Scene.Movement.Orders == 2 && failed.Scene.Movement.Destination.x > failed.First.x
            && failed.Bot.Movement.Path[0].Equals(failed.Bot.Position),
            "a preview miss falls back immediately from the actual arrival position");

        var blocked = new Trip();
        blocked.Bot.Position = new(-97, 0, 0);
        blocked.Tick(20);
        blocked.Bot.Position = new(-91, 0, 0);
        NavMesh.Connected = false; // A wall now separates the feet from the cached first corner.
        blocked.Tick();
        check(blocked.Scene.Movement.Orders == 1, "a blocked join cannot start a cached path through a wall");
        NavMesh.Connected = true;
        blocked.Tick(20);
        check(blocked.Bot.Movement.Path[0].Equals(blocked.Bot.Position), "blocked join replans from the bot");

        var danger = new Trip();
        danger.Bot.Position = new(-98, 0, 0);
        danger.Tick(20);
        danger.Bot.GrenadeInterruptions = new GrenadeMemory();
        danger.Bot.Position = new(-91, 0, 0);
        danger.Tick(20);
        check(danger.Scene.Movement.Orders == 2 && danger.Bot.Movement.Path[0].Equals(danger.Bot.Position),
            "an armed grenade detour gets a normal movement order from the actual feet");

        var mined = new Trip();
        mined.Bot.Position = new(-97, 0, 0);
        mined.Tick(20);
        mined.Bot.Position = mined.First;
        DirectionalMineSafety.Blocked = true;
        mined.Tick();
        check(mined.Scene.Movement.Orders == 1 && mined.Bot.Movement.Status == MovementStatus.Moving,
            "a mine veto rejects the prepared path without writing a movement failure");

        var combat = new Trip();
        combat.Bot.Position = new(-97, 0, 0);
        combat.Tick(20);
        combat.Bot.IsActive = false;
        combat.Approach.ResumeObjectiveWork();
        combat.Bot.Position = combat.First;
        combat.Approach.ResumeObjectiveWork();
        check(combat.Scene.Movement.Orders == 1, "queued work cannot move a bot during combat");
        combat.Bot.IsActive = true;
        var prior = NavMesh.PathQueries;
        combat.Tick(20);
        check(NavMesh.PathQueries > prior && combat.Scene.Movement.Destination.Equals(combat.Second),
            "combat return searches again from the actual position");

        var stalled = new Trip();
        stalled.Bot.Position = new(-98, 0, 0);
        stalled.Tick(20);
        stalled.Bot.Movement.Status = MovementStatus.Failed;
        Time.time += 3f;
        // The watchdog counts active time in capped intervals.
        for (var i = 0; i < 3; i++) { Time.time += 1f; stalled.Approach.Tick(true); }
        check(stalled.Bot.Movement.Status == MovementStatus.Stopped && stalled.Scene.Movement.Orders == 1,
            "failing the current leg drops even a ready continuation");
        prior = NavMesh.PathQueries;
        stalled.Tick(20);
        check(NavMesh.PathQueries == prior, "a real failed leg still observes the retry delay");

        var changed = new Trip();
        changed.Bot.Position = new(-98, 0, 0);
        changed.Tick(20);
        changed.Bot.Objective.Location = null;
        changed.Approach.ResumeObjectiveWork();
        changed.Bot.Position = changed.First;
        changed.Approach.ResumeObjectiveWork();
        check(changed.Scene.Movement.Orders == 1, "reassignment invalidates queued extraction work");
    }
}
