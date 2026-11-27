using System;
using System.Linq;
using Comfort.Common;
using EFT.Interactive;
using Orbit;
using Orbit.Core;
using Orbit.Entities;
using Orbit.Navigation;
using Orbit.Systems;
using Orbit.Tasks;
using Orbit.Tasks.Actions;
using UnityEngine;

int checks = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }

NativeDepartureTests.Run(Check);
CampWatchTests.Run(Check);
VoronLootTests.Run(Check);
FactoryDrawerTests.Run(Check);

var solo = new Scenario();
var departing = solo.Add(true);
solo.Add(false, status: ObjectiveStatus.Looting, active: false);
solo.Tick(0); solo.Tick(59.9f);
Check(departing.Bot.LeaveData.Removals == 0, "solo waits full 60s");
solo.Tick(60);
Check(departing.Bot.LeaveData.Removals == 1, "solo skips 90s even with teammates elsewhere, including start time zero");
Check(solo.Car.Status == EExfiltrationStatus.NotPresent, "solo departure closes car");
Check(Log.Messages.Count(x => x.Contains("no squad wait")) == 1, "solo countdown logged once");

var group = new Scenario();
var leader = group.Add(false);
var straggler = group.Add(false, new(73,0,0), ObjectiveStatus.Extracting, false);
group.Tick(0); group.Tick(89.9f);
Check(!Log.Messages.Any(x => x.Contains("countdown started")), "group waits for missing teammate");
group.Tick(90); group.Tick(149.9f);
Check(leader.Bot.LeaveData.Removals == 0, "group wait is followed by full 60s");
group.Tick(150);
Check(leader.Bot.LeaveData.Removals == 1 && straggler.Bot.LeaveData.Removals == 0, "only physically present active group member extracts");
straggler.IsActive = true; group.Tick(151);
Check(straggler.Objective.Location == null && straggler.Bot.LeaveData.Removals == 0, "combat straggler replans on return after car departure");
Check(group.Squad.ExtractRequested && group.Squad.CompletedPoiIds.Contains(group.Exit.Id), "group extraction intent survives missed departure");

var together = new Scenario();
var t1 = together.Add(false); var t2 = together.Add(false);
together.Tick(0); together.Tick(60);
Check(t1.Bot.LeaveData.Removals == 1 && t2.Bot.LeaveData.Removals == 1, "ready group skips wait and extracts every member before trigger disabled");

var late = new Scenario();
var l1 = late.Add(false); var l2 = late.Add(false, new(20,0,0), active:false);
late.Tick(0); l2.Position = default; l2.IsActive = true; late.Tick(25); late.Tick(84.9f);
Check(l1.Bot.LeaveData.Removals == 0, "last teammate arrival starts group countdown early");
late.Tick(85);
Check(l1.Bot.LeaveData.Removals == 1 && l2.Bot.LeaveData.Removals == 1, "group completes 60s after last arrival");

var popple = new Scenario();
var p = popple.Add(true);
popple.Tick(0); p.IsActive = false; popple.Tick(20);
p.Position = new(73,0,0); popple.Tick(75);
Check(p.Bot.LeaveData.Removals == 0, "combat retains control beyond car timer");
p.IsActive = true; popple.Tick(76);
Check(p.Bot.LeaveData.Removals == 0, "PoppleO regression: never despawn 73m away on combat return");
Check(p.Objective.Location == null && p.SoloExtractTarget == null && p.SoloExtractRequested, "missed solo clears both pins and retains intent");
Check(popple.Squad.NearestExfilCached == null && popple.Squad.NearestExfilCachedAt < 0, "departed car invalidates cached exfil selection");

