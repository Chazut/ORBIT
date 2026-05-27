using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Orbit.Helpers;
using Orbit.Navigation;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Orbit.Entities;

// ╔══════════════════════════════════════════════════════════════════╗
// ║ Per-agent component blocks. Each piece is small and they're       ║
// ║ always read together as the agent's "current state", so they      ║
// ║ live in one file. Enums sit next to the classes that consume them.║
// ╚══════════════════════════════════════════════════════════════════╝

// ── Guard ─────────────────────────────────────────────────────────────

public struct AreaSweepJob
{
    public JobHandle Handle;
    public NativeArray<RaycastCommand> Commands;
    public NativeArray<RaycastHit> Hits;
}

public enum GuardStatus
{
    None,
    Moving,
    Sweep,
    Watch,
}

public class Guard
{
    public GuardStatus Status;
    public CoverPoint? CoverPoint;
    public AreaSweepJob? AreaSweepJob;
    public float WatchTimeout;
    public readonly List<Vector3> WatchDirections = [];

    public override string ToString()
        => $"{nameof(Guard)}({CoverPoint}, status: {Status} directions: {WatchDirections.Count})";
}

// ── Look ──────────────────────────────────────────────────────────────

public enum LookType
{
    Position,
    Direction
}

public class Look
{
    public Vector3? Target = null;
    public LookType Type = LookType.Position;
}

// ── Movement ──────────────────────────────────────────────────────────

public enum MovementStatus
{
    Stopped,
    Moving,
    Failed
}

public enum MovementUrgency
{
    High,
    Medium,
    Low
}

public class Movement
{
    public static readonly Vector3 Infinity = new(float.MaxValue, float.MaxValue, float.MaxValue);

    /// <summary>Sentinel "no target" — far enough from anywhere that distance
    /// checks against valid positions all read as way out of range.</summary>
    public Vector3 Target = Infinity;
    public Vector3[] Path;
    public MovementStatus Status = MovementStatus.Stopped;

    public int CurrentCorner;
    public int Retry;

    public float Speed = 1f;
    public float Pose = 1f;
    public bool Sprint = false;
    public bool Prone = false;
    public MovementUrgency Urgency = MovementUrgency.Medium;

    public bool HasPath
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Path is { Length: > 0 };
    }

    public readonly TimePacing VoxelUpdatePacing = new(0.25f);

    public override string ToString()
        => $"Movement(Path: {CurrentCorner}/{Path?.Length}, Status: {Status} Retry: {Retry} Speed: {Speed}, Pose: {Pose} Sprint {Sprint}, Prone: {Prone})";
}

// ── Stuck (soft + hard) ───────────────────────────────────────────────

public enum SoftStuckStatus
{
    None,
    Vaulting,
    Jumping,
    Failed
}

public enum HardStuckStatus
{
    None,
    Retrying,
    Teleport,
    Failed
}

public class HardStuck
{
    public readonly PositionHistory PositionHistory = new(50);
    public readonly RollingAverage AverageSpeed = new(50);

    public HardStuckStatus Status = HardStuckStatus.None;
    public float LastUpdate;
    public float Timer;

    public override string ToString()
    {
        var moveDist = Mathf.Sqrt(PositionHistory.GetDistanceSqr());
        return $"HardStuck(status: {Status}, timer: {Timer}, avgSpeed: {AverageSpeed.Value} moveDist: {moveDist})";
    }
}

public class SoftStuck
{
    public Vector3 LastPosition;
    public float LastSpeed;

    public SoftStuckStatus Status = SoftStuckStatus.None;
    public float LastUpdate;
    public float Timer;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Reset()
    {
        Status = SoftStuckStatus.None;
        Timer = 0f;
    }

    public override string ToString()
        => $"SoftStuck(status: {Status}, timer: {Timer}, lastSpeed: {LastSpeed}";
}

public class Stuck
{
    public readonly TimePacing Pacing = new(0.1f);

    public HardStuck Hard = new();
    public SoftStuck Soft = new();

    public override string ToString() => $"Stuck(soft: {Soft} hard: {Hard})";
}
