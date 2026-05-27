using System.Collections.Generic;
using System.Runtime.CompilerServices;
using EFT.InventoryLogic;
using UnityEngine;

namespace Orbit.Helpers;

/// <summary>
/// Catch-all for small, single-purpose static helpers that don't deserve
/// their own file. Grows phase by phase as later subsystems land. Phase 2
/// covers the bare minimum needed by entities + components: list utility
/// extensions, the two pacing variants, rolling-average accumulator, and
/// Vector3↔Vector2 conversions.
/// </summary>
public static class ListHelpers
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool SwapRemove<T>(this List<T> list, T member)
    {
        for (var i = 0; i < list.Count; i++)
        {
            var candidate = list[i];
            if (!candidate.Equals(member)) continue;
            list.SwapRemoveAt(i);
            return true;
        }
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SwapRemoveAt<T>(this List<T> list, int index)
    {
        var lastIndex = list.Count - 1;
        list[index] = list[lastIndex];
        list.RemoveAt(lastIndex);
    }
}

public static class VectorExtensions
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector2 ToVector2(this Vector3 vector3) => new(vector3.x, vector3.z);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 ToVector3(this Vector2 vector2) => new(vector2.x, 0f, vector2.y);
}

/// <summary>
/// Gate that returns true only once every <c>Interval</c> seconds; used to
/// throttle per-tick work (e.g. corpse scan, gizmo refresh) without
/// scattering manual time-since-last-call bookkeeping across systems.
/// </summary>
public class TimePacing(float interval)
{
    public readonly float Interval = interval;
    private float _triggerTime;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Blocked()
    {
        if (Time.time < _triggerTime) return true;
        _triggerTime = Time.time + Interval;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Allowed()
    {
        if (Time.time < _triggerTime) return false;
        _triggerTime = Time.time + Interval;
        return true;
    }
}

/// <summary>Frame-count variant of <see cref="TimePacing"/>.</summary>
public class FramePacing(int interval)
{
    private float _triggerCount;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Blocked()
    {
        if (Time.time < _triggerCount) return true;
        _triggerCount = Time.frameCount + interval;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Allowed()
    {
        if (Time.time < _triggerCount) return false;
        _triggerCount = Time.frameCount + interval;
        return true;
    }
}

/// <summary>
/// Fixed-window rolling-average accumulator. Periodically re-sums the
/// buffer (every <c>recalcInterval</c> updates) to prevent floating-point
/// drift on very long runs.
/// </summary>
public class RollingAverage(int windowSize, int recalcInterval = 1000)
{
    private readonly float[] _buffer = new float[windowSize];
    private int _writeIndex;
    private float _sum;
    private int _count;
    private int _updatesSinceRecalc;

    public float Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _count > 0 ? _sum / _count : 0f;
    }

    public void Update(float value)
    {
        if (_count >= _buffer.Length)
            _sum -= _buffer[_writeIndex];

        _buffer[_writeIndex] = value;
        _sum += value;
        _writeIndex = (_writeIndex + 1) % _buffer.Length;

        if (_count < _buffer.Length)
            _count++;

        if (_updatesSinceRecalc < recalcInterval)
        {
            _updatesSinceRecalc++;
            return;
        }

        Recalculate();
        _updatesSinceRecalc = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Recalculate()
    {
        _sum = 0f;
        for (var i = 0; i < _count; i++)
            _sum += _buffer[i];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Reset()
    {
        _writeIndex = 0;
        _sum = 0f;
        _count = 0;
        _updatesSinceRecalc = 0;
    }
}

/// <summary>
/// Sort a collider list by squared distance to a reference position.
/// Used by the inventory pickup chain to walk nearby containers in
/// nearest-first order.
/// </summary>
public class ColliderDistanceComparer(Vector3 referencePosition) : IComparer<Collider>
{
    public int Compare(Collider x, Collider y)
    {
        var distX = (x.bounds.center - referencePosition).sqrMagnitude;
        var distY = (y.bounds.center - referencePosition).sqrMagnitude;
        return distX.CompareTo(distY);
    }
}

/// <summary>
/// Non-LINQ scans over BSG's controller event lists — the LINQ-based
/// equivalents allocate enumerators every tick which adds up across
/// hundreds of bots. Same checks BSG does internally, just hand-rolled.
/// </summary>
public static class ControllerExtensions
{
    public static bool IsChangingWeaponNonLinq(this InventoryController controller)
    {
        foreach (var activeEvent in controller.List_0)
        {
            if (activeEvent is GEventArgs10 or GEventArgs9)
                return true;
        }
        return false;
    }

    public static bool HasAnyHandsActionNonLinq(this TraderControllerClass controller)
    {
        foreach (var eventArg in controller.List_0)
        {
            if (eventArg is GInterface418)
                return true;
        }
        return false;
    }
}
