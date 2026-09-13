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
}

/// <summary>
/// Host to clients, one per resolved simulated ghost fight: replay its gunfire burst locally.
/// Profile ids identify the shooters so each client pulls authentic weapon sound banks from its own
/// observed players instead of shipping audio over the wire. The per-shooter list (2.1+) trails the
/// original fields, so an older client still reads a valid packet and an older host still produces
/// one this client can read.
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
        if (reader.AvailableBytes < sizeof(int)) return; // pre-2.1 host
        var count = reader.GetUnmanaged<int>();
        for (var i = 0; i < count; i++)
        {
            Shooters.Add(new OrbitGhostShooter
            {
                ProfileId = reader.GetString(),
                Position = reader.GetUnmanaged<Vector3>(),
                Shots = reader.GetUnmanaged<int>(),
            });
        }
    }
}
