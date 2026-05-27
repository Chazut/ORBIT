using System;
using System.Collections.Generic;
using EFT.Interactive;
using UnityEngine;

namespace Orbit.Navigation;

public enum WaypointCategory
{
    ContainerLoot,
    LooseLoot,
    Corpse,
    Quest,
    Synthetic,
    Exfil
}

/// <summary>
/// A point of interest in the world — a container, a corpse, a quest
/// trigger, an exfil, etc. The dispatch strategy treats waypoints as the
/// atomic unit of "place a squad can be sent to". Equality is id-based.
/// </summary>
public class Waypoint(
    int id,
    WaypointCategory category,
    string name,
    Vector3 position,
    float radiusSqr,
    List<Door> doors,
    List<CoverPoint> coverPoints,
    MonoBehaviour target
) : IEquatable<Waypoint>
{
    private readonly int _id = id;
    public int Id => _id;
    /// <summary>Identifier-like string, e.g. for Quest the TriggerWithId
    /// gameObject name (which doubles as the quest zone ID).</summary>
    public readonly string Name = name;
    public readonly Vector3 Position = position;
    public readonly float RadiusSqr = radiusSqr;
    public readonly WaypointCategory Category = category;
    public readonly List<Door> Doors = doors;
    public readonly List<CoverPoint> CoverPoints = coverPoints;
    /// <summary>
    /// The EFT interactable this POI refers to, when applicable. For
    /// lootable categories the target is an InteractableObject
    /// (LootableContainer / LootItem / Corpse). For Exfil it's an
    /// ExfiltrationPoint (a MonoBehaviour, not an InteractableObject —
    /// that's why this field is typed at the common base). For Quest /
    /// Synthetic it's null.
    /// </summary>
    public readonly MonoBehaviour Target = target;

    /// <summary>
    /// Locked doors detected on the natural navmesh route to this POI.
    /// Populated lazily the first time a PMC squad tries to dispatch and
    /// finds <c>NavMesh.CalculatePath</c> returning anything other than
    /// PathComplete. When non-null and non-empty, the POI is treated as
    /// reachable *if* the squad force-unlocks the door(s) on arrival.
    /// <see langword="null"/> means "never checked" or "no locked door
    /// nearby"; the distinction is purely diagnostic.
    /// </summary>
    public List<Door> LockedDoorsOnPath;

    public bool Equals(Waypoint other)
    {
        if (other is null) return false;
        return _id == other._id;
    }

    public override bool Equals(object obj)
    {
        if (obj is null) return false;
        if (ReferenceEquals(this, obj)) return true;
        return obj.GetType() == GetType() && Equals((Waypoint)obj);
    }

    public override int GetHashCode() => _id;

    public static bool operator ==(Waypoint lhs, Waypoint rhs)
    {
        if (lhs is null) return rhs is null;
        return lhs.Equals(rhs);
    }

    public static bool operator !=(Waypoint lhs, Waypoint rhs) => !(lhs == rhs);

    public override string ToString() => $"Waypoint({_id}, {Category}, {Name})";
}
