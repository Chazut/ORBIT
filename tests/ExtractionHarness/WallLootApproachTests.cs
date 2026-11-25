using System;
using EFT.Interactive;
using Orbit.Entities;
using Orbit.Navigation;
using UnityEngine;
using UnityEngine.AI;

internal static class WallLootApproachTests
{
    // Customs, FadeMan_TV at 334.8 seconds. Endpoints extracted from the suitcase collider
    // and the installed Waypoints bigmap mesh. The original waypoint lies outside the warehouse.
    private static readonly Vector3 feet = new(421.29f, 1.25f, -87.45f);
    private static readonly Vector3 waypoint = new(420.49f, 1.25f, -87.97f);
    private static readonly Vector3 centre = new(419.36645f, 1.867f, -87.83107f);
    private static readonly Vector3 stand = new(418.8529f, 1.4f, -87.8008f);
    internal static void Run(Action<bool,string> check)
    {
        foreach (var arrival in new[] { "waypoint", "nav-snap", "blocked-session" })
        foreach (bool connected in new[] { true, false })
        foreach (bool dormant in new[] { false, true })
        {
            var scene = new Scenario();
            scene.Squad.ExtractRequested = false;
            var suitcase = new LootableContainer { Collider = new Collider {
                bounds = new Bounds { min = centre - Vector3.up * .15f, max = centre + Vector3.up * .15f } } };
            var loc = new Waypoint(304, WaypointCategory.ContainerLoot, "plastic_suitcase_140_open_cover",
                waypoint, arrival == "nav-snap" ? .25f : 1f, new(), new(), suitcase);
            var bot = scene.Add(false, feet, ObjectiveStatus.None, location: loc);
            bot.IsDormant = dormant;
            var partition = new Collider { name = "concrete 1", transform = new Transform {
                parent = new Transform { name = "sklad_concrete" } },
                SurfaceProbe = ray => new RaycastHit { distance = .1f, normal = -ray.direction } };
            Physics.SegmentHit = (a,b) =>
            {
                // Local plane of the concrete partition, from the map collision mesh.
                var normal = new Vector3(.99f,0,-.15f);
                float da = Vector3.Dot(a - new Vector3(420.16f,1.8f,-87.6f), normal);
                float db = Vector3.Dot(b - new Vector3(420.16f,1.8f,-87.6f), normal);
                if (da * db >= 0) return null;
                float t = da / (da-db);
                return new RaycastHit { collider = partition, normal = normal, point = a+(b-a)*t, distance = (b-a).magnitude*t };
            };
            NavMesh.Sampler = (seed, radius) => stand;
            NavMesh.Route = (start,end,path) =>
            {
                check((start-feet).sqrMagnitude < .001f && (end-stand).sqrMagnitude < .001f, "path query uses bot feet and item-side stand");
                path.status = connected ? NavMeshPathStatus.PathComplete : NavMeshPathStatus.PathPartial;
                return true;
            };
            try
            {
                scene.Tick(1);
                if (arrival == "nav-snap") bot.Movement.Status = MovementStatus.Stopped;
                if (arrival == "blocked-session")
                {
                    bot.Objective.Status = ObjectiveStatus.Looting;
                    WallLootApproach.ResumeAfterBlockedSession(bot);
                }
                else scene.Tick(2);
                check(bot.Objective.Status == ObjectiveStatus.Moving, "cannot open the suitcase through the warehouse partition");
                scene.Tick(3);
                if (!connected)
                {
                    for (int i=0; i<26; i++) scene.Tick(3.1f+i*.15f);
                    check(bot.Objective.Status == ObjectiveStatus.Failed && scene.Movement.Orders == 1, "partial paths exhaust the search without a claim");
                    continue;
                }
                check(scene.Movement.Orders == 2 && scene.Movement.Destination.Equals(stand), "orders normal travel to the accessible side");
                bot.Position = new Vector3(419.5f,.85f,-82.5f);
                scene.Tick(10);
                bot.Position = new Vector3(410f,1.35f,-98.5f);
                scene.Tick(22);
                check(bot.Objective.Status == ObjectiveStatus.Moving && scene.Movement.Orders == 2, "keeps the detour while it leads away from the suitcase");
                bot.Position = stand;
                scene.Tick(26);
                check(bot.Objective.Status == ObjectiveStatus.Looting, "claims the suitcase on its side, outside original waypoint radius");
            }
            finally { Physics.SegmentHit = null; NavMesh.Route = null; NavMesh.Sampler = null; }
        }
    }
}
