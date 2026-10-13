#nullable disable
using System.IO;

namespace Orbit.Settings;

public enum SquadLootWaitMode { Follow, EscortCover, HoldCover }

/// <summary>One group decision per loot stop; individual cover positions remain separate.</summary>
public sealed class SquadLootWaitStyle
{
    public float CoverChance { get; set; } = .7f;
    public float StayBehindChance { get; set; } = .4f;

    public static SquadLootWaitStyle For(string archetype) => archetype switch
    {
        "Timmy" => new() { CoverChance = .7f, StayBehindChance = .45f },
        "Cautious" => new() { CoverChance = .9f, StayBehindChance = .7f },
        "Aggressive" => new() { CoverChance = .65f, StayBehindChance = .2f },
        "VeryAggressive" => new() { CoverChance = .45f, StayBehindChance = .1f },
        _ => new(),
    };

    public SquadLootWaitMode Choose(float coverRoll, float stayRoll)
        => CoverChance <= 0f || (CoverChance < 1f && coverRoll >= CoverChance) ? SquadLootWaitMode.Follow
            : StayBehindChance >= 1f || stayRoll < StayBehindChance ? SquadLootWaitMode.HoldCover : SquadLootWaitMode.EscortCover;

    public void Validate()
    {
        foreach (var chance in new[] { CoverChance, StayBehindChance })
            if (float.IsNaN(chance) || float.IsInfinity(chance) || chance < 0f || chance > 1f)
                throw new InvalidDataException("Squad loot waiting probabilities must be between 0 and 1.");
    }
}
