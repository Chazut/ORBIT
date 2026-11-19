using EFT.Interactive;
using Orbit.Systems;
using UnityEngine.AI;

int checks = 0;
void Check(bool ok, string scenario)
{
    if (!ok) throw new InvalidOperationException(scenario);
    checks++;
}

var registry = new WaypointSystem();
var crate = new LootableContainer();
Check(registry.Discover(crate) == null, "an unregistered crate cannot be claimed");
registry.RegisterLandedAirdrop(crate);
NavMesh.Reachable = false;
Check(registry.Discover(crate) == null, "no point before a successful NavMesh sample");
NavMesh.Reachable = true;
var point = registry.Discover(crate);
Check(point.IsAirdrop && !registry.InGrid(point.Id), "discovery keeps the crate out of ordinary loot");
registry.RegisterLandedAirdrop(crate);
Check(ReferenceEquals(point, registry.Discover(crate)), "repeated landing and discovery share one point");

const int viktor = 1, mark = 2, aristarh = 3;
Check(registry.TryClaim(point.Id, viktor), "Woods: first scav can loot the landed crate");
Check(registry.TryClaim(point.Id, viktor), "the owner's arrival and loot start are idempotent");
Check(Orbit.Log.Events.Count(s => s.StartsWith("AIRDROP LOOT: reserved")) == 1, "one grant event");
Check(registry.IsClaimedByOther(point.Id, mark) && !registry.IsClaimedByOther(point.Id, viktor),
    "discovery and ambush see the same owner");
Check(!registry.TryClaim(point.Id, mark) && !registry.TryClaim(point.Id, aristarh),
    "another squad or scav cannot start a second session");
registry.ReleaseClaim(point.Id, mark);
Check(registry.IsClaimedByOther(point.Id, mark), "only the owner may release the crate");
Check(registry.BeginAirdropLoot(point.Id, viktor), "owner starts inventory transfer");
registry.ReleaseClaim(point.Id, viktor);
Check(!registry.BeginAirdropLoot(point.Id, mark), "ending the route cannot interrupt an active transfer");
registry.EndLootSession(point, aristarh);
Check(registry.IsClaimedByOther(point.Id, mark), "unrelated cancellation cannot end the transfer");
registry.EndLootSession(point, viktor);
Check(registry.TryClaim(point.Id, mark), "another scav can try after release");
registry.ReleaseClaim(point.Id, viktor);
Check(registry.IsClaimedByOther(point.Id, viktor), "a stale release preserves the new owner");

var second = new LootableContainer();
registry.RegisterLandedAirdrop(second);
var other = registry.Discover(second);
Check(other.Id != point.Id && registry.TryClaim(other.Id, aristarh), "independent live crates");
Check(registry.BeginAirdropLoot(point.Id, mark), "the new owner takes over the session");
registry.EndLootSession(point, viktor);
Check(registry.IsClaimedByOther(point.Id, viktor), "late completion preserves the new session");
registry.ReleaseAirdropClaims(mark);
Check(!registry.IsClaimed(point.Id) && registry.IsClaimed(other.Id), "despawn releases only that bot's claims");
Check(registry.TryClaim(point.Id, viktor), "handoff after despawn");
Check(registry.Retire(point.Id) && !registry.IsClaimed(point.Id), "explicit removal releases the owner");
registry.RegisterLandedAirdrop(crate);
Check(!registry.Retire(point.Id) && registry.Discover(crate) == null && !registry.TryClaim(point.Id, mark),
    "late landing and stale references cannot revive a purged crate");

second.Destroyed = true;
Check(!registry.TryClaim(other.Id, aristarh) && registry.Discover(second) == null,
    "a destroyed Unity container cannot be reclaimed by its former owner");
registry.Prune();
Check(!registry.IsClaimed(other.Id) && !registry.TryClaim(other.Id, mark), "prune clears destroyed reservations");
registry.AddGridPoint(42);
Check(registry.TryClaim(42, viktor) && !registry.TryClaim(42, mark), "normal grid points still reserve exclusively");
Check(!registry.TryClaim(9999999, viktor), "an unknown off-grid id is denied");
Console.WriteLine($"PASS: {checks} airdrop and grid reservation checks");
