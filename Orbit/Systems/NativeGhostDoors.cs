using System;
using System.Collections.Generic;
using EFT;
using EFT.Interactive;
using Orbit.Helpers;
using UnityEngine;

namespace Orbit.Systems;

// The door owns its animation. Never start a body interaction or replace the native goal.
internal sealed class NativeGhostDoors(BotOwner bot, DoorSystem doors)
{
    private static readonly Dictionary<Door, float> Started = new();
    private Door _pending;
    private readonly NearbyDoorCache _nearby = new();
    private float _deadline, _nextScan;
    private Vector3 _scanDirection;
    internal bool Pending => _pending != null;
    internal static void Clear() => Started.Clear();

    internal bool Check(out string failure, Door requested = null)
    {
        failure = null;
        if (_pending != null) return Wait(out failure);
        var door = requested ?? FindOnRoute();
        if (door == null) return false;
        if (IsOpen(door)) { Started.Remove(door); return false; }
        var opener = bot.DoorOpener;
        if (door.GetType() != typeof(Door) || !door.enabled || !door.Operatable
            || door.DoorState is not (EDoorState.Shut or EDoorState.Interacting or EDoorState.Open)
            || (opener?._currentDoorLink?.Door == door && opener._doBreach))
        {
            failure = $"door requires body: id={door.Id} state={door.DoorState}";
            return true;
        }
        // Requests from a node can refer to a distant or stale link. Let its route approach it.
        if ((door.transform.position - bot.Position).sqrMagnitude > 16f) return false;
        _pending = door;
        if (Started.TryGetValue(door, out var previous) && Time.time >= previous + 13f) Started.Remove(door);
        if (Started.TryGetValue(door, out var started)) _deadline = started + 13f;
        else
        {
            _deadline = Time.time + 13f;
            if (door.DoorState == EDoorState.Shut && door.InteractingPlayer == null)
            {
                Started[door] = Time.time;
                try
                {
                    // Fika's override sends WorldInteraction immediately, even with this body inactive.
                    if (FikaDetection.FikaLoaded)
                        bot.GetPlayer.ExecuteInteraction(door, new InteractionResult(EInteractionType.Open));
                    else door.Open();
                    Log.Info($"NATIVE GHOST: {bot.Profile.Nickname} opening door in Ghost: id={door.Id} native goal retained");
                }
                catch (Exception e)
                {
                    failure = $"door open failed: id={door.Id} {e.GetType().Name}: {e.Message}";
                    return true;
                }
            }
        }
        return Wait(out failure);
    }

    private bool Wait(out string failure)
    {
        failure = null;
        var door = _pending;
        if (door == null) { _pending = null; return false; }
        // State alone is insufficient: also wait for the actual leaf to reach its open angle.
        if (IsOpen(door))
        {
            Started.Remove(door);
            _pending = null;
            Log.Info($"NATIVE GHOST: {bot.Profile.Nickname} door ready in Ghost: id={door.Id} resuming native route");
            return false;
        }
        if (door.DoorState is EDoorState.Locked or EDoorState.Breaching || Time.time >= _deadline)
            failure = $"door wait failed: id={door.Id} state={door.DoorState} angle={door.CurrentAngle:F1}";
        return true;
    }

    private static bool IsOpen(Door door)
        => door.DoorState == EDoorState.Open
            && Mathf.Abs(Mathf.DeltaAngle(door.CurrentAngle, door.GetAngle(EDoorState.Open))) <= 1f;

    private Door FindOnRoute()
    {
        var path = bot.Mover?.ActualPathController;
        if (path?.HavePath != true) return null;
        var from = bot.Position;
        var direction = path.CurrentCorner() - from;
        var distance = Mathf.Min(direction.magnitude, 1.5f);
        if (distance < 0.01f) return null;
        if (Time.time < _nextScan && Vector3.Dot(direction.normalized, _scanDirection) > 0.95f) return null;
        _nextScan = Time.time + (distance >= 1.5f ? 0.2f : 0f);
        _scanDirection = direction.normalized;
        var ray = new Ray(from + Vector3.up * 0.6f, direction.normalized);
        foreach (var door in _nearby.Get(doors, from))
        {
            if (door == null || IsOpen(door) || door.Collider == null
                || (door.transform.position - from).sqrMagnitude > 16f) continue;
            var bounds = door.Collider.bounds;
            bounds.Expand(0.35f);
            if (bounds.Contains(ray.origin) || bounds.IntersectRay(ray, out var hit) && hit <= distance) return door;
        }
        return null;
    }
}
