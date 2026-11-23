using System;
using System.Collections.Generic;
using UnityEngine;
namespace UnityEngine
{
    public struct Vector2Int { public int x,y; }
    public static partial class Physics
    {
        public static Func<Vector3,Vector3,bool> LineBlock;
        public static int QuestQueries;
        public static bool Linecast(Vector3 a, Vector3 b, int mask, QueryTriggerInteraction triggers)
        { QuestQueries++; return LineBlock?.Invoke(a,b) ?? Blocked; }
    }
}
namespace UnityEngine.AI
{
    public static partial class NavMesh
    {
        public static bool Raycast(Vector3 a, Vector3 b, out NavMeshHit hit, int mask)
        { hit=new(); return !Connected; }
    }
}
namespace Orbit.Entities
{
    public partial class Agent { internal readonly Orbit.Systems.QuestArrival QuestArrival=new(); }
    public partial class Squad { public List<MainObjective> MainObjectives; public string ExtractRequestedReason; }
}
namespace Orbit.Api { public static class OrbitTelemetry { public static int MainObjectivesRevision; } }
namespace Orbit.Systems
{
    public static class BotLandingGuard {
        public static bool TryHandoffLanding(Orbit.Entities.Bot bot,out Vector3 point){point=default;return false;}
        public static bool IsRejected(Orbit.Entities.Bot bot,Vector3 point) => false;
        public static bool Accepts(Orbit.Entities.Bot bot,Vector3 point)=>true;
        public static bool TryPlace(Orbit.Entities.Bot bot,Vector3 point,string source,Action repath=null,bool validateGround=false){bot.GetPlayer.Teleport(point);return true;}
    }
    public static class BotGroundPlacement
    {
        public static bool Finite(Vector3 p) => float.IsFinite(p.x) && float.IsFinite(p.y) && float.IsFinite(p.z);
        public static bool HasSupport(Orbit.Entities.Player p) => true;
        public static bool TryResolve(Orbit.Entities.Player p,Vector3 v,out Vector3 landing,out string reason){landing=v;reason=null;return true;}
    }
}
