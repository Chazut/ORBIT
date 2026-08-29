using Fika.Core.Networking.LiteNetLib.Utils;
using UnityEngine;

namespace Orbit.Fika;

/// <summary>
/// Host to clients, one per resolved simulated ghost fight: replay its gunfire burst locally.
/// Profile ids identify one member per side so each client pulls authentic weapon sound banks
/// from its own observed players instead of shipping audio over the wire.
/// </summary>
public struct OrbitGhostFightPacket : INetSerializable
{
    public Vector3 PosA;
    public Vector3 PosB;
    public string ProfileA;
    public string ProfileB;
    public int Shots;
    public float Duration;

    public readonly void Serialize(NetDataWriter writer)
    {
        writer.PutUnmanaged(PosA);
        writer.PutUnmanaged(PosB);
        writer.Put(ProfileA ?? string.Empty);
        writer.Put(ProfileB ?? string.Empty);
        writer.PutUnmanaged(Shots);
        writer.PutUnmanaged(Duration);
    }

    public void Deserialize(NetDataReader reader)
    {
        PosA = reader.GetUnmanaged<Vector3>();
        PosB = reader.GetUnmanaged<Vector3>();
        ProfileA = reader.GetString();
        ProfileB = reader.GetString();
        Shots = reader.GetUnmanaged<int>();
        Duration = reader.GetUnmanaged<float>();
    }
}
