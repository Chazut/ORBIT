using System;
using System.Collections.Generic;
using Orbit.Entities;
using Orbit.Systems;
using UnityEngine;
using UnityEngine.AI;
namespace UnityEngine
{
    public partial struct Vector3 { public static Vector3 zero=>default; }
    public static partial class Mathf { public const float PI=MathF.PI;public static float Cos(float x)=>MathF.Cos(x);public static float Sin(float x)=>MathF.Sin(x); }
    public struct LayerMask { public int value; }
    public static partial class Physics { public static bool Linecast(Vector3 a,Vector3 b,out object hit,int mask){hit=null;return Blocked;} }
}
namespace UnityEngine.AI
{
    public enum NavMeshPathStatus { PathComplete,PathPartial,PathInvalid }
    public class NavMeshPath { public NavMeshPathStatus status; public Vector3[] corners; }
    public static partial class NavMesh
    {
        public static bool Connected=true;public static int PathQueries;
        public static bool CalculatePath(Vector3 a,Vector3 b,int areas,NavMeshPath path)
        {PathQueries++;path.corners=Connected ? new[]{a,b} : Array.Empty<Vector3>();path.status=Connected?NavMeshPathStatus.PathComplete:NavMeshPathStatus.PathInvalid;return Connected;}
    }
}
public class BotMover
{
    public Orbit.Entities.Bot _owner;
    public Vector3 _lastGoodCastPoint,_prevSuccessLinkedFrom,_prevLinkPos,PositionOnWayInner;
    public float _lastGoodCastPointTime,_prevPosLinkedTime,_prevOffsetGoodCastedTime;
    public Vector3 _positionOnWayCasted,_prevOffsetGoodCasted;
    public bool _linkedToNavmeshInitially;
}
namespace Orbit.Entities
{
    public enum HardStuckStatus { None }
    public class Counter {public void Reset(){}}
    public class HardStuck {public HardStuckStatus Status;public float Timer;public Counter AverageSpeed=new(),PositionHistory=new();}
    public class SoftStuck {public void Reset(){}}
    public class Stuck
    {
        internal readonly LocalEscapeSearch LocalEscape=new();public bool IdleRescueIntent;public float IdleRescueLastObservedAt=-1;
        public HardStuck Hard=new();public SoftStuck Soft=new();public int GhostInvalidPathStreak;
        internal readonly OrbitMovementRecovery Recovery=new();
        public Vector3 IdleRescueAnchor;public float IdleRescueSince=-1,IdleRescueLastMovingAt=-1;
    }
    public partial class Agent { public Stuck Stuck=new(); }
    public partial class Squad { public int ConsecutiveDispatchFailures;public float NextDispatchAttemptAt; }
    public partial class Bot {public bool IsDead;}
    public class Health { public bool IsAlive=true; }
    public partial class Player
    {
        public Health HealthController=new();public Vector3 Position=>Transform.position;public int Teleports;
        public void Teleport(Vector3 p){Transform.position=p;Teleports++;}
    }
}
namespace Orbit.Systems
{
    public partial class MovementSystem
    {
        private readonly WaypointSystem _waypointSystem=new();
        private bool IsPathComplete(Vector3 a,Vector3 b)=>NavMesh.CalculatePath(a,b,NavMesh.AllAreas,_rescuePath)&&_rescuePath.status==NavMeshPathStatus.PathComplete;
        private readonly NavMeshPath _rescuePath=new();
        private readonly List<Player> _humanPlayers=new();
        private static LayerMask TeleportVisLayerMask=new(){value=1};
        public bool SourceSafe=true;
        private bool TeleportSafe(Agent a,List<Player> humans)=>SourceSafe;
        private static void ResetPath(Agent a){a.Movement.Status=MovementStatus.Stopped;a.Movement.Path=null;a.Movement.HasPath=false;}
        public int RecoveryPathQueries { get; private set; }
        public void RescueTick(Agent agent)
        {
            var before = NavMesh.PathQueries;
            agent.Bot.GetPlayer = agent.Player;
            _recoveryAgents = agent.Squad?.Members;
            ObserveRescueProgress(agent);
            TryIdleIslandRescue(agent);
            RecoveryPathQueries += NavMesh.PathQueries - before;
        }
    }
}
namespace Orbit.Navigation { public static class DangerZones {public static bool IsInside(Vector3 p)=>false;} }
namespace Orbit.Systems { public partial class WaypointSystem {public void ClearSquadUnreachability(Squad squad){} } }
