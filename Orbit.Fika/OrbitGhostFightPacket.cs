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
        writer.Put(PosA.x);
        writer.Put(PosA.y);
        writer.Put(PosA.z);
        writer.Put(PosB.x);
        writer.Put(PosB.y);
        writer.Put(PosB.z);
        writer.Put(ProfileA ?? string.Empty);
        writer.Put(ProfileB ?? string.Empty);
        writer.Put(Shots);
        writer.Put(Duration);
    }

    public void Deserialize(NetDataReader reader)
    {
        PosA = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
        PosB = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
        ProfileA = reader.GetString();
        ProfileB = reader.GetString();
        Shots = reader.GetInt();
        Duration = reader.GetFloat();
    }
}
