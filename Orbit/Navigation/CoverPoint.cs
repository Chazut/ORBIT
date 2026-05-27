using System;
using UnityEngine;

namespace Orbit.Navigation;

public enum CoverCategory
{
    Hard,
    Soft,
    None
}

/// <summary>
/// A precomputed cover hint attached to a waypoint — used by Guard to
/// pick a stance when the bot reaches the POI. Two constructors: one for
/// internal <see cref="CoverCategory"/> values, one that maps BSG's
/// <c>CoverType</c> (Wall / Foliage / Other) into the simpler 3-state
/// category we care about.
/// </summary>
public readonly struct CoverPoint : IEquatable<CoverPoint>
{
    public readonly Vector3 Position;
    public readonly Vector3 Direction;
    public readonly CoverCategory Category;
    public readonly CoverLevel Level;

    public CoverPoint(Vector3 position, Vector3 direction, CoverCategory category, CoverLevel level)
    {
        Position = position;
        Direction = direction;
        Category = category;
        Level = level;
    }

    public CoverPoint(Vector3 position, Vector3 direction, CoverType category, CoverLevel level)
    {
        Position = position;
        Direction = direction;
        Category = category switch
        {
            CoverType.Wall => CoverCategory.Hard,
            CoverType.Foliage => CoverCategory.Soft,
            _ => CoverCategory.None
        };
        Level = level;
    }

    public bool Equals(CoverPoint other) => Position.Equals(other.Position);

    public override bool Equals(object obj) => obj is CoverPoint other && Equals(other);

    public override int GetHashCode() => Position.GetHashCode();

    public override string ToString() => $"{nameof(CoverPoint)}(category: {Category}, level: {Level})";
}
