using Orbit.Navigation;
using Orbit.Zones;
using UnityEngine;
using UnityEngine.AI;

internal static class LibraryCampTests
{
    private static readonly Vector3 Camp = new(78.84624f, 1.432f, -156.15479f);
    private static readonly Vector3 Spawn = new(647.48f, 1.8f, 48.37f);
    private const float UpperFloor = 5.4f;

    internal static void Run()
    {
        foreach (var reachableNearCamp in new[] { true, false })
        {
            // Model the partial upper-floor paths observed in raid 501. The lower
            // floor is connected throughout and the authored height points to it.
            NavMesh.Sample = (p, radius) =>
            {
                float height = Vector3.Distance(p, Camp) < 18f && Math.Abs(p.y - UpperFloor) < Math.Abs(p.y - Camp.y)
                    ? UpperFloor : Camp.y;
                return Math.Abs(p.y - height) <= radius ? new(p.x, height, p.z) : null;
            };
            NavMesh.Path = (from, to, path) =>
            {
                bool connected = to.y < UpperFloor || reachableNearCamp && !OperationRouteSearch.CanStageFrom(from, Camp);
                path.status = connected ? NavMeshPathStatus.PathComplete : NavMeshPathStatus.PathPartial;
                path.corners = [from, connected ? to : new(to.x, Camp.y, to.z)];
                return true;
            };
            var actor = Spawn;
            var distantRepair = Repair(actor);
            Require(distantRepair.HasDisconnectedSurface, "compatible floor but no complete path from spawn");
            var visited = new List<Vector3> { actor };
            while (OperationRouteSearch.CanStageFrom(actor, Camp))
            {
                var search = new OperationRouteSearch();
                bool found, final;
                Vector3 order;
                int slices = 0;
                do
                {
                    Require(++slices < 100, "bounded route search");
                    found = search.Find(actor, Camp, out order, out final, avoided: visited, floorAware: true,
                        requireLocalDestination: true, destinationScope: OnUpperFloor,
                        destinationRadius: 10f, stageDisconnectedTarget: true);
                } while (search.Pending);
                Require(found && !final && !search.InvalidDestination, "reachable intermediate order");
                Require(Vector3.Distance(order, Camp) < Vector3.Distance(actor, Camp) - 3f, "leg moves closer");
                Require(search.RouteCorners.Last() == order && !visited.Contains(order), "verified and unvisited endpoint");
                actor = order;
                visited.Add(actor);
                Require(visited.Count < 25, "approach terminates");
            }
            Require(visited.Count > 2 && !OnUpperFloor(actor), "multiple legs do not count as floor arrival");
            var localRepair = Repair(actor);
            if (reachableNearCamp)
                Require(localRepair.Outcome == "corrected" && OnUpperFloor(localRepair.Result), "upstairs route verified locally");
            else
                Require(localRepair.Outcome == "no-reachable-surface", "unreachable floor still rejected locally");
        }
        Console.WriteLine("PASS LibraryCamp staged approach and local floor validation");
    }

    private static bool OnUpperFloor(Vector3 p) => FloorCatalog.Matches("bigmap", "2nd-floor", p.x, p.y, p.z);

    private static AuthoredPointSearch Repair(Vector3 actor)
    {
        var repair = new AuthoredPointSearch(Camp, 10f, OnUpperFloor);
        for (int slice = 0; slice < 160; slice++)
            if (repair.Step(actor)) return repair;
        throw new InvalidOperationException("LibraryCamp repair did not finish");
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException("LibraryCamp: " + message);
    }
}
