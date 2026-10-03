using EFT;
using Orbit.Api;
using UnityEngine;

namespace Orbit.Systems;

public partial class DormancySystem
{
    private static void RecordWakeEvent(Player player, GhostWakeReason reason)
    {
        if (player?.HealthController is not { IsAlive: true }) return;
        var position = player.Position;
        OrbitTelemetry.PushGhostWake(new OrbitGhostWake
        {
            RecordedAt = Time.realtimeSinceStartup, ProfileId = player.ProfileId, Name = player.Profile?.Nickname,
            Cause = reason.Cause.ToString(), Detail = reason.Message,
            TriggerProfileId = reason.TriggerProfileId, TriggerName = reason.TriggerName,
            MemberProfileId = reason.MemberProfileId, Distance = reason.Distance,
            X = position.x, Y = position.y, Z = position.z,
        });
    }

    private static GhostWakeReason NeighbourWake(Player member, Player neighbour)
        => new(GhostWakeCause.BotProximity, $"awake bot {neighbour.Profile?.Nickname} near {member.Profile?.Nickname}",
            neighbour, member.ProfileId, Vector3.Distance(member.Position, neighbour.Position));
}