var returns = new Scenario();
var r = returns.Add(true);
returns.Tick(0); r.IsActive = false; returns.Tick(20); r.Position = new(20,0,0);
r.IsActive = true; returns.Tick(30);
Check(r.Objective.Status == ObjectiveStatus.None && r.SoloExtractTarget == returns.Exit, "outside bot re-arms return to available car");
returns.Tick(31);
Check(r.Objective.Status == ObjectiveStatus.Moving && returns.Movement.Destination.Equals(returns.Exit.Position), "real Goto submits the return route");
r.Position = default; returns.Tick(39); returns.Tick(40); returns.Tick(59.9f);
Check(r.Bot.LeaveData.Removals == 0, "returning bot waits original countdown");
returns.Tick(60);
Check(r.Bot.LeaveData.Removals == 1, "brief interruption does not restart countdown");

var nearCombat = new Scenario();
var nc = nearCombat.Add(true); nearCombat.Tick(0); nc.IsActive=false; nearCombat.Tick(60);
Check(nc.Bot.LeaveData.Removals == 0, "combat bot inside trigger also keeps combat priority");
nc.IsActive=true; nearCombat.Tick(61);
Check(nc.Bot.LeaveData.Removals == 1, "resume inside can finish expired countdown");

var fightingGroup = new Scenario();
var fg1 = fightingGroup.Add(false); var fg2 = fightingGroup.Add(false);
fightingGroup.Tick(0); fg2.IsActive = false; fightingGroup.Tick(60);
Check(fg1.Bot.LeaveData.Removals == 1 && fg2.Bot.LeaveData.Removals == 0, "group batch cannot remove inactive combat member even inside");
fg2.IsActive=true; fightingGroup.Tick(61);
Check(fg2.Bot.LeaveData.Removals == 0 && fg2.Objective.Location == null, "combat survivor seeks another exit after group departure");

var movingGroup = new Scenario();
var mg1 = movingGroup.Add(false); var mg2 = movingGroup.Add(false);
movingGroup.Tick(0); mg2.Position = new(10,0,0); mg2.Objective.Status=ObjectiveStatus.Moving;
movingGroup.Tick(60);
Check(mg1.Bot.LeaveData.Removals == 1 && mg2.Bot.LeaveData.Removals == 0 && mg2.Objective.Location == null, "active group member outside trigger is redirected, not despawned");

foreach(var status in new[]{EExfiltrationStatus.NotPresent,EExfiltrationStatus.Hidden,EExfiltrationStatus.AwaitsManualActivation})
{
    var gone = new Scenario(); var g = gone.Add(true); gone.Tick(0);
    gone.Car.SetStatusLogged(status,"external departure"); gone.Tick(61);
    Check(g.Bot.LeaveData.Removals == 0 && g.SoloExtractTarget == null, $"unavailable car {status} cannot despawn bot");
}

foreach(var pos in new[]{new Vector3(10,0,0),new Vector3(0,10,0),new Vector3(0,0,10)})
{
    var outside = new Scenario(); var o=outside.Add(true,pos); outside.Tick(0);
    Check(o.Objective.Status==ObjectiveStatus.None && !Log.Messages.Any(x=>x.Contains("no squad wait")), "3D trigger required before countdown");
}

var unreachable = new Scenario(); var u=unreachable.Add(true,new(10,0,0),ObjectiveStatus.None);
for(int i=0;i<200 && u.Objective.Location!=null;i++) unreachable.Tick(i*0.1f);
Check(u.Objective.Location == null && u.SoloExtractTarget == null && u.Bot.LeaveData.Removals == 0, "outside-trigger timeout abandons car without force-extract loop");
Check(unreachable.Squad.CompletedPoiIds.Contains(unreachable.Exit.Id), "unreachable car blacklisted");

var failedPath = new Scenario(); var fp=failedPath.Add(true,new(20,0,0),ObjectiveStatus.None);
var arrivalFailure = typeof(GotoObjectiveAction).GetMethod("TrackArrivalFailure",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance);
for(int i=0;i<3;i++) arrivalFailure.Invoke(failedPath.Goto,new object[]{fp,failedPath.Exit});
Check(fp.Objective.Location == null && fp.SoloExtractTarget == null && fp.Bot.LeaveData.Removals == 0, "3-strike fallback cannot force car extraction outside trigger");

