using EFT.Interactive;
using Orbit.Entities;
using Orbit.Navigation;
using UnityEngine;

int checks = 0;
void Check(bool result, string message) { if (!result) throw new Exception(message); checks++; }

var bot = new Agent();
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

var root = new Transform();
var crate = new LootableContainer { Collider = new(), transform = new() { parent = root, position = new(321, 9, -266) } };
var head = crate.transform.position + new Vector3(1, 1, 0);
AirdropVisibility.Register(crate, root);
var guard = new LootAccessGuard();
Physics.FirstHit = new Collider { transform = new() { parent = root } };
Check(AirdropVisibility.Check(head, crate, out _) && guard.Check(1, false, head, crate, out _), "DocsM4 can start and continue through the crate's own shell");
Check((Physics.End - AirdropVisibility.Target(crate)).sqrMagnitude < .0001f, "same ray endpoint on arrival and pickup");
Physics.FirstHit = new Collider { transform = new() { parent = new Transform() } };
Check(!AirdropVisibility.Check(head, crate, out _) && !guard.Check(2, false, head, crate, out _), "another crate occludes the target");
Physics.FirstHit = new Collider();
Check(!guard.Check(3, false, head, crate, out _), "wall still blocks transfer");
Console.WriteLine($"Woods bunker and airdrop visibility: {checks} checks passed.");
