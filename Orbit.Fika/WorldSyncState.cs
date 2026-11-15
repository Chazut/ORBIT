using System;
using System.Collections.Generic;
using EFT.Interactive;

namespace Orbit.Fika;

/// <summary>Session-local ordering and validation. Exit revisions and bot identities are independent.</summary>
internal sealed class WorldSyncState
{
    internal readonly Dictionary<string, OrbitWorldPacket> Exits = new();
    private readonly Dictionary<string, OrbitWorldPacket> _poses = new();
    private readonly Dictionary<string, ulong> _poseRevisions = new();

    internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    internal static bool Valid(OrbitWorldPacket packet)
    {
        if (packet.Protocol != OrbitWorldPacket.Version || packet.Session?.Length != 32) return false;
        if (packet.Kind == WorldMessage.Request) return true;
        if (packet.Revision == 0 || string.IsNullOrEmpty(packet.Id) || packet.Id.Length > 256) return false;
        if (packet.Kind == WorldMessage.Exit)
            return Enum.IsDefined(typeof(EExfiltrationStatus), (EExfiltrationStatus)packet.Status)
                && Finite(packet.Elapsed) && packet.Elapsed >= 0 && packet.Elapsed <= 86400;
        if (packet.Kind != WorldMessage.DeathPose) return false;
        var p = packet.Position;
        var q = packet.Rotation;
        var norm = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
        return Finite(p.x) && Finite(p.y) && Finite(p.z)
            && Math.Abs(p.x) < 100000 && Math.Abs(p.y) < 100000 && Math.Abs(p.z) < 100000
            && Finite(norm) && norm > .9f && norm < 1.1f;
    }

    internal bool Receive(OrbitWorldPacket packet)
    {
        if (!Valid(packet)) return false;
        if (packet.Kind == WorldMessage.Exit)
        {
            if (Exits.TryGetValue(packet.Id, out var previous)
                && (packet.Revision <= previous.Revision
                    || previous.Status == (byte)EExfiltrationStatus.NotPresent)) return false;
            Exits[packet.Id] = packet;
            return true;
        }
        if (packet.Kind != WorldMessage.DeathPose) return false;
        if (_poseRevisions.TryGetValue(packet.Id, out var revision) && packet.Revision <= revision) return false;
        _poseRevisions[packet.Id] = packet.Revision;
        _poses[packet.Id] = packet;
        return true;
    }

    internal bool TakePose(string profile, int netId, out OrbitWorldPacket pose)
    {
        if (_poses.TryGetValue(profile, out pose) && pose.NetId == netId)
        {
            _poses.Remove(profile);
            return true;
        }
        pose = default;
        return false;
    }

    internal void Clear()
    {
        Exits.Clear();
        _poses.Clear();
        _poseRevisions.Clear();
    }
}
