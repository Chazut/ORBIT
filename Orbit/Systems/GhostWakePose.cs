using EFT;
using UnityEngine;

namespace Orbit.Systems;

// Capture before enabling the body: native activation can replace the Ghost transform
// before the landing guard gets to read it. Keep diagnostics at the actual offending stage.
internal readonly struct GhostWakePose
{
    private readonly Player _player;
    private readonly Vector3 _position;
    private readonly Quaternion _rotation;

    internal GhostWakePose(Player player)
    {
        _player = player;
        _position = player.Position;
        _rotation = player.Transform.rotation;
    }

    internal void RestoreIfMoved(string stage)
    {
        var current = _player.Position;
        if (!BotGroundPlacement.Finite(_position)) return;
        var distance = Vector3.Distance(current, _position);
        if (BotGroundPlacement.Finite(current) && distance <= .25f) return;
        _player.Teleport(_position);
        _player.Transform.rotation = _rotation;
        Log.Info($"GHOST WAKE POSITION: {_player.Profile.Nickname} stage={stage} before={_position} after={current} restored={_player.Position} displacement={distance:F2}m");
    }
}
