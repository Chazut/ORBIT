using System.Collections.Generic;
using Fika.Core.Networking.LiteNetLib.Utils;
using UnityEngine;

namespace Orbit.Fika;

/// <summary>One firing member of a simulated fight, as shipped over the wire.</summary>
public struct OrbitGhostShooter
{
    public string ProfileId;
    public Vector3 Position;
    public int Shots;
    public List<float> ShotTimes;
}

/// <summary>
/// Host to clients, one per resolved simulated ghost fight: replay its gunfire burst locally.
/// Profile ids identify the shooters so each client pulls authentic weapon sound banks from its own
/// observed players instead of shipping audio over the wire. The schedule extension requires an
/// updated bridge on receiving clients. This reader also accepts older hosts without that extension.
/// </summary>
public struct OrbitGhostFightPacket : INetSerializable
{
    public Vector3 PosA;
    public Vector3 PosB;
    public string ProfileA;
    public string ProfileB;
    public int Shots;
    public float Duration;
    public List<OrbitGhostShooter> Shooters;
    public string FightId;
    private const int ScheduleMarker = 0x47415331;

    public readonly void Serialize(NetDataWriter writer)
    {
        writer.PutUnmanaged(PosA);
        writer.PutUnmanaged(PosB);
        writer.Put(ProfileA ?? string.Empty);
        writer.Put(ProfileB ?? string.Empty);
        writer.PutUnmanaged(Shots);
        writer.PutUnmanaged(Duration);
        var count = Shooters?.Count ?? 0;
        writer.PutUnmanaged(count);
        for (var i = 0; i < count; i++)
        {
            writer.Put(Shooters[i].ProfileId ?? string.Empty);
            writer.PutUnmanaged(Shooters[i].Position);
            writer.PutUnmanaged(Shooters[i].Shots);
        }
        // Updated readers accept legacy packets. Sending this extension requires updated receivers.
        writer.PutUnmanaged(ScheduleMarker);
        writer.Put(FightId ?? string.Empty);
        for (var i = 0; i < count; i++)
        {
            var times = Shooters[i].ShotTimes;
            writer.PutUnmanaged(times?.Count ?? -1);
            if (times != null)
                for (var j = 0; j < times.Count; j++) writer.PutUnmanaged(times[j]);
        }
    }

    public void Deserialize(NetDataReader reader)
    {
        PosA = reader.GetUnmanaged<Vector3>();
        PosB = reader.GetUnmanaged<Vector3>();
        ProfileA = reader.GetString();
        ProfileB = reader.GetString();
        Shots = reader.GetUnmanaged<int>();
        Duration = reader.GetUnmanaged<float>();
        Shooters = new List<OrbitGhostShooter>();
        FightId = null;
        if (reader.AvailableBytes < sizeof(int)) return; // pre-2.1 host
        var count = reader.GetUnmanaged<int>();
        if (count < 0 || count > 128) throw new System.FormatException("Invalid Ghost shooter count");
        for (var i = 0; i < count; i++)
        {
            Shooters.Add(new OrbitGhostShooter
            {
                ProfileId = reader.GetString(),
                Position = reader.GetUnmanaged<Vector3>(),
                Shots = reader.GetUnmanaged<int>(),
            });
        }
        if (reader.AvailableBytes < sizeof(int)) return;
        var extensionStart = reader.Position;
        if (reader.GetUnmanaged<int>() != ScheduleMarker)
        {
            reader.SetPosition(extensionStart);
            return;
        }
        FightId = reader.GetString();
        if (float.IsNaN(Duration) || float.IsInfinity(Duration) || Duration < 0f)
            throw new System.FormatException("Invalid Ghost fight duration");
        for (var i = 0; i < count; i++)
        {
            var length = reader.GetUnmanaged<int>();
            if (length == -1) continue; // Legacy schedule fallback.
            if (length < 0 || length > 90 || length > Shooters[i].Shots || reader.AvailableBytes < length * sizeof(float))
                throw new System.FormatException("Invalid Ghost shot schedule");
            var times = new List<float>(length);
            var previous = -1f;
            for (var j = 0; j < length; j++)
            {
                var time = reader.GetUnmanaged<float>();
                if (float.IsNaN(time) || float.IsInfinity(time) || time < 0f || time <= previous || time >= Duration)
                    throw new System.FormatException("Invalid Ghost shot time");
                times.Add(time);
                previous = time;
            }
            var shooter = Shooters[i];
            shooter.ShotTimes = times;
            Shooters[i] = shooter;
        }
    }
}
