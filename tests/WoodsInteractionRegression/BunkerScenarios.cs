using EFT.Interactive;
using Orbit.Entities;
using Orbit.Navigation;
using Orbit.Systems;
using UnityEngine;
using Random = UnityEngine.Random;

internal static class BunkerScenarios
{
    private sealed class Raid
    {
        internal readonly Agent Bot = new() { Position = new(12, 8, 0) };
        internal readonly Squad Squad = new();
        internal readonly Door Gate = new() { DoorState = EDoorState.Locked, transform = new() { position = new(8, -3, 0) } };
        internal readonly Waypoint Bunker, Other;
        internal readonly WaypointSystem Waypoints = new();
        internal readonly MovementSystem Mover = new();
        internal readonly ExfilApproach Approach;
        internal Raid(float roll)
        {
            Random.Next = roll; Random.Draws = 0; Time.time = 1; Time.frameCount = 0;
            WaypointSystem.Queried.Clear();
            WaypointSystem.GatePathEnd = null;
            Squad.Leader = Bot; Squad.Members.Add(Bot); Bot.Squad = Squad;
            Bunker = new() { Id = 1, Name = "ZB-014", ExfilGate = Gate, Position = new(11, 0, 0),
                ExfilInteriorPosition = new(12, -3, 0), Target = new ExfiltrationPoint {
                    Settings = new() { Name = "ZB-014" }, Volume = new() { bounds = new() { center = new(12, -3, 0), size = new(3, 3, 3) } } } };
            Other = new() { Id = 2, Name = "Outskirts", Position = new(300, 2, 1),
                Target = new ExfiltrationPoint { Settings = new() { Name = "Outskirts" } } };
            Waypoints.Add(Bunker); Waypoints.Add(Other);
            Approach = new(Bot, Mover, Waypoints);
        }
        internal Waypoint Choose(Agent solo = null)
        {
            for (int i = 0; i < 10; i++)
            {
                Time.frameCount++; Time.time += .1f;
                var result = Waypoints.FindNearestEligibleExfil(Squad, solo);
                if (!Waypoints.ExfilSearchPending(Squad, solo)) return result;
            }
            throw new Exception("selection did not finish");
        }
        internal void Tick(int seconds = 1)
        {
            for (int i = 0; i < seconds; i++)
            { Time.frameCount++; Time.time++; Approach.Tick(false); }
        }
    }

