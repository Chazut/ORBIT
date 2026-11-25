using System;
using System.Collections.Generic;
using Orbit.Entities;
using Orbit.Navigation;
using UnityEngine;

namespace Orbit.Settings { public enum CampSiteKind { Hotspot, Extract, Airdrop } }

namespace UnityEngine
{
    public static class Time { public static float time, deltaTime = .02f; }
    public enum FindObjectsSortMode { None }
    public class Object
    {
        public string name = "test";
        public static readonly Dictionary<Type, object> Found = new();
        public static T[] FindObjectsByType<T>(FindObjectsSortMode mode) => Found.TryGetValue(typeof(T), out var a) ? (T[])a : Array.Empty<T>();
    }
    public class MonoBehaviour : Object
    {
        public Transform transform = new(); public GameObject gameObject = new();
        public T GetComponent<T>() where T : class => gameObject.GetComponent<T>();
    }
    public class GameObject : Object
    {
        public Transform transform = new();
        public readonly Dictionary<Type, object> Components = new();
        public int ComponentLookups;
        public T GetComponent<T>() where T : class { ComponentLookups++; return Components.TryGetValue(typeof(T), out var c) ? c as T : null; }
        public T AddComponent<T>() where T : new() { var t = new T(); Components[typeof(T)] = t; return t; }
    }
    public class LODGroup { }
    public class Transform : Object {
        public Transform parent;
        public readonly Dictionary<Type,object> Components = new();
        public readonly List<object> ChildComponents = new();
        public T GetComponent<T>() where T:class => Components.TryGetValue(typeof(T),out var c) ? c as T : null;
        public void GetComponentsInChildren<T>(bool includeInactive, List<T> result) { result.Clear(); foreach(var c in ChildComponents) if(c is T t) result.Add(t); }
        public bool IsChildOf(Transform other) => this == other || parent?.IsChildOf(other) == true; public Transform Original=>this; public Vector3 position, forward = new(0,0,1), lossyScale = Vector3.one; public Vector3 InverseTransformPoint(Vector3 p) => p - position; }
    public partial struct Vector3(float x, float y, float z)
    {
        public float x = x, y = y, z = z;
        public float sqrMagnitude => x*x+y*y+z*z;
        public float magnitude => MathF.Sqrt(sqrMagnitude);
        public static Vector3 up => new(0,1,0); public static Vector3 one => new(1,1,1);
        public Vector3 normalized => magnitude > 0 ? this / magnitude : default;
        public void Normalize() { this = normalized; }
        public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.x+b.x,a.y+b.y,a.z+b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.x-b.x,a.y-b.y,a.z-b.z);
        public static Vector3 operator -(Vector3 a) => a * -1;
        public static Vector3 operator *(Vector3 a, float b) => new(a.x*b,a.y*b,a.z*b);
        public static Vector3 operator *(float b, Vector3 a) => a*b;
        public static Vector3 operator /(Vector3 a, float b) => a*(1f/b);
        public static float Distance(Vector3 a, Vector3 b) => (a-b).magnitude;
#if !EXTRACTION_HARNESS
        public static float Dot(Vector3 a, Vector3 b) => a.x*b.x + a.y*b.y + a.z*b.z;
#endif
        public static Vector3 Scale(Vector3 a, Vector3 b) => new(a.x*b.x,a.y*b.y,a.z*b.z);
        public override string ToString() => $"({x},{y},{z})";
    }
    public static partial class Mathf
    {
        public static float InverseLerp(float a,float b,float v) => a == b ? 0 : Math.Clamp((v-a)/(b-a),0,1);
        public static float Sqrt(float v) => MathF.Sqrt(v);
        public static float Abs(float v) => MathF.Abs(v);
        public static float Max(float a,float b) => MathF.Max(a,b);
        public static float Min(float a,float b) => MathF.Min(a,b);
    }
    public static class Random { public static float value => 0; public static float Range(float a,float b) => a; public static int Range(int a,int b) => a; }
    public struct Quaternion { public static Quaternion AngleAxis(float a,Vector3 b) => new(); public static Vector3 operator *(Quaternion q,Vector3 v) => v; }
    public struct Bounds { public Vector3 min,max; public Vector3 size => max-min; public Vector3 center => (min+max)*.5f; public Vector3 extents => size*.5f; public bool Inside,Spatial; public bool Contains(Vector3 p) => Spatial ? p.x>=min.x && p.y>=min.y && p.z>=min.z && p.x<=max.x && p.y<=max.y && p.z<=max.z : Inside; }
    public readonly record struct Ray(Vector3 origin, Vector3 direction);
    public class Mesh : Object { }
    public class MeshCollider : Collider { public Mesh sharedMesh; }
    public class Collider : Object { public bool Raycast(Ray ray, out RaycastHit hit, float length) { hit = default; return false; } public Transform transform = new(); public Bounds bounds; public Func<Vector3,Vector3> Closest; public Vector3 ClosestPoint(Vector3 p) => Closest?.Invoke(p) ?? new(Math.Clamp(p.x,bounds.min.x,bounds.max.x),Math.Clamp(p.y,bounds.min.y,bounds.max.y),Math.Clamp(p.z,bounds.min.z,bounds.max.z)); }
    public class BoxCollider : Collider { public Vector3 size, center; public bool enabled = true; }
    public enum QueryTriggerInteraction { Ignore }
    public static partial class Physics {
        public static bool Blocked;
        public static Collider FirstHit;
        public static Vector3 LastTarget, LastOrigin;
        public static int SightQueries, LastMask;
        public static QueryTriggerInteraction LastTriggers;
        public static int RaycastNonAlloc(Vector3 origin, Vector3 direction, RaycastHit[] hits, float distance, int mask, QueryTriggerInteraction triggers)
        {
            if (!Raycast(origin, direction, out var first, distance, mask, triggers) || first.collider == null) return 0;
            hits[0] = first;
            return 1;
        }
        public static RaycastHit[] RaycastAll(Vector3 origin, Vector3 direction, float distance, int mask, QueryTriggerInteraction triggers)
            => Blocked ? new[] { new RaycastHit { collider = FirstHit, distance = distance * .5f } } : Array.Empty<RaycastHit>();
        public static bool Raycast(Vector3 a,Vector3 b,float c,int d) => Blocked;
        public static bool Raycast(Vector3 a,Vector3 b,out RaycastHit hit,float c,int d,QueryTriggerInteraction triggers) {
            SightQueries++; LastOrigin=a; LastTarget=a+b*c; LastMask=d; LastTriggers=triggers;
            hit=new(){collider=FirstHit,distance=c*.5f}; return Blocked;
        }
    }
    public struct QueryParameters { public int layerMask; }
    public struct RaycastHit { public Collider collider; public float distance; public Vector3 point, normal; }
    public struct RaycastCommand
    {
        public float distance; public Vector3 direction;
        public RaycastCommand(Vector3 a, Vector3 b, QueryParameters p, float d) { distance=d;direction=b; }
        public static Unity.Jobs.JobHandle ScheduleBatch(Unity.Collections.NativeArray<RaycastCommand> a,Unity.Collections.NativeArray<RaycastHit> b,int c) => new();
    }
}
namespace UnityEngine.AI {
#if !EXTRACTION_HARNESS
    public enum NavMeshPathStatus { PathComplete, PathPartial }
    public class NavMeshPath { public NavMeshPathStatus status; }
    public static partial class NavMesh {
        public static bool Connected = true;
        public static bool CalculatePath(Vector3 a, Vector3 b, int areas, NavMeshPath path)
        { path.status = Connected ? NavMeshPathStatus.PathComplete : NavMeshPathStatus.PathPartial; return true; }
    }
#endif
    public class NavMeshObstacle { public bool carving,enabled; public Vector3 center,size; }
    public struct NavMeshHit { public Vector3 position; }
    public static partial class NavMesh {
        public const int AllAreas=-1; public static int Queries;
        public static Func<Vector3,float,Vector3?> Sampler;
        public static bool SamplePosition(Vector3 p,out NavMeshHit hit,float radius,int areas) {
            Queries++;var result=Sampler?.Invoke(p,radius);hit=new(){position=result??default};return result.HasValue;
        }
    }
}
namespace Unity.Collections
{
    public enum Allocator { TempJob }
    public struct NativeArray<T>
    {
        private T[] items;
        public NativeArray(int size,Allocator a) { items = new T[size]; }
        public int Length => items.Length;
        public T this[int i] { get => items[i]; set => items[i] = value; }
        public void Dispose() { }
    }
}
namespace Unity.Jobs { public struct JobHandle { public bool IsCompleted => true; public void Complete() { } } }
public enum CoverLevel { Stay, Sit }
public enum CoverType { Wall, Foliage, Other }
public static class LayersMaskController { public const int HighPolyWithTerrainMask = 1; }
public static class LocationScene { public static T[] GetAllObjects<T>() => Array.Empty<T>(); }
namespace EFT.Ballistics { public static class BallisticsCalculatorConstants { public const int HitMask=1; } }
namespace EFT.Interactive
{
    public class InteractableObject : MonoBehaviour { public Transform Pelvis; public Transform TrackableTransform => Pelvis ?? transform; }
    public class LootableContainer : InteractableObject { public Collider Collider; }
    public class Corpse : InteractableObject { }
    public class LootItem : InteractableObject { }
    public enum EDoorState { Locked, Shut, Interacting, Open }
    public partial class Door : MonoBehaviour { public EDoorState DoorState; }
    public class Minefield : MonoBehaviour { }
    public class SniperFiringZone : MonoBehaviour { }
    public enum EExfiltrationStatus { UncompleteRequirements,RegularMode,Countdown,NotPresent,Hidden,AwaitsManualActivation }
    public enum EExfiltrationType { Individual,SharedTimer,Manual }
    public class ExfilSettings { public EExfiltrationType ExfiltrationType; public string Name; public float ExfiltrationTime; }
    public class ExfiltrationPoint : MonoBehaviour
    {
        public event Action<ExfiltrationPoint, EExfiltrationStatus> OnStatusChanged;
        public EExfiltrationStatus Status; public ExfilSettings Settings = new();
        public void OnItemTransferred(string id) { }
        public void SetStatusLogged(EExfiltrationStatus s,string message) {
            var previous=Status;
            Status=s;
            if(s==EExfiltrationStatus.NotPresent && gameObject.Components.TryGetValue(typeof(Collider),out var c))
                  ((Collider)c).bounds = default;
            OnStatusChanged?.Invoke(this,previous);
        }
    }
}
namespace Orbit
{
    public static class Log
    {
        public static readonly List<string> Messages = new();
        public static void Debug(string s) => Messages.Add(s);
        public static void Info(string s) => Messages.Add(s);
        public static void Warning(string s) => Messages.Add(s);
        public static void Error(string s) => Messages.Add(s);
    }
    public static class ServerConfig
    {
        public static MainConfig MainObjectives = new(); public static LootConfig Loot = new();
        public class MainConfig { public float SyntheticVisitCooldownSeconds=120; public bool ExtractOnAllCompleted=true; }
        public class LootConfig { public BotSwitch LootingEnabled = new(); }
        public class BotSwitch { public bool IsBotEnabled(int role) => true; }
    }
}
namespace Orbit.Looting { }
namespace Orbit.Helpers
{
    public static class SprintGate { public static bool IsAllowedByFaction(Agent a) => true; public static float Propensity(Agent a) => a.Squad?.Personality?.SprintPropensity ?? .5f; }
    public static class Extensions { public static void SwapRemoveAt<T>(this List<T> a,int i) { a[i]=a[a.Count-1];a.RemoveAt(a.Count-1); } }
    public static class PerfMonitor { public static int SweepJobsSubmitted,SweepJobsDrained,SweepJobsCompleted; }
    public class TimePacing(float t) { public bool Blocked() => t < 0; }
}
namespace Orbit.Entities
{
    public class Entity(int id,float[] scores) { public int Id=id; public float[] TaskScores=scores; public Orbit.Tasks.TaskAssignment TaskAssignment; }
    public class Dataset<T> { public List<T> Values = new(); }
    public class AgentData { public Dataset<Agent> Entities = new(); }
    public class SquadData { public Dataset<Squad> Entities = new(); }
    public partial class Squad : Entity
    {
        public CampPort Camp = new();
        public EscortPort CorpseEscort=new();
        public int Size = 1;
        public List<Agent> Members = new();
        public Waypoint NearestExfilCached;
        public float NearestExfilCachedAt;
        public Squad() : base(0,new float[4]) { }
        public SquadObjective Objective = new(); public HashSet<int> CompletedPoiIds = new();
        public Dictionary<int,float> RecentlyVisitedPoiCooldowns = new(); public bool ExtractRequested;
        public Personality Personality = new();
    }
    public partial class Personality { public float SprintPropensity=0.5f; }
    public class CampPort { public Agent Looter; public bool Enabled; public CampSitePort Site = new(); public bool Owns(Agent agent) => Enabled; }
    public class CampSitePort { public Vector3 Position; public Orbit.Settings.CampSiteKind Kind; }
    public enum MovementStatus { Moving,Stopped,Failed }
    public enum MovementUrgency { Low }
    public partial class Movement { public Vector3 Target; public int PathRevision,Retry;public float DoorInteractHoldUntil,NextGhostDoorCheck,Speed=1;public bool Prone;public MovementUrgency Urgency;public MovementStatus Status; public bool HasPath,Sprint; public float Pose=1; public Vector3[] Path; }
    public enum GuardStatus { None,Moving,Sweep,Watch }
    public class Guard { public CoverPoint? CoverPoint; public GuardStatus Status; public int ExtractWatchProbe, ExtractWatchMisses; public AreaSweepJob AreaSweepJob; public List<Vector3> WatchDirections = new(); public float WatchTimeout; }
    public class AreaSweepJob { public int Count; public bool IsCompleted = true; public Unity.Jobs.JobHandle Handle; public Unity.Collections.NativeArray<RaycastCommand> Commands; public Unity.Collections.NativeArray<RaycastHit> Hits; }
    public enum LookType { Position, Direction }
    public class Look { public Vector3? Target; public LookType Type; }
    public partial class Agent : Entity
    {
        #if !EXTRACTION_HARNESS
        public QuestPort QuestArrival=new();
#endif
        public Agent() : base(1,new float[4]) { }
        public bool IsActive=true,IsDormant,SoloExtractRequested;
        public string SoloExtractReason;public Waypoint SoloExtractTarget;
        public Objective Objective = new(); public Movement Movement = new(); public Squad Squad = new();
        public Guard Guard = new(); public Look Look = new(); public Player Player = new(); public Bot Bot = new();
        public Vector3 Position { get => Player.Transform.position; set => Player.Transform.position=value; }
        public readonly Orbit.Navigation.ArrivalFailureHistory ArrivalFailures=new();
        public int LoSBlockedPoiId=-1;
        public float LastFailedPoiDistance=float.MaxValue,LoSBlockedSinceTime=-1,GuardOnLootPoiSinceTime=-1;
    }
    public partial class Player { public Transform Transform = new(); public Bones PlayerBones = new(); public string ProfileId="bot"; }
    public class Bones { public Transform Head = new(); }
    public partial class Bot { public Player GetPlayer = new(); public Sensor LookSensor; public Profile Profile = new(); public LeaveData LeaveData=new(); public void SetPose(float v) {} }
    public class LeaveData { public int Removals; public void RemoveFromMap() => Removals++; }
    public class Sensor { public Vector3 HeadPoint; }
    public partial class Profile { public Info Info = new(); }
    public class Info { public Settings Settings = new(); }
    public class Settings { public int Role; }
}
namespace Comfort.Common { public static class Singleton<T> { public static T Instance; } }
namespace Orbit.Core { public class OrbitManager { public Action<Agent> Removed; public void RemoveAgent(Agent a) => Removed?.Invoke(a); } }
namespace Orbit.Systems
{
    public partial class MovementSystem
    {
        public int Orders; public Vector3 Destination;
        public void MoveToByPath(Agent a,Vector3 p,float pose=1,float speed=1,bool prone=false,bool sprint=false,MovementUrgency urgency=MovementUrgency.Low,bool styledTravel=false)
        { Orders++;Destination=p;a.Movement.Target=p;a.Movement.Status=MovementStatus.Moving;a.Movement.Sprint=sprint;a.Movement.Pose=pose;a.Movement.HasPath=true;a.Movement.Path=new[]{a.Position,p}; }
        public void Step(Agent a,float distance)
        {
            if (a.Movement.Status!=MovementStatus.Moving) return;
            var delta=Destination-a.Position;
            if(delta.magnitude<=distance){a.Position=Destination;a.Movement.Status=MovementStatus.Stopped;}
            else a.Position+=delta.normalized*distance;
        }
        public static void ResetGait(Agent a,float pose=1) { a.Movement.Sprint=false;a.Movement.Pose=pose; }
        public void RegisterPoiBlacklistAndMaybeCloseDoors(Agent a) { }
    }
    public partial class WaypointSystem
    {
        public bool FloorReached=true,Claim=true;
        public bool HasReachedZoneFloor(Squad s,Waypoint w,Vector3 p) => FloorReached;
        public bool TryClaim(int id,int agentId) => Claim;
    }
    public static class LookSystem
    {
        public static Vector3 RandomDirectionInEllipse(Vector3 d,float a,float b)=>d;
        public static int Calls;
        public static Vector3 LastDirection;
        public static void LookToPoint(Agent a,Vector3 p,float speed) { Calls++; a.Look.Type=LookType.Position; a.Look.Target=p; LastDirection=(p-a.Position).normalized; }
        public static void LookToDirection(Agent a,Vector3 b,float speed=180f) { Calls++; a.Look.Type=LookType.Direction; a.Look.Target=b; LastDirection=b; }
    }
}

namespace Orbit.Entities {
    public class EscortPort {
        public Waypoint Target;
        public bool IsEscortWaypoint(Waypoint w)=>w==Target;
    }
    // These quest ports are outside the focused corpse-escort action scenario.
    public class QuestPort { public bool Check(Vector3 from,Vector3 to,out string reason) {reason="line-of-sight";return !Physics.Blocked;} }
}
namespace Orbit.Tasks {
#if !EXTRACTION_HARNESS
    public static class QuestObjectiveRecovery { public static void Retire(Squad s,Waypoint w,string why){} }
#endif
}
