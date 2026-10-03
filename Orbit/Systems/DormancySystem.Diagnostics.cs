using EFT;
using Orbit.Api;
using Orbit.Helpers;
using UnityEngine;

namespace Orbit.Systems;

public partial class DormancySystem
{
    private static void RecordWakeEvent(Player player, GhostWakeReason reason)
    {
        if (!DiagnosticCapture.IsRecording || player?.HealthController is not { IsAlive: true }) return;
        var position = player.Position;
        DiagnosticCapture.Wake(new OrbitGhostWake
        {
            RecordedAt = Time.realtimeSinceStartup, ProfileId = player.ProfileId, Name = player.Profile?.Nickname,
            Cause = reason.Cause.ToString(), Detail = reason.Message, Distance = -1f,
            X = position.x, Y = position.y, Z = position.z,
        });
    }
}
