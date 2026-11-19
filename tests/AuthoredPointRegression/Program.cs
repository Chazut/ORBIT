using Orbit;
using Orbit.Entities;
using Orbit.Navigation;
using Orbit.Settings;
using Orbit.Systems;
using UnityEngine;
using UnityEngine.AI;

int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}

void Floors(params float[] heights)
{
    NavMesh.Sample = (p, radius) =>
    {
        var height = heights.MinBy(y => Math.Abs(y-p.y));
        return Math.Abs(height-p.y) <= radius ? new(p.x,height,p.z) : null;
    };
}
bool Complete(Vector3 a, Vector3 b, NavMeshPath path)
{
    path.status = NavMeshPathStatus.PathComplete;
    path.corners = [a,b];
    return true;
}
NavMesh.Path = Complete;
var seed = new Vector3(0,0,0);
var agent = new Agent { Position=new(7,0,10) };

AuthoredPointSearch Resolve(Vector3 position, Func<Vector3,bool> scope=null)
{
    var search = new AuthoredPointSearch(position, 9, scope);
    for (int i=0; i<140; i++)
    {
        var samples=search.Samples;
        var paths=search.Paths;
        bool finished=search.Step(agent.Position);
        Check(search.Samples-samples<=3 && search.Paths-paths<=1,"work bounded per frame");
        if (finished) return search;
    }
    throw new InvalidOperationException("unbounded search");
}

Floors(-4,4);
Check(Resolve(seed).Outcome=="ambiguous-floors","two equally plausible walkable storeys");
Check(Resolve(seed,p=>p.y>0).Result.y==4,"explicit upstairs selection");
NavMesh.Path=(a,b,path)=>b.y>0 && Complete(a,b,path);
Check(Resolve(seed).Result.y==4,"disconnected floor cannot win");
NavMesh.Path=(a,b,path)=>{ Complete(a,b,path); path.status=NavMeshPathStatus.PathPartial; return true; };
Check(Resolve(seed).Outcome=="no-reachable-surface","partial route is not a correction");
NavMesh.Path=(a,b,path)=>{ Complete(a,b,path); path.corners=[a,new(100,0,100)]; return true; };
Check(Resolve(seed).Outcome=="no-reachable-surface","path ends too far from the sample");
NavMesh.Path=Complete;
Physics.Blocked=_=>true;
Check(Resolve(seed).Outcome=="no-reachable-surface","reject obstructed standing positions");
Physics.Blocked=_=>false;
Floors(50);
Check(Resolve(seed).Outcome=="no-compatible-surface","unbounded height cannot select another building");

Physics.Ground=p=>new(new(p.x,50,p.z),new object());
Check(Resolve(seed).Outcome=="no-compatible-surface","building roof cannot substitute for distant terrain");
Physics.Ground=p=>new(new(p.x,50,p.z),new TerrainCollider());
Check(Resolve(seed).Result.y==50,"recover a large outdoor height error backed by terrain and mesh");
Physics.Ground=_=>null;

var w=new WaypointSystem();
var site=new RushPoint();
site.FloorId = null;
Floors(12);
Vector3? Run(WaypointSystem system, string role, out int samples)
{
    AuthoredPointSearch search=null;
    for (int i=0;i<140;i++)
        if (system.RepairPoint(site,role,seed,seed,agent,null,ref search,out var result))
        { samples=search.Samples; return result; }
    throw new InvalidOperationException("unbounded cache search");
}
Check(Run(w,"approach",out var first)?.y==12 && first>0,"first squad measures");
Check(Run(w,"approach",out var repeat)?.y==12 && repeat==0,"next squad checks the cached path");
Check(Log.Messages.Count==1,"one correction log");
NavMesh.Path=(a,b,path)=>false;
Check(Run(w,"approach",out var disconnected)==null && disconnected>0,"cached point rechecked for each origin");
NavMesh.Path=Complete;
Check(Run(w,"approach",out var restored)?.y==12 && restored==0,"actor failure does not invalidate the raid cache");
Check(Run(w,"interior",out var interior)?.y==12 && interior>0,"interior separate from approach");
Check(Run(new(),"approach",out var nextRaid)?.y==12 && nextRaid>0,"fresh map uses a fresh cache");

// Fixtures contain the responses of the 81 local sample probes on the original map geometry.
// Each row is probe index and closest point, absent rows mean no mesh in range.
foreach (var (name,position,low,high) in new[] {
    ("Customs", new Vector3(494.30374f,4.16f,106.07951f),15f,16f),
    ("HiddenHideoutCamp", new Vector3(-511.74313f,12.379f,-390.96237f),15f,16.5f) })
{
    var hits=new Dictionary<int,(Vector3 Probe, Vector3 Hit)>();
    var path=Path.Combine(AppContext.BaseDirectory,name+".csv");
    foreach (var row in File.ReadLines(path).Skip(1))
    {
        var f=Array.ConvertAll(row.Split(','),v=>float.Parse(v,System.Globalization.CultureInfo.InvariantCulture));
        hits[(int)f[0]]=(new(f[1],f[2],f[3]), new(f[4],f[5],f[6]));
    }
    int index=0;
    NavMesh.Sample=(p,r)=>
    {
        if (!hits.TryGetValue(index++,out var measure)) return null;
        Check(Vector3.Distance(p,measure.Probe)<.001f && r==2.35f,"same sample as map capture");
        return measure.Hit;
    };
    var found=Resolve(position);
    Check(found.Outcome=="corrected" && found.Result.y>=low && found.Result.y<=high,name+" selects walkable ground");
    Console.WriteLine($"{name} {position} -> {found.Result}, {found.Samples} samples, {found.Paths} path checks");
}
Console.WriteLine($"PASS {checks} authored point repair checks");
