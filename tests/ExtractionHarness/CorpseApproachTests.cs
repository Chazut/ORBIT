using System;
using EFT.Interactive;
using Orbit.Entities;
using Orbit.Navigation;
using UnityEngine;
using UnityEngine.AI;

internal static class CorpseApproachTests
{
    private static (Scenario scene, Agent bot, Corpse body) At(float distance, bool ghost)
    {
        var scene = new Scenario();
        scene.Squad.ExtractRequested = false;
        var body = new Corpse { Pelvis = new Transform { position = Vector3.zero } };
        body.transform.position = new(20, 0, 0);
        var waypoint = new Waypoint(77, WaypointCategory.Corpse, "body", body.transform.position,
            CorpseApproach.PreferredSqr, new(), new(), body);
        var bot = scene.Add(false, new(distance, 0, 0), ObjectiveStatus.None, location: waypoint);
        bot.IsDormant = ghost;
        scene.Tick(1);
        return (scene, bot, body);
    }

    internal static void Run(Action<bool,string> check)
    {
        foreach (bool alreadyProne in new[] { false, true })
        foreach (bool clearance in new[] { false, true })
        {
            check(!CorpseApproach.PreferProne(false, false, alreadyProne, clearance),
                "ordinary scavs, bosses and guards crouch even when arriving prone");
            check(CorpseApproach.PreferProne(true, false, alreadyProne, clearance) == (alreadyProne || clearance),
                "PMC keeps prone or lies down when there is clearance");
            check(CorpseApproach.PreferProne(false, true, alreadyProne, clearance) == (alreadyProne || clearance),
                "simulated player scav follows the same posture rule as a PMC");
        }
        foreach (bool ghost in new[] { false, true })
        {
            foreach (float distance in new[] { .8f, 1f, 2.75f, 3f, 3.01f })
            {
                var (scene, bot, body) = At(distance, ghost);
                check((scene.Movement.Destination - body.Pelvis.position).sqrMagnitude < .001f, "approach targets the live ragdoll");
                scene.Tick(2);
                if (distance <= CorpseApproach.Preferred)
                {
                    check(bot.Objective.Status == ObjectiveStatus.Looting, "close body accessible without fallback");
                    continue;
                }
                check(bot.Objective.Status == ObjectiveStatus.Moving, "entering 3m cannot start looting by itself");
                bot.Movement.Status = MovementStatus.Stopped;
                for (int i = 0; i < CorpseApproach.Samples; i++) scene.Tick(5 + i * .25f);
                check(bot.Objective.Status == ObjectiveStatus.Moving, "exhaust close approaches before using fallback");
                scene.Tick(10);
                check((bot.Objective.Status == ObjectiveStatus.Looting) == (distance <= 3f), "3m is an inclusive hard limit");
                if (distance <= 3f)
                    check(CorpseApproach.WithinLootReach(distance * distance, bot.Objective.CorpseLootReachSqr), "approved session permits actual transfer range");
            }

            var (reachable, walker, _) = At(2.5f, ghost);
            walker.Movement.Status = MovementStatus.Failed;
            NavMesh.Sampler = (seed, radius) => new Vector3(.4f, 0, 0);
            reachable.Tick(5);
            check(walker.Objective.Status == ObjectiveStatus.Moving && reachable.Movement.Orders == 2
                && reachable.Movement.Destination.x == .4f, "bot walks closer before claiming loot");
            walker.Position = reachable.Movement.Destination;
            reachable.Tick(6);
            check(walker.Objective.Status == ObjectiveStatus.Looting && walker.Objective.CorpseLootReachSqr == CorpseApproach.PreferredSqr,
                "reachable closer approach gets only normal session reach");

            var (moving, follower, ragdoll) = At(2.5f, ghost);
            ragdoll.Pelvis.position = new(5, 0, 0);
            moving.Tick(2); moving.Tick(3);
            check(moving.Movement.Destination.x == 5f && follower.Objective.Status == ObjectiveStatus.Moving, "a sliding body gets a fresh approach");

            var (blocked, stuck, _) = At(2f, ghost);
            stuck.Movement.Status = MovementStatus.Stopped;
            Physics.Blocked = true;
            Physics.FirstHit = new Collider { name = "wall" };
            for (int i = 0; i <= CorpseApproach.Samples; i++) blocked.Tick(5 + i * .25f);
            check(stuck.Objective.Status != ObjectiveStatus.Looting, "fallback cannot loot through a wall, including in Ghost");

            var (upstairs, below, _) = At(2f, ghost);
            upstairs.Waypoints.FloorReached = false;
            below.Movement.Status = MovementStatus.Failed;
            NavMesh.Sampler = (seed, radius) => seed;
            NavMesh.Connected = false;
            for (int i = 0; i <= CorpseApproach.Samples; i++) upstairs.Tick(5 + i * .25f);
            check(upstairs.Movement.Orders == 1 && below.Objective.Status == ObjectiveStatus.Failed,
                "disconnected approaches are skipped and fallback from another floor is refused");
        }
        check(!CorpseApproach.WithinLootReach(1f, 0f), "fresh session cannot borrow fallback reach");
        check(!CorpseApproach.WithinLootReach(2.6f * 2.6f, 2.5f * 2.5f), "a body moving away interrupts a fallback transfer");
        check(!CorpseApproach.WithinLootReach(3.01f * 3.01f, 100f), "transfer always enforces the 3m cap");
    }
}
