using EFT.Interactive;
using Orbit.Entities;
using Orbit.Navigation;
using UnityEngine;

int checks = 0;
void Check(bool result, string message) { if (!result) throw new Exception(message); checks++; }

var bot = new Agent();
BunkerScenarios.Run(Check);
var exit = new ExfiltrationPoint { Settings = new() { Name = "ZB-014" } };
var point = new Waypoint { Target = exit, ExfilGate = new Door() };
Check(!ExfilArrival.IsInside(bot, point), "no trigger cannot fall back to anchor");
exit = new ExfiltrationPoint { Settings = exit.Settings, Volume = new() { bounds = new() { size = new(4, 4, 4) } } };
point.Target = exit;
foreach (var state in Enum.GetValues<EDoorState>())
{
    point.ExfilGate.DoorState = state;
    Check(ExfilArrival.IsInside(bot, point) == (state == EDoorState.Open), $"actual entry with {state} gate");
}
exit.Volume.Closest = p => p + Vector3.up;
Check(!ExfilArrival.IsInside(bot, point), "wrong side inside the rotated bounding box");
exit.Volume.Closest = p => p;
exit.Status = EExfiltrationStatus.NotPresent;
Check(!ExfilArrival.IsInside(bot, point), "unavailable bunker");
exit.Status = EExfiltrationStatus.RegularMode;
point.ExfilGate = null;
Check(!ExfilArrival.IsInside(bot, point), "unresolved gate");
exit.Settings.Name = "ZB-016";
Check(ExfilArrival.IsInside(bot, point), "second Woods bunker needs no keyed gate");

var crateRoot = new Transform();
var crate = new LootableContainer { transform = new() { parent = crateRoot, position = new(2, 0, 0) },
    Collider = new Collider { bounds = new() { center = new(2, 1, 0), size = new(1, 1, 1) } } };
AirdropVisibility.Register(crate, crateRoot);
Physics.FirstHit = new Collider { transform = new() { parent = crateRoot } };
Check(AirdropVisibility.Check(Vector3.up, crate, out _), "airdrop discoverable through its own shell");
Physics.FirstHit = new Collider();
Check(!AirdropVisibility.Check(Vector3.up, crate, out _), "distant airdrop discovery still requires sight");

var head = Vector3.up;
var loot = new InteractableObject { transform = new() { position = new(2, 0, 0) } };
var wall = new Collider { name = "Wall_tile_5x10_BALLISTIC_concrete" };
var drawer = new Collider { name = "card_file_Box_BALLISTIC_metalthin" };
var surfaces = new List<(Collider collider, float x)>();
Physics.Intersections = (origin, direction, length) => surfaces
    .Select(surface => (surface.collider, t: (surface.x - origin.x) / direction.x))
    .Where(surface => surface.t > 0 && surface.t < length)
    .Select(surface => new RaycastHit { collider = surface.collider, distance = surface.t,
        point = origin + direction * surface.t, normal = new(-Math.Sign(direction.x), 0, 0) }).ToArray();
foreach (var target in new InteractableObject[] { loot, crate })
{
    surfaces.Clear();
    surfaces.Add((wall, 3f));
    surfaces.Add((drawer, .8f));
    Check(LootVisibility.Check(head, target, out _), "same side of wall allows pickup");
    surfaces.Add((wall, 1.5f));
    Check(!LootVisibility.Check(head, target, out var blocked) && blocked.collider == wall,
        "wall between bot and target blocks regardless of other intersections");
    Check(LootVisibility.Check(new(1.6f, 1, 0), target, out _), "moving to the loot side allows pickup");
}
surfaces.Clear();
foreach (var name in new[] { "card_file_BALLISTIC_metalthin", "scontainer_crete_BALLISTIC_woodthick",
    "bed_frame", "WallClock", "wall_lamp", "drywallpaper", "wallet" })
    surfaces.Add((new Collider { name = name }, 1f));
Check(LootVisibility.Check(head, loot, out _), "non-partitions allow pickup");
var generic = new MeshCollider { name = "ballistic", sharedMesh = new Mesh { name = "Custom_Container_3_Wall_BALLISTIC_metalthin" } };
surfaces.Add((generic, 1.5f));
Check(!LootVisibility.Check(head, loot, out var hit) && hit.collider == generic, "shared wall mesh identifies a generic collider");
surfaces.Clear();
var parent = new Transform { name = "ConcreteWall" };
surfaces.Add((new Collider { name = "ballistic", transform = new() { parent = parent } }, .7f));
Check(!LootVisibility.Check(head, loot, out _), "structural prefab identifies its generic collision child");
surfaces.Clear();
surfaces.Add((new Collider { name = drawer.name, transform = new() { parent = parent } }, 1f));
Check(LootVisibility.Check(head, loot, out _), "unrelated parent does not identify a prop as a partition");

surfaces.Clear();
surfaces.Add((new Collider { name = "Wall_panel", transform = new Transform { parent = loot.transform } }, 1.9f));
Check(LootVisibility.Check(head, loot, out _), "a wall-mounted target cannot be its own separating wall");

var room = new Collider { name = "Hostel_01_Room_02_BALLISTIC_concrete" };
surfaces.Clear();
surfaces.Add((room, 1f));
Check(LootVisibility.Check(head, loot, out _), "a room mesh alone is insufficient");
room.SurfaceProbe = (ray, length) => ray.origin.y > 1.5f
    ? new RaycastHit { distance = .1f, normal = -ray.direction } : null;
Check(!LootVisibility.Check(head, loot, out _), "continuous vertical face in a combined room mesh blocks");

surfaces.Clear();
for (int i = 0; i < 32; i++) surfaces.Add((drawer, .4f));
surfaces.Add((wall, 1.5f));
Check(!LootVisibility.Check(head, loot, out _) && Physics.Overflows > 0, "saturated query still finds the wall");
var guard = new LootAccessGuard();
foreach (bool dormant in new[] { false, true })
{
    guard.Reset();
    Check(!guard.Check(1, dormant, head, loot, out _), "wall prevents inventory transfer awake and asleep");
    surfaces.Clear();
    Check(guard.Check(2, dormant, head, loot, out _), "same-side transfer permitted");
    int queries = Physics.Queries;
    Check(guard.Check(2, dormant, head, loot, out _) && Physics.Queries == queries, "same session and frame reuse access");
    surfaces.Add((wall, 1.5f));
    Check(!guard.Check(3, dormant, head, loot, out _), "a wall appearing mid-session prevents the next transfer");
}
Physics.Intersections = (origin, direction, length) => direction.x > 0 ? Array.Empty<RaycastHit>()
    : new[] { new RaycastHit { collider = wall, distance = .3f } };
Check(!LootVisibility.Check(head, crate, out var reverse) && Math.Abs(reverse.distance - 1.7f) < .001f,
    "one-sided wall blocks from its back; distance is relative to the bot");
Check(!guard.Check(4, false, head, null, out _), "missing target rejected");
guard.CompleteLoosePickup(true);
Check(guard.Check(5, false, head, null, out _), "completed loose pickup may finalize");
guard.Reset();
Check(!guard.Check(5, false, head, null, out _), "completion does not leak across sessions");
Console.WriteLine($"Woods bunker, airdrop discovery and loot wall access: {checks} checks passed.");