    internal static void Run(Action<bool, string> check)
    {
        var r = new Raid(.8f);
        check(r.Choose() == r.Other && Random.Draws == 1, "declined gate immediately selects alternative");
        check(WaypointSystem.Queried.SequenceEqual([r.Other.Position]), "no route to the rejected bunker");
        Random.Next = .1f; Time.time += 5; r.Gate.DoorState = EDoorState.Open;
        check(r.Choose() == r.Other && Random.Draws == 1, "cache expiry and another squad opening cannot reroll a refusal");
        check(r.Choose(r.Bot) == r.Bunker && Random.Draws == 2, "solo decision independent of squad");
        r.Bot.SoloExtractRequested = true; r.Bot.SoloExtractTarget = r.Bot.Objective.Location = r.Bunker;
        ((ExfiltrationPoint)r.Bunker.Target).Status = EExfiltrationStatus.NotPresent; r.Tick();
        check(r.Bot.Objective.Location == null && !r.Squad.CompletedPoiIds.Contains(r.Bunker.Id), "solo loss cannot blacklist another squad member's exit");
        ((ExfiltrationPoint)r.Bunker.Target).Status = EExfiltrationStatus.RegularMode;
        check(r.Choose(r.Bot) == r.Other, "unusable solo result cannot be reused from the route cache");
        foreach (bool ghost in new[] { false, true })
        {
            r = new(.1f); r.Bot.IsDormant = ghost; r.Bot.Objective.Location = r.Choose();
            var handle = new Vector3(7, -3, 0);
            check(r.Bot.Objective.Location == r.Bunker && WaypointSystem.Queried.SequenceEqual([handle]),
                "candidate routes to exterior handle on bunker floor, even from above the inside room");
            r.Tick(17);
            check(r.Mover.Orders.SequenceEqual([handle]) && r.Gate.Unlocks == 0 && r.Bot.Objective.Location == r.Bunker,
                "outside for 15 seconds neither unlocks nor abandons the bunker");
            r.Bot.Position = handle; r.Gate.Obstructed = true; r.Tick();
            check(r.Gate.Unlocks == 0, "obstructed handle cannot be operated through a wall");
            r.Gate.Obstructed = false; r.Bot.IsActive = false; r.Tick(55);
            check(r.Gate.Unlocks == 0, "combat pause cannot consume the travel or interaction timeout");
            r.Bot.IsActive = true; r.Tick(); r.Tick();
            check(r.Gate.Unlocks == 1 && r.Gate.Opens == 1, "unlock then open at the handle");
            r.Tick();
            check(r.Mover.Orders.Last() == r.Bunker.ExfilInteriorPosition && r.Bot.Objective.Status == ObjectiveStatus.Moving,
                "open gate changes target to room, no extraction in antechamber");
            r.Bot.Position = r.Bunker.ExfilInteriorPosition.Value; r.Tick();
            check(r.Bot.Objective.Status == ObjectiveStatus.Extracting && Random.Draws == 1, "entry starts extraction without a second roll");
        }
        r = new(.1f); r.Gate.DoorState = EDoorState.Open; r.Bunker.ExfilInteriorPosition = null;
        r.Bot.Objective.Location = r.Choose(); r.Tick();
        check(r.Mover.Orders.Single() == new Vector3(12,-3,0) && r.Gate.Unlocks == 0,
            "open gate goes straight to live trigger if no interior was sampled during loading");
        r.Gate.DoorState = EDoorState.Shut; r.Tick();
        check(r.Mover.Orders.Last() == new Vector3(7,-3,0), "gate closing en route invalidates the old room leg");
        r = new(.1f); r.Bot.Objective.Location = r.Choose(); r.Bot.Position = new(7,-3,0);
        var follower = new Agent(2) { Squad = r.Squad, Position = r.Bot.Position, IsDormant = true };
        follower.Objective.Location = r.Bunker; r.Squad.Members.Add(follower);
        var followApproach = new ExfilApproach(follower, r.Mover, r.Waypoints);
        r.Gate.DelayedUnlock = true; r.Tick(); followApproach.Tick(false);
        check(r.Gate.Unlocks == 1, "awake and Ghost squadmates cannot start two simultaneous unlocks");
        r.Squad.Leader = follower; r.Bot.Bot.IsDead = true; Random.Next = .99f;
        check(r.Choose() == r.Bunker && Random.Draws == 1, "leader replacement keeps the squad's roll");
        r.Gate.DoorState = EDoorState.Shut; Time.time += 2; followApproach.Tick(false);
        check(r.Gate.Opens == 1, "surviving squadmate can finish opening the unlocked gate");
        follower.Position = r.Bunker.ExfilInteriorPosition.Value; followApproach.Tick(false);
        check(follower.Objective.Status == ObjectiveStatus.Extracting, "successor still needs to enter the volume");
        r = new(.1f); r.Bot.Bot.Profile.Info.Settings.Role = Role.Scav;
        check(r.Choose() == r.Other && Random.Draws == 0, "non-PMC cannot gain a virtual key");
        r = new(.1f); r.Bunker.ExfilGate = null;
        check(r.Choose() == r.Other && Random.Draws == 0, "missing native gate skips bunker before planning");
        r = new(.1f); WaypointSystem.GatePathEnd = new(6.5f,-3,0);
        check(r.Choose() == r.Bunker, "a partial path ending at the reachable handle can win selection");
        r = new(.1f); WaypointSystem.GatePathEnd = new(7,4,0);
        check(r.Choose() == r.Other, "partial path stopping above the handle cannot outrank a reachable exit");
        r = new(.1f); r.Bot.Objective.Location = r.Choose(); r.Bot.Position = new(7,-3,0); r.Gate.Obstructed = true;
        r.Tick(70);
        check(r.Bot.Objective.Location == null && r.Choose() == r.Other && r.Gate.Unlocks == 0,
            "exhausted approaches can abandon a genuinely inaccessible gate");
    }
}