var noCollider = new Scenario(false); var n = noCollider.Add(true,new(73,0,0)); noCollider.Tick(0);
Check(n.Objective.Status == ObjectiveStatus.None, "missing collider still rejects far extraction");
Check(!ExfilArrival.IsInside(n,noCollider.Exit), "fallback distance bounded");
n.Position=new(3,0,0);n.Objective.Status=ObjectiveStatus.Extracting;noCollider.Tick(10);noCollider.Tick(70);
Check(n.Bot.LeaveData.Removals == 1, "missing collider supports bounded arrival fallback");

var two = new Scenario(); var a=two.Add(true); two.Tick(0);
var car2 = Scenario.NewCar(); var exit2=Scenario.NewExit(car2,200);
var b=two.Add(true,location:exit2); two.Tick(20); two.Tick(60);
Check(a.Bot.LeaveData.Removals==1 && b.Bot.LeaveData.Removals==0, "solo timers in same squad do not share different cars");
two.Tick(80); Check(b.Bot.LeaveData.Removals==1, "second solo gets full personal countdown");

var mixed = new Scenario(); var m1=mixed.Add(false); var m2=mixed.Add(true);
mixed.Tick(0); mixed.Tick(60);
Check(m1.Bot.LeaveData.Removals==0 && m2.Bot.LeaveData.Removals==1, "solo does not trigger group readiness or inherit group wait");
mixed.Tick(61); Check(m1.Bot.LeaveData.Removals==1, "present gathering party also leaves when solo's car departs");

var recycled = new Scenario(); var old=recycled.Add(true);recycled.Tick(0);
recycled.Remove(old); var replacement=recycled.Add(true);replacement.Id=old.Id;
recycled.Tick(50);recycled.Tick(60);
Check(replacement.Bot.LeaveData.Removals==0, "recycled agent ID cannot inherit an earlier timer");
recycled.Tick(110); Check(replacement.Bot.LeaveData.Removals==1, "replacement countdown starts from its arrival");

var recycledSquad = new Scenario(); var oldMember=recycledSquad.Add(false); recycledSquad.Tick(0);
recycledSquad.Remove(oldMember); recycledSquad.Squad=new Squad();var newMember=recycledSquad.Add(false);
recycledSquad.Tick(50);recycledSquad.Tick(60);
Check(newMember.Bot.LeaveData.Removals==0, "recycled squad ID cannot inherit an earlier timer");
recycledSquad.Tick(110); Check(newMember.Bot.LeaveData.Removals==1, "replacement group gets full countdown");

var foot = new Scenario(); foot.Car.Settings.ExfiltrationType=EExfiltrationType.Individual;
var f=foot.Add(false);foot.Tick(0);foot.Tick(4.9f);Check(f.Bot.LeaveData.Removals==0,"foot minimum unchanged");
foot.Tick(5);Check(f.Bot.LeaveData.Removals==1,"foot extraction still 5s");

var footInterrupted = new Scenario();footInterrupted.Car.Settings.ExfiltrationType=EExfiltrationType.Individual;
var fi=footInterrupted.Add(false);footInterrupted.Tick(0);fi.IsActive=false;footInterrupted.Tick(4);fi.IsActive=true;footInterrupted.Tick(10);footInterrupted.Tick(14.9f);
Check(fi.Bot.LeaveData.Removals==0,"foot interruption restarts personal hold");footInterrupted.Tick(15);Check(fi.Bot.LeaveData.Removals==1,"foot hold after combat unchanged");

