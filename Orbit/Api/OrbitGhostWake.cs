namespace Orbit.Api;

/// <summary>One actual Ghost-to-awake transition. A squad shares the member and trigger that caused it.</summary>
public sealed class OrbitGhostWake
{
    public float RecordedAt;
    public string ProfileId;
    public string Name;
    public string Cause;
    public string Detail;
    public string TriggerProfileId;
    public string TriggerName;
    public string MemberProfileId;
    public float Distance;
    public float X, Y, Z;
}
