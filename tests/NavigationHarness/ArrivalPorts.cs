using System.Collections.Generic;
using System;
using Orbit.Entities;
using Orbit.Navigation;
using UnityEngine;

namespace Orbit.Navigation
{
    public static class NoBackpackExfil
    {
        public enum Preparation { Ready, Rejected, Waiting }
        public static void PauseRecovery(Agent a) { }
        public static bool CanRecover(Agent a, Waypoint p) => false;
        public static bool RecoveryDue(Agent a, Waypoint p) => false;
        public static bool CanFinish(Agent a, Waypoint p) => true;
        public static bool RequiresDrop(EFT.Interactive.ExfiltrationPoint e) => false;
        public static Preparation Prepare(Agent a, Waypoint p) => Preparation.Ready;
        public static bool RetainAfterDrop(Agent a, Waypoint p) => false;
    }
}
namespace Orbit.Systems
{
    public static class MultiStepAccess
    {
        public static bool IsConditional(EFT.Interactive.ExfiltrationPoint e) => false;
        public static bool ExitActive(EFT.Interactive.ExfiltrationPoint e, Agent a) => true;
    }
    public partial class MovementSystem { public static bool IsTravelPaused(Agent a) => false; }
    public partial class WaypointSystem { public bool HasReachedObjectiveFloor(Agent a, Waypoint p, Vector3 feet) => FloorReached; }
#if !EXTRACTION_HARNESS
    // The extraction harness drives the full Voron detour with scene and movement ports.
    internal static class VoronEntranceRoute { internal static bool InLootRoom(Vector3 feet) => false; }
    internal sealed class VoronLootApproach
    {
        internal VoronLootApproach(Agent a, MovementSystem m, WaypointSystem w) { }
        internal enum Result { Working, Inside, Failed, Unavailable }
        internal Result Tick() => Result.Failed;
        internal bool Owns(Waypoint point) => false;
        internal static bool Allowed(Agent agent, EFT.Interactive.Door door) => false;
        internal void Cancel() { }
    }
    public partial class MovementSystem
    {
        internal static void StopAtCorpse(Agent a)
        {
            a.Movement.Target = a.Position;
            a.Movement.Path = null;
            a.Movement.HasPath = a.Movement.Sprint = false;
            a.Movement.Status = MovementStatus.Stopped;
        }
    }
    // Extraction has its own route harness. Keep the existing arrival tests at the final gate.
    public class ExfilApproach
    {
        public ExfilApproach(Agent agent, MovementSystem movement, WaypointSystem waypoints) { }
        public void Pause() { }
        public bool Tick(bool sprint) => false;
    }
#endif
}
namespace Orbit.Entities
{
    public partial class Squad { public float GhostFightUntil; public RushPort Rush; }
    public class RushPort { public SniperPort Sniper; }
    public class SniperPort { public bool Watch(Agent a) => false; public bool IsPostShift(Agent a) => false; }
    public class AreaSweepPool
    {
        public const int MaxCandidates = 32;
        public void Poll() { }
        public void FlushScheduled() { }
        public void Retire(AreaSweepJob job) { }
        public void Release(AreaSweepJob job) { }
        public void Dispose() { }
        public AreaSweepJob Submit(Vector3 origin, List<Vector3> directions, int mask) => new();
    }
}
namespace EFT.Interactive { public class TriggerWithId : MonoBehaviour { } }
namespace Orbit.Navigation { public static class QuestWaypointPlacement { public static bool Contains(Collider c, Vector3 p) => c.bounds.Contains(p); } }
namespace Orbit.Navigation { public static class FlareExfil { public static bool RequiresSignal(EFT.Interactive.ExfiltrationPoint p) => false; } }
namespace Orbit.Helpers { public static class NativeExtractionDiagnostics { public static Timing Begin(string id) => new(); } }
namespace Orbit.Helpers
{
    public enum TransitionPhase { StrategyScores, StrategyTasks, StrategyPick, GuardComplete }
    public struct Timing : IDisposable { public void Dispose() { } }
    public static partial class PerformanceJournal { public static Timing Measure(TransitionPhase p, string label, object task, int id=0) => new(); }
    public static class TransitionPerformance { public static Timing Measure(TransitionPhase p) => new(); }
}
