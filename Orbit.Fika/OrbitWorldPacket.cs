using Fika.Core.Networking.LiteNetLib.Utils;
using UnityEngine;

namespace Orbit.Fika;

internal enum WorldMessage : byte { Request, Exit, DeathPose }

/// <summary>Only sent to peers advertising the world-state capability in the existing handshake.</summary>
internal struct OrbitWorldPacket : INetSerializable
{
    internal const byte Version = 1;
    public byte Protocol;
    public WorldMessage Kind;
    public string Session, Id;
    public ulong Revision;
    public byte Status;
    public float Elapsed;
    public int NetId;
    public Vector3 Position;
    public Quaternion Rotation;

    public void Serialize(NetDataWriter writer)
    {
        writer.Put(Protocol);
        writer.Put((byte)Kind);
        writer.Put(Session ?? string.Empty);
        writer.Put(Id ?? string.Empty);
        writer.Put(Revision);
        writer.Put(Status);
        writer.Put(Elapsed);
        writer.Put(NetId);
        writer.Put(Position.x); writer.Put(Position.y); writer.Put(Position.z);
        writer.Put(Rotation.x); writer.Put(Rotation.y); writer.Put(Rotation.z); writer.Put(Rotation.w);
    }

    public void Deserialize(NetDataReader reader)
    {
        Protocol = reader.GetByte();
        Kind = (WorldMessage)reader.GetByte();
        Session = reader.GetString(64);
        Id = reader.GetString(256);
        Revision = reader.GetULong();
        Status = reader.GetByte();
        Elapsed = reader.GetFloat();
        NetId = reader.GetInt();
        Position = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
        Rotation = new Quaternion(reader.GetFloat(), reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
    }
}
