using System;
using Orbit.Tasks;

namespace Orbit.Entities;

/// <summary>
/// Base class for everything the task system schedules. Each entity owns a per-task <see cref="TaskScores"/>
/// array (one float per registered task) and an <see cref="TaskAssignment"/> describing what it's currently
/// running. Equality is purely id-based — two entities are the same iff their <see cref="Id"/>s match.
/// </summary>
public class Entity(int id, float[] taskScores) : IEquatable<Entity>
{
    public readonly int Id = id;

    public readonly float[] TaskScores = taskScores;
    public TaskAssignment TaskAssignment;

    public bool Equals(Entity other)
    {
        if (ReferenceEquals(other, null))
            return false;
        return Id == other.Id;
    }

    public override bool Equals(object obj)
    {
        if (obj is null) return false;
        if (ReferenceEquals(this, obj)) return true;
        // (Entity), NOT (Agent): this base-class override runs for every subtype, and the old hard cast
        // to Agent threw InvalidCastException the first time two Squads landed in the same hash bucket
        // (squad ids are recycled, so same-id collisions are routine).
        return obj.GetType() == GetType() && Equals((Entity)obj);
    }

    public override int GetHashCode() => Id;

    public static bool operator ==(Entity lhs, Entity rhs)
    {
        if (lhs is null) return rhs is null;
        return lhs.Equals(rhs);
    }

    public static bool operator !=(Entity lhs, Entity rhs) => !(lhs == rhs);
}
