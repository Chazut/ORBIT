using Comfort.Common;
using EFT;
using EFT.Interactive;
using Orbit.Navigation;
using UnityEngine;

namespace Orbit.Systems;

internal static class NativeGhostRelocation
{
    internal static bool IsSafe(BotOwner bot, Vector3 from, Vector3 to, DoorSystem doors)
    {
        var world = Singleton<GameWorld>.Instance;
        if (world == null) return false;
        var mask = LayersMaskController.HighPolyWithTerrainMask;
        var direction = to - from;
        var length = direction.magnitude;
        // Do not cross solid walls or locked rooms to compensate for missing navigation.
        if (Physics.CheckCapsule(to + Vector3.up * 0.4f, to + Vector3.up * 1.4f, 0.25f, mask, QueryTriggerInteraction.Ignore)
            || Physics.SphereCast(from + Vector3.up * 0.8f, 0.25f, direction.normalized, out _, length, mask, QueryTriggerInteraction.Ignore))
            return false;
        for (var d = 0f; d <= length; d += 0.5f)
            if (DangerZones.IsInside(from + direction.normalized * d)) return false;
        var ray = new Ray(from + Vector3.up * 0.8f, direction.normalized);
        foreach (var door in doors.Doors)
        {
            if (door == null || door.DoorState == EDoorState.Open || door.Collider == null) continue;
            var bounds = door.Collider.bounds;
            bounds.Expand(0.5f);
            if (bounds.Contains(ray.origin) || bounds.IntersectRay(ray, out var distance) && distance <= length) return false;
        }
        foreach (var player in world.AllAlivePlayersList)
        {
            if (player == null || player == bot.GetPlayer || player.HealthController is not { IsAlive: true }) continue;
            var distance = Mathf.Min((player.Position - from).sqrMagnitude, (player.Position - to).sqrMagnitude);
            if (player.IsAI)
            {
                // Avoid landing among bots, or relocating within an awake encounter.
                if ((player.Position - to).sqrMagnitude < 9f || distance < 900f && player.gameObject.activeSelf) return false;
                continue;
            }
            if (distance < 10000f) return false;
            var head = player.PlayerBones?.Head?.Original;
            if (head == null) return false;
            if (Visible(head.position, from, mask) || Visible(head.position, to, mask)) return false;
        }
        return true;
    }

    private static bool Visible(Vector3 head, Vector3 foot, int mask)
        => !Physics.Linecast(head, foot + Vector3.up * 0.3f, mask)
            || !Physics.Linecast(head, foot + Vector3.up, mask)
            || !Physics.Linecast(head, foot + Vector3.up * 1.7f, mask);
}
