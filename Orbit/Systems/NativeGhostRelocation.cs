using Comfort.Common;
using EFT;
using EFT.Interactive;
using Orbit.Navigation;
using UnityEngine;

namespace Orbit.Systems;

internal static class NativeGhostRelocation
{
    internal static bool IsSafe(BotOwner bot, Vector3 from, Vector3 to, DoorSystem doors)
        => IsSafe(bot, from, to, doors, out _);

    internal static bool IsSafe(BotOwner bot, Vector3 from, Vector3 to, DoorSystem doors, out string reason)
    {
        reason = null;
        var world = Singleton<GameWorld>.Instance;
        if (world == null) { reason = "no-world"; return false; }
        var mask = LayersMaskController.HighPolyWithTerrainMask;
        var direction = to - from;
        var length = direction.magnitude;
        // Do not cross solid walls or locked rooms to compensate for missing navigation.
        if (Physics.CheckCapsule(to + Vector3.up * 0.4f, to + Vector3.up * 1.4f, 0.25f, mask, QueryTriggerInteraction.Ignore))
        { reason = "occupied-volume"; return false; }
        if (Physics.SphereCast(from + Vector3.up * 0.8f, 0.25f, direction.normalized, out _, length, mask, QueryTriggerInteraction.Ignore))
        { reason = "solid-wall"; return false; }
        for (var d = 0f; d <= length; d += 0.5f)
            if (DangerZones.IsInside(from + direction.normalized * d)) { reason = "danger-volume"; return false; }
        var ray = new Ray(from + Vector3.up * 0.8f, direction.normalized);
        foreach (var door in doors.Doors)
        {
            if (door == null || door.DoorState == EDoorState.Open || door.Collider == null) continue;
            var bounds = door.Collider.bounds;
            bounds.Expand(0.5f);
            if (bounds.Contains(ray.origin) || bounds.IntersectRay(ray, out var distance) && distance <= length) { reason = "closed-door"; return false; }
        }
        foreach (var player in world.AllAlivePlayersList)
        {
            if (player == null || player == bot.GetPlayer || player.HealthController is not { IsAlive: true }) continue;
            var distance = Mathf.Min((player.Position - from).sqrMagnitude, (player.Position - to).sqrMagnitude);
            if (player.IsAI)
            {
                // An awake encounter always blocks recovery. Overlapping sleeping allies may
                // separate a little, otherwise each one vetoes every fine edge landing of the other.
                if (distance < 900f && player.gameObject.activeSelf
                    || (player.Position - to).sqrMagnitude < 9f && !SeparatesSleepingAlly(bot, player, from, to))
                { reason = "nearby-bot"; return false; }
                continue;
            }
            if (distance < 10000f) { reason = "nearby-human"; return false; }
            var head = player.PlayerBones?.Head?.Original;
            if (head == null) { reason = "no-human-head"; return false; }
            if (Visible(head.position, from, mask) || Visible(head.position, to, mask)) { reason = "visible-human"; return false; }
        }
        return true;
    }

    private static bool SeparatesSleepingAlly(BotOwner bot, Player other, Vector3 from, Vector3 to)
    {
        if ((to - from).sqrMagnitude > 1.5f * 1.5f) return false;
        var offset = from - other.Position;
        var landing = to - other.Position;
        // Horizontal clearance must improve without passing through the other body. Moving up
        // or down alone cannot make an overlapping landing safe.
        if (Mathf.Abs(offset.y) > 0.75f) return false;
        offset.y = landing.y = 0f;
        var clearance = offset.magnitude + 0.15f;
        if (offset.sqrMagnitude > 0.5f * 0.5f
            || landing.sqrMagnitude < clearance * clearance
            || Vector3.Dot(offset, landing - offset) < 0f) return false;
        var mate = other.AIData?.BotOwner;
        var group = bot.BotsGroup;
        if (mate == null || group == null || !ReferenceEquals(group, mate.BotsGroup)
            || !NativeGhostSystem.OwnsInactiveMovement(bot) || !NativeGhostSystem.OwnsInactiveMovement(mate)
            || NativeGhostSystem.MovementPinned(bot) || NativeGhostSystem.MovementPinned(mate)) return false;
        // Use the game's IPlayer bridges, as the hostility checks do, without adding voice-chat dependencies.
        var world = Singleton<GameWorld>.Instance;
        var otherPlayer = world.GetAlivePlayerBridgeByProfileID(other.ProfileId)?.iPlayer;
        var ownPlayer = world.GetAlivePlayerBridgeByProfileID(bot.GetPlayer.ProfileId)?.iPlayer;
        return otherPlayer != null && ownPlayer != null && !group.IsEnemy(otherPlayer) && !group.IsEnemy(ownPlayer);
    }

    private static bool Visible(Vector3 head, Vector3 foot, int mask)
        => !Physics.Linecast(head, foot + Vector3.up * 0.3f, mask)
            || !Physics.Linecast(head, foot + Vector3.up, mask)
            || !Physics.Linecast(head, foot + Vector3.up * 1.7f, mask);
}
