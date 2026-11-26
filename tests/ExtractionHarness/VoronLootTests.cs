using System;
using System.Collections.Generic;
using EFT.Interactive;
using Orbit.Entities;
using Orbit.Navigation;
using Orbit.Systems;
using UnityEngine;
using UnityEngine.AI;

internal static class VoronLootTests
{
    private static readonly Vector3 Facade = new(-234.54f, -41.02f, 198.63f);
    private static readonly Vector3 Outside = new(-223.50f, -40.90f, 182.50f);
    private static readonly Vector3 Hall = new(-223.875f, -40.65f, 184.875f);
    private static readonly Vector3 Keycard = new(-236.05f, -39.88f, 195.18f);
    private static bool Near(Vector3 a, Vector3 b) => (a - b).sqrMagnitude < .04f;

    internal static void Run(Action<bool,string> check)
    {
        foreach (string scenario in new[] { "loose", "container", "direct", "denied", "claimed", "reassigned", "blocked", "mesh gap" })
        {
            var s = new Scenario();
            s.Squad.ExtractRequested = false;
            var marked = new Door { Id = VoronEntranceRoute.MarkedDoorId, DoorState = EDoorState.Locked };
            marked.transform.position = new(-239.752f,-40.67f,197.511f);
            var entry = new Door { Id = "door_Shoreline_Middle_00037", DoorState = EDoorState.Shut };
            entry.transform.position = Outside;
            s.Waypoints.Doors[entry.Id] = entry;
            InteractableObject item = scenario == "container" ? new LootableContainer() : new LootItem();
            var category = scenario == "container" ? WaypointCategory.ContainerLoot : WaypointCategory.LooseLoot;
            var point = new Waypoint(501, category, "Voron pickup", Keycard, 1, new(), new(), item);
            foreach (var other in new[] { Facade, VoronEntranceRoute.VoronHallHandle, Keycard + Vector3.up * 5 })
            {
                item.transform.position = other;
                check(VoronLootApproach.RoomDoor(point, marked) == null, "hall, facade and upper floor excluded");
            }
            item.transform.position = Keycard;
            point.VoronLootDoor = VoronLootApproach.RoomDoor(point, marked);
            check(point.VoronLootDoor == marked, "SPT keycard position belongs to room");
            var otherLock = new Door();
            point.LockedDoorsOnPath = new() { otherLock };
            VoronLootApproach.IncludeLock(ref point.LockedDoorsOnPath, marked);
            VoronLootApproach.IncludeLock(ref point.LockedDoorsOnPath, marked);
            check(point.LockedDoorsOnPath.Count == 2 && point.LockedDoorsOnPath.Contains(otherLock), "retain other locks; require one Voron decision");
            if (scenario != "denied") s.Squad.ForceUnlockDoorIds.Add(marked.GetInstanceID());
            var actor = s.Add(false, Facade, ObjectiveStatus.None, location: point);
            s.Squad.Objective.Location = actor.Objective.SplinterParent = s.Exit;
            s.Waypoints.OpenRushDoor = (a,d) => {
                check(d == entry && Near(a.Position,Outside), "open real entrance from outside");
                return s.Movement.OpenObjectiveDoor(a,d);
            };
            int crossings = 0;
            s.Waypoints.CrossVoronEntrance = (a,d,target) => {
                check(d.DoorState == EDoorState.Open, "physical crossing only through an open door");
                if (scenario == "blocked") return false;
                crossings++;
                s.Movement.MoveToByPath(a,target);
                return true;
            };
            NavMesh.Sampler = (p,r) => p;
            NavMesh.Route = (a,b,path) => {
                bool ok = Near(a,b) || Near(a,Facade) && Near(b,Outside)
                    || Near(a,Outside) && Near(b,Hall) && entry.DoorState == EDoorState.Open && scenario is not ("mesh gap" or "blocked")
                    || Near(a,Hall) && Near(b,VoronEntranceRoute.VoronHallHandle)
                    || Near(a,VoronEntranceRoute.VoronHallHandle) && Near(b,VoronEntranceRoute.RoomStand) && marked.DoorState == EDoorState.Open && scenario != "mesh gap"
                    || VoronEntranceRoute.InLootRoom(a) && VoronEntranceRoute.InLootRoom(b);
                path.status = ok ? NavMeshPathStatus.PathComplete : NavMeshPathStatus.PathPartial;
                path.corners = ok ? new[]{a,b} : new[]{a,Facade};
                return true;
            };
            s.Tick(1);
            check(s.Movement.Orders == 1 && Near(s.Movement.Destination,point.Position), "original pickup attempted first");
            if (scenario == "direct")
            {
                marked.DoorState = EDoorState.Open;
                actor.Position = point.Position;
                s.Tick(2);
                check(actor.Objective.Status == ObjectiveStatus.Looting && entry.Opens == 0, "direct success stays direct");
                continue;
            }
            actor.Movement.Status = MovementStatus.Failed;
            s.Tick(4);
            if (scenario == "denied")
            {
                check(actor.Objective.Status != ObjectiveStatus.Looting && marked.Unlocks == 0 && entry.Opens == 0
                    && s.Squad.ForceUnlockDoorIds.Count == 0 && !VoronLootApproach.Allowed(actor, marked), "no second chance on refused roll");
                continue;
            }
            if (scenario == "claimed") s.Waypoints.ClaimedByOther = true;
            if (scenario == "reassigned") { actor.Objective.Location = s.Exit; actor.Objective.Status = ObjectiveStatus.None; }
            int previous = s.Movement.Orders;
            for (int i=0; i<100; i++)
            {
                s.Tick(4.5f + i*.5f);
                if (scenario == "reassigned" || actor.Objective.Status is ObjectiveStatus.Looting or ObjectiveStatus.Failed) break;
                if (s.Movement.Orders != previous)
                {
                    previous = s.Movement.Orders;
                    s.Movement.Step(actor,100);
                }
                if (marked.Unlocks > 0 && marked.Opens == 0) check(Near(actor.Position,VoronEntranceRoute.VoronHallHandle), "unlock from inside hall");
            }
            if (scenario is "reassigned" or "claimed" or "blocked")
                check(marked.Unlocks == 0 && actor.Objective.Status != ObjectiveStatus.Looting, "no stale pickup or remote unlock: " + scenario);
            else
            {
                check(entry.Opens == 1 && marked.Unlocks == 1 && marked.Opens == 1 && crossings == (scenario == "mesh gap" ? 2 : 0), "all doors used once: " + scenario);
                check(ReferenceEquals(actor.Objective.Location,point) && actor.Objective.Status == ObjectiveStatus.Looting
                    && VoronEntranceRoute.InLootRoom(actor.Position), "same pickup reaches real Looting action inside: " + scenario);
                check(ReferenceEquals(s.Squad.Objective.Location,s.Exit) && ReferenceEquals(actor.Objective.SplinterParent,s.Exit), "squad and splinter preserved");
            }
        }
    }
}