// BSG disables the trigger before ExtractAction's final update (jklei18).
foreach(var soloParty in new[]{true,false})
{
    var native=new Scenario();var n1=native.Add(soloParty);var n2=soloParty?null:native.Add(false);
    Time.deltaTime=0.02f;native.Tick(0.02f);Time.time=60f;Time.deltaTime=0.005f;
    native.Car.SetStatusLogged(EExfiltrationStatus.NotPresent,"BSG countdown finished");native.Tick(60f);
    Check(n1.Bot.LeaveData.Removals==1,"native departure completes inside participant before ORBIT final tick");
    if(n2!=null)Check(n2.Bot.LeaveData.Removals==1,"native departure completes every present group member");
}
{
    var native=new Scenario();var n1=native.Add(true);native.Tick(0);
    n1.Position=new(20,0,0);Time.time=60;
    native.Car.SetStatusLogged(EExfiltrationStatus.NotPresent,"departure");n1.Position=default;native.Tick(60.1f);
    Check(n1.Bot.LeaveData.Removals==0 && n1.Objective.Location==null,"returning after actual departure cannot extract");
}
{
    var native=new Scenario();var n1=native.Add(true);native.Tick(0);Time.time=60;
    native.Car.SetStatusLogged(EExfiltrationStatus.NotPresent,"departure");n1.Position=new(20,0,0);native.Tick(60.1f);
    Check(n1.Bot.LeaveData.Removals==0,"must still be present when native departure is completed");
}
{
    var native=new Scenario();var n1=native.Add(false);native.Add(false,new(50,0,0),active:false);native.Tick(0);Time.time=60;
    native.Car.SetStatusLogged(EExfiltrationStatus.NotPresent,"departure during gathering");native.Tick(60);
    Check(n1.Bot.LeaveData.Removals==1,"native departure extracts a present participant even during group gathering");
}
{
    var native=new Scenario();var n1=native.Add(true);native.Tick(0);n1.IsActive=false;Time.time=60;
    native.Car.SetStatusLogged(EExfiltrationStatus.NotPresent,"departure during combat");n1.IsActive=true;native.Tick(60.1f);
    Check(n1.Bot.LeaveData.Removals==0,"combat at actual departure preserves combat priority");
}
{
    var native=new Scenario();var n1=native.Add(true);native.Tick(0);Time.time=60;
    native.Car.SetStatusLogged(EExfiltrationStatus.NotPresent,"departure");native.Tick(80);
    Check(n1.Bot.LeaveData.Removals==0,"expired departure cannot be claimed much later");
}
{
    var native=new Scenario();var n1=native.Add(true);native.Tick(0);Time.time=10;
    native.Car.SetStatusLogged(EExfiltrationStatus.NotPresent,"native departure before ORBIT timer");native.Tick(10);
    Check(n1.Bot.LeaveData.Removals==1,"actual car departure takes precedence over ORBIT countdown");
}
{
    var native=new Scenario(false);var n1=native.Add(true,new(3,0,0));native.Tick(0);Time.time=60;
    native.Car.SetStatusLogged(EExfiltrationStatus.NotPresent,"departure");native.Tick(60);
    Check(n1.Bot.LeaveData.Removals==1,"bounded no-collider fallback also supports native departure");
}

var cache = new Scenario();var c=cache.Add(true);cache.Tick(0);
long before=GC.GetAllocatedBytesForCurrentThread();
for(int i=0;i<10000;i++) cache.Extract.Update();
long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
Check(cache.Car.gameObject.ComponentLookups==1,"trigger collider lookup cached across 10000 ticks");
Check(allocated==0,$"countdown tick must allocate zero bytes, got {allocated}");

