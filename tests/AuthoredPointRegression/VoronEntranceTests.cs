using EFT.Interactive;
using Orbit.Entities;
using Orbit.Navigation;
using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Systems;

internal sealed partial class RushPlan
{
    internal static void TestVoronEntrance()
    {
        // 11_KK's feet and rejected handle are from the raid. Exterior/hall pairs and
        // their ground connectivity were checked against Shoreline_Middle and Waypoints' mesh.
        var facade = new Vector3(-234.54f, -41.02f, 198.63f);
        var rejected = new Vector3(-238.97f, -41.02f, 197.81f);
        var handle = new Vector3(-241f, -40.66f, 197.625f);
        var outside = new Vector3[] { new(-223.50f,-40.90f,182.50f), new(-218.75f,-40.88f,194.125f), new(-240.375f,-40.94f,184.75f) };
        var inside = new Vector3[] { new(-223.875f,-40.65f,184.875f), new(-221f,-40.64f,193.75f), new(-240.875f,-40.66f,187.125f) };
        string[] ids = ["door_Shoreline_Middle_00037", "door_Shoreline_Middle_00026", "door_Shoreline_Middle_00027"];
        static bool Near(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < .3f;
        static void Expect(bool ok, string msg) { if (!ok) throw new Exception("Voron: " + msg); }
        var direct = new OperationRouteSearch();
        NavMesh.Sample = (p, r) => Vector3.Distance(p, rejected) < 1.3f ? rejected : null;
        NavMesh.Path = (a, b, p) => { p.status = NavMeshPathStatus.PathPartial; p.corners = [a, facade]; return true; };
        bool found;
        do { found = direct.Find(facade, rejected, out _, out _, localAccess: true, finalPoint: p => Near(p,rejected)); }
        while (direct.Pending);
        Expect(!found && direct.PartialPaths > 0, "reproduce failed approach from the facade before rescue");

        foreach (var scenario in new[] { "open on request", "already open", "mesh gap", "blocked crossing",
            "missing exterior route", "missing interior route", "all locked", "already in hall", "no marked reach" })
        {
            var plan = new RushPlan();
            var squad = new Squad(); squad.Members.Add(plan._actor);
            var w = new WaypointSystem();
            var marked = new Door { Id = "door_Shoreline_Middle_00030", DoorState = EDoorState.Locked,
                InteractionCheck = p => scenario != "no marked reach" && Near(p,handle) };
            var opened = new List<int>();
            var crossed = new List<int>();
            var visits = new List<Vector3>();
            for (int i = 0; i < ids.Length; i++)
            {
                int entry = i;
                w.Doors[ids[i]] = new Door { Id = ids[i], InteractionCheck = p => Near(outside[entry], p),
                    DoorState = scenario == "all locked" ? EDoorState.Locked : scenario == "already open" ? EDoorState.Open : EDoorState.Shut };
            }
            int Exit(Vector3 p) => Array.FindIndex(outside, q => Near(q,p));
            int Hall(Vector3 p) => Array.FindIndex(inside, q => Near(q,p));
            bool Path(Vector3 a, Vector3 b)
            {
                if (Near(a,b)) return true;
                if (Exit(b) is var ext && ext >= 0)
                    return (Near(a,facade) || Exit(a) >= 0) && !(scenario == "missing exterior route" && ext == 0);
                if (Near(b,handle)) return Hall(a) >= 0 && !(scenario == "missing interior route" && Hall(a) == 0);
                int side = Hall(b);
                return side >= 0 && side == Exit(a) && w.Doors[ids[side]].DoorState == EDoorState.Open
                    && scenario is not ("mesh gap" or "blocked crossing");
            }
            NavMesh.Sample = (p,r) => p;
            NavMesh.Path = (a,b,p) =>
            {
                bool ok = Path(a,b);
                p.status = ok ? NavMeshPathStatus.PathComplete : NavMeshPathStatus.PathPartial;
                p.corners = ok ? [a,b] : [a,facade]; return true;
            };
            w.OpenRushDoor = (a,d) =>
            {
                int entry = Array.IndexOf(ids,d.Id);
                Expect(entry >= 0 && Near(outside[entry],a.Position), "entrance opens only at its outside handle");
                opened.Add(entry); d.DoorState = EDoorState.Open; return true;
            };
            w.CrossVoronEntrance = (a,d,b) =>
            {
                int entry = Array.IndexOf(ids,d.Id);
                Expect(d.DoorState == EDoorState.Open && Near(outside[entry],a.Position) && Near(inside[entry],b),
                    "short crossing is only requested at the matching open doorway");
                if (scenario == "blocked crossing") return false;
                crossed.Add(entry); return true;
            };
            plan._actor.Position = scenario == "already in hall" ? inside[2] : facade;
            w.RecordedMap = "Reserve";
            Expect(!plan.TryStartVoronEntrance(squad,w,marked), "other maps excluded");
            w.RecordedMap = "Shoreline";
            plan._inside = true;
            Expect(!plan.TryStartVoronEntrance(squad,w,marked), "a bot already looting never goes back for an entrance");
            plan._inside = false;
            Expect(!plan.TryStartVoronEntrance(squad,w,w.Doors[ids[0]]), "other Shoreline objectives excluded");
            Expect(plan.TryStartVoronEntrance(squad,w,marked), "start rescue on failed Voron approach");
            for (int i = 0; i < 160 && plan._result == null; i++)
            {
                Time.time += .5f;
                plan.TickVoronEntrance(squad,w,new(),marked);
                if (!plan._orders.TryGetValue(plan._actor,out var next) || Near(plan._actor.Position,next)) continue;
                Expect(Path(plan._actor.Position,next) || crossed.Contains(Hall(next)), "movement order has a route");
                plan._actor.Position = next; plan._actor.Objective.Status = ObjectiveStatus.Finished; visits.Add(next);
            }
            if (scenario is "all locked" or "blocked crossing" or "no marked reach")
                Expect(plan._result != null && plan._result != "reached marked handle" && !visits.Contains(handle),
                    "bounded failure without entering or unlocking");
            else
            {
                Expect(plan._result == "reached marked handle" && !plan._partial, scenario + " reaches the hall-side handle");
                if (scenario == "already in hall") Expect(visits.SequenceEqual(new[]{handle}), "no return outside");
                else
                {
                    int entry = scenario.StartsWith("missing") ? 1 : 0;
                    Expect(visits.SequenceEqual(new[]{outside[entry],inside[entry],handle}), "outside, threshold, hall handle in order");
                    Expect(opened.Count == (scenario == "already open" ? 0 : 1), "one native open request if needed");
                    Expect(crossed.Count == (scenario == "mesh gap" ? 1 : 0), "prefer ordinary navigation");
                }
            }
        }
        Console.WriteLine("PASS 11_KK at VoronHideout: blocked facade, hall-side access and nine entrance scenarios");
    }
}
