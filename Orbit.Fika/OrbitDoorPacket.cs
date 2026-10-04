using Fika.Core.Networking.LiteNetLib.Utils;

namespace Orbit.Fika;

internal enum DoorMessage : byte { Hello, Welcome, State, Barrier, Fence, Acknowledge, SwitchInteraction }

// Separate from the audio protocol. State is sent only to peers that negotiated this version.
internal struct OrbitDoorPacket : INetSerializable
{
    internal const byte CurrentProtocol = 1;
    public byte Protocol;
    public DoorMessage Kind;
    public string Session;
    public string DoorId;
    public ulong Revision;
    public ulong Token;
    public byte State;
    public float Angle;
    public bool Broken;

    public void Serialize(NetDataWriter writer)
    {
        writer.Put(Protocol);
        writer.Put((byte)Kind);
        writer.Put(Session ?? string.Empty);
        writer.Put(DoorId ?? string.Empty);
        writer.Put(Revision);
        writer.Put(Token);
        writer.Put(State);
        writer.Put(Angle);
        writer.Put(Broken);
    }

    public void Deserialize(NetDataReader reader)
    {
        Protocol = reader.GetByte();
        Kind = (DoorMessage)reader.GetByte();
        Session = reader.GetString(64);
        DoorId = reader.GetString(256);
        Revision = reader.GetULong();
        Token = reader.GetULong();
        State = reader.GetByte();
        Angle = reader.GetFloat();
        Broken = reader.GetBool();
    }
}