BunkerTests.Run(Check);
ExfilLookaheadTests.Run(Check);
FailedExitTests.Run(Check);
RetainedExitRecoveryTests.Run(Check);
QuestArrivalTests.Run(Check);
foreach (bool ghost in new[] { false, true })
foreach (var (category, target, drop) in new (WaypointCategory, InteractableObject, bool)[] {
    (WaypointCategory.LooseLoot, new LootItem(), false), (WaypointCategory.Corpse, new Corpse(), false),
    (WaypointCategory.ContainerLoot, new LootableContainer(), false), (WaypointCategory.ContainerLoot, new LootableContainer(), true) })
{
    var scene = new Scenario();
    scene.Squad.ExtractRequested = false;
    var poi = new Waypoint(42, category, "loot", default, 1f, new(), new(), target) { IsAirdrop = drop };
    var looter = scene.Add(false, new(.6f, 0f, 0f), ObjectiveStatus.None, location: poi);
    looter.IsDormant = ghost;
    Physics.Blocked = true;
    Physics.FirstHit = new Collider { name = "Wall_tile_5x10_BALLISTIC_concrete" };
    scene.Tick(1); scene.Tick(2);
    Check(looter.Objective.Status == ObjectiveStatus.Moving, $"a wall prevents {category} arrival, ghost={ghost}, drop={drop}");
    Physics.FirstHit = new Collider { name = "card_file_Box_BALLISTIC_metalthin" };
    scene.Tick(3);
    Check(looter.Objective.Status == ObjectiveStatus.Looting, $"same-side {category} hands off to loot, ghost={ghost}, drop={drop}");
}
CorpseApproachTests.Run(Check);
WallLootApproachTests.Run(Check);
Console.WriteLine($"Extraction: {checks} checks passed; 10000 steady ticks = {allocated} allocated bytes. Real actions/task manager with engine doubles.");

sealed class Scenario
{
    public readonly AgentData Data=new();
    public Squad Squad=new() { ExtractRequested=true };
    public readonly ExfiltrationPoint Car;
    public readonly Waypoint Exit;
    public readonly MovementSystem Movement=new();
    public readonly WaypointSystem Waypoints=new();
    public readonly GotoObjectiveAction Goto;
    public readonly ExtractAction Extract;
    public readonly ActionManager Manager;
    private int nextId=1;
    public Scenario(bool collider=true)
    {
        Time.time=0;Time.deltaTime=0.02f;Log.Messages.Clear();Physics.Blocked=false;
        UnityEngine.AI.NavMesh.Sampler = null;
        UnityEngine.AI.NavMesh.Route = null;
        DirectionalMineSafety.Blocked = false;
        UnityEngine.AI.NavMesh.Connected = true;
        Physics.LineBlock = null;
        Car=NewCar(collider);Exit=NewExit(Car,100);
        Goto=new(Data,Movement,Waypoints,0.15f);Extract=new(Data,0.1f);
        Manager=new(Data,new Task<Agent>[]{Goto,Extract});
        Singleton<OrbitManager>.Instance=new() { Removed=Remove };
    }
    public static ExfiltrationPoint NewCar(bool collider=true)
    {
        var car=new ExfiltrationPoint { name="car",Status=EExfiltrationStatus.Countdown,Settings=new() { ExfiltrationType=EExfiltrationType.SharedTimer } };
        if(collider) car.gameObject.Components[typeof(Collider)]=new Collider { bounds=new() { Spatial=true,min=new(-5,-2,-5),max=new(5,2,5) } };
        return car;
    }
    public static Waypoint NewExit(ExfiltrationPoint car,int id) => new(id,WaypointCategory.Exfil,"car",default,225,new(),new(),car);
    public Agent Add(bool solo, Vector3 position=default,ObjectiveStatus status=ObjectiveStatus.Extracting,bool active=true,Waypoint location=null)
    {
        var a=new Agent { Id=nextId++,Squad=Squad,SoloExtractRequested=solo,Position=position,IsActive=active };
        a.Movement.OrdersOwner = Movement;
        a.Objective.Location=location??Exit;a.Objective.Status=status;
        if(solo)a.SoloExtractTarget=a.Objective.Location;
        Squad.NearestExfilCached=a.Objective.Location;Squad.NearestExfilCachedAt=Time.time;
        if(!solo)Squad.Objective.Location=a.Objective.Location;
        Squad.Members.Add(a);Squad.Size=Squad.Members.Count;Data.Entities.Values.Add(a);return a;
    }
    public void Tick(float time) { Time.time=time;Manager.Update(); }
    public void Remove(Agent a) { Manager.RemoveEntity(a);Data.Entities.Values.Remove(a);a.Squad.Members.Remove(a);a.Squad.Size=a.Squad.Members.Count; }
}
