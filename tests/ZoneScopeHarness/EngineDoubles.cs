using Orbit.Entities;
using Orbit.Navigation;
using Orbit.Zones;
using UnityEngine;

namespace UnityEngine
{
    [Newtonsoft.Json.JsonObject(Newtonsoft.Json.MemberSerialization.Fields)]
    public struct Vector2(float x, float y)
    {
        public float x = x, y = y;
        public static Vector2 zero => new(0, 0);
        public float magnitude => MathF.Sqrt(x*x+y*y);
        public Vector2 normalized => magnitude > 0 ? new(x/magnitude,y/magnitude) : zero;
        public static Vector2 operator -(Vector2 a,Vector2 b) => new(a.x-b.x,a.y-b.y);
        public static Vector2 operator +(Vector2 a,Vector2 b) => new(a.x+b.x,a.y+b.y);
        public static Vector2 operator *(float n,Vector2 v) => new(n*v.x,n*v.y);
        public static float Distance(Vector2 a,Vector2 b) => (a-b).magnitude;
    }
    public record struct Vector2Int(int x, int y)
    {
        public static implicit operator Vector2(Vector2Int p) => new(p.x,p.y);
    }
    public struct Vector3(float x,float y,float z) { public float x=x,y=y,z=z; }
    public static class Mathf
    {
        public static float Clamp01(float x)=>Math.Clamp(x,0,1);
        public static float Clamp(float x,float lo,float hi)=>Math.Clamp(x,lo,hi);
        public static float Pow(float x,float n)=>MathF.Pow(x,n);
        public static float Exp(float x)=>MathF.Exp(x);
        public static int CeilToInt(float x)=>(int)MathF.Ceiling(x);
    }
}
namespace Orbit
{
    public static class Log { public static readonly List<string> Lines=[]; public static void Info(string s)=>Lines.Add(s); public static void Warning(string s)=>Lines.Add(s); public static void Debug(string s)=>Lines.Add(s); }
}
namespace Orbit.Config
{
    public struct Range(float min,float max) { public float Min=min,Max=max; }
    public class ConfigBundle<T>(string path,T value) { public string Path=path; public T Value=value; }
}
namespace EFT
{
    public enum WildSpawnType { arenaFighter, arenaFighterEvent, assault, assaultGroup, bossBoar, bossBoarSniper, bossBully, bossGluhar, bossKilla, bossKnight, bossKojaniy, bossKolontay, bossPartisan, bossSanitar, bossTagilla, bossTest, bossZryachiy, crazyAssaultEvent, cursedAssault, exUsec, followerBigPipe, followerBirdEye, followerBoar, followerBoarClose1, followerBoarClose2, followerBully, followerGluharAssault, followerGluharScout, followerGluharSecurity, followerGluharSnipe, followerKojaniy, followerKolontayAssault, followerKolontaySecurity, followerSanitar, followerTagilla, followerTest, followerZryachiy, pmcBEAR, pmcBot, pmcUSEC, sectantPriest, sectantWarrior, untarTest, ruafTest, remnantTest, blackDivTest, ISBTest, CombineTest, unclassifiedTest }
    public class BotOwner { public Profile Profile=new(); }
    public class Profile { public Info Info=new(); }
    public class Info { public Settings Settings=new(); public string Nickname="test"; public string MainProfileNickname; }
    public class Settings { public WildSpawnType Role=WildSpawnType.pmcUSEC; }
}
namespace Orbit.Entities
{
    public class Squad { public Agent Leader=new(); public bool ExtractRequested; public List<MainObjective> MainObjectives=[]; }
    public class Agent { public EFT.BotOwner Bot=new(); public Vector3 Position; }
    public enum MainObjectiveType { Kills,LootValue,Quest }
    public class MainObjective { public bool Completed; public MainObjectiveType Type; public Vector2Int CellCoords; public string ZoneFloorId; }
}
namespace Orbit.Helpers
{
    public static class ServerConfig { public static ZoneConfig Zones=new(); }
    public class ZoneConfig { public float ZoneRadiusScale=1, ZoneForceScale=1, ZoneFalloffScale=1; }
}
namespace Orbit.Navigation
{
    public enum WaypointCategory { Quest,Exfil,Synthetic,ContainerLoot,LooseLoot,Corpse }
    public class Waypoint { public WaypointCategory Category; public Vector3 Position; public bool Reachable=true; }
}
namespace Orbit.Systems
{
    // Engine/grid doubles. All scope decisions under test come from the production partial class.
    public partial class WaypointSystem
    {
        private readonly string _zoneKey;
        private readonly NativeBotsController _botsController = new();
        public Dictionary<string,string> NativeMetadata(params NativeBotZone[] zones)
        {
            _botsController.BotSpawner._allBotZones=zones;
            return CollectNativeZoneFloors();
        }
        private readonly float _cellSize=10;
        private readonly Vector2Int _gridSize=new(10,10);
        private readonly List<Zone> _zones=[];
        private readonly Cell[,] _cells=new Cell[10,10];
        public WaypointSystem(string map)
        {
            _zoneKey=map;
            for(var x=0;x<10;x++)for(var y=0;y<10;y++) _cells[x,y]=new();
        }
        public void Add(Zone zone)=>_zones.Add(zone);
        public void Add(Waypoint point) { var c=WorldToCell(point.Position); _cells[c.x,c.y].Waypoints.Add(point); }
        public Vector2Int WorldToCell(Vector3 p)=>new((int)(p.x/10),(int)(p.z/10));
        private Vector2 WorldToCellCentered(Vector2 p)=>new(p.x/10-.5f,p.y/10-.5f);
        private static float XzDistanceSqr(Vector3 a,Vector3 b)=>(a.x-b.x)*(a.x-b.x)+(a.z-b.z)*(a.z-b.z);
        private static bool SquadCanUseWaypoint(Squad squad,bool pmc,Waypoint point)=>true;
        private static bool IsWaypointReachable(Waypoint point,Squad squad)=>point.Reachable;
        public Vector2 Attraction(Squad squad,Vector2Int cell)=>ScopedAttraction(squad,cell);
        public string Floor(Squad squad,Vector2Int cell)=>ZoneFloorForCell(squad,cell);
        public float Weight(Squad squad,Waypoint point)=>ScopedWaypointWeight(squad,point);
        public bool AllowsFloor(string floor,Waypoint point)=>MatchesDestinationFloor(floor,point);
        public class Cell { public List<Waypoint> Waypoints=[]; }
        public readonly struct Zone(Vector2 coords,float radius,float force,float decay,ZoneScope scope=null,Vector3 worldPosition=default,bool killMains=false)
        {
            public readonly Vector2 Coords=coords;
            public readonly float Radius=radius,Force=force,Decay=decay;
            public readonly ZoneScope Scope=scope;
            public readonly Vector3 WorldPosition=worldPosition;
            public readonly bool KillMains=killMains;
            public bool IsScoped=>Scope!=null&&(Scope.BotTypes!=null||!string.IsNullOrEmpty(Scope.FloorId));
        }
    }
}
public class NativeBotsController { public NativeSpawner BotSpawner=new(); }
public class NativeSpawner { public NativeBotZone[] _allBotZones=[]; }
public class NativeBotZone { public string name; public NativeSpawn[] SpawnPoints=[]; public NativePatrolWay[] PatrolWays=[]; }
public class NativeSpawn { public Vector3 Position; }
public class NativePatrolWay { public List<NativePatrolPoint> Points=[]; }
public class NativePatrolPoint
{
    public Vector3 Position;
    public List<NativePatrolPoint> SubPoints=[];
    public int SubPointsCount=>SubPoints.Count;
    public NativePatrolPoint GetSubPoint(int index)=>SubPoints[index];
}
namespace SPT.Common.Http
{
    public static class RequestHandler
    {
        public static string LastUrl, LastJson;
        public static Task<string> PostJsonAsync(string url,string json)
        {
            LastUrl=url;LastJson=json;
            return Task.FromResult("{}");
        }
    }
}
namespace SPTarkov.Common.Models.Logging
{
    public interface ISptLogger<T> { void Info(string s); void Error(string s); }
    public class TestLogger<T>:ISptLogger<T> { public void Info(string s) {} public void Error(string s) {} }
}
namespace SPTarkov.DI.Annotations
{
    public enum InjectionType { Singleton }
    [AttributeUsage(AttributeTargets.Class)] public class InjectableAttribute(InjectionType type):Attribute { public InjectionType Type=type; }
}
