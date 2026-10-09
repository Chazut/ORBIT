using Fika.Core.Networking.LiteNetLib.Utils;

namespace Orbit.Fika;

internal struct OrbitScopePacket : INetSerializable
{
    internal const byte CurrentProtocol = 1;
    public byte Protocol;
    public string Session, ProfileId, SightId;
    public ulong Sequence;
    public bool Active;
    public float Zoom, FieldOfView;

    public void Serialize(NetDataWriter writer)
    {
        writer.Put(Protocol);
        writer.Put(Session ?? string.Empty);
        writer.Put(ProfileId ?? string.Empty);
        writer.Put(SightId ?? string.Empty);
        writer.Put(Sequence);
        writer.Put(Active);
        writer.Put(Zoom);
        writer.Put(FieldOfView);
    }

    public void Deserialize(NetDataReader reader)
    {
        Protocol = reader.GetByte();
        Session = reader.GetString(64);
        ProfileId = reader.GetString(64);
        SightId = reader.GetString(64);
        Sequence = reader.GetULong();
        Active = reader.GetBool();
        Zoom = reader.GetFloat();
        FieldOfView = reader.GetFloat();
    }
}
