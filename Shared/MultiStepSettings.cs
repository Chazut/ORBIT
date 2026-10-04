#nullable disable
using System;
using System.Collections.Generic;

namespace Orbit.Settings;

public sealed class MultiStepSettings
{
    public bool Enabled { get; set; } = true;
    public bool Pmc { get; set; } = true;
    public bool PlayerScav { get; set; } = true;
    public float StepTimeout { get; set; } = 240;
    public float LootDuration { get; set; } = 90;
    public float RegroupTimeout { get; set; } = 45;
    public MultiStepStyle Timmy { get; set; } = new(.15f, .25f, .8f);
    public MultiStepStyle Cautious { get; set; } = new(.40f, .5f, 1f);
    public MultiStepStyle Average { get; set; } = new(.35f, .5f, .65f);
    public MultiStepStyle Aggressive { get; set; } = new(.5f, .5f, .25f);
    public MultiStepStyle VeryAggressive { get; set; } = new(.6f, .4f, .1f);
    public MultiStepStyle PlayerScavStyle { get; set; } = new(.25f, 0f, .75f);
    // Weight zero disables an operation. Missing keys in older presets keep the default weight.
    public Dictionary<string, float> Weights { get; set; } = new()
    {
        ["kiba"] = 1, ["ultra"] = 1, ["object21ws"] = 1, ["object14"] = 1,
        ["saferoom-loot"] = 1, ["saferoom-extract"] = 1,
        ["d2"] = 1, ["hermetic"] = 1, ["zb013"] = 1
    };
    public float Weight(string id) => Weights.TryGetValue(id, out var value) ? value : 1;
    public MultiStepStyle Style(string archetype, bool scav = false) => scav ? PlayerScavStyle : archetype switch
    {
        "Timmy" => Timmy, "Cautious" => Cautious, "Aggressive" => Aggressive,
        "VeryAggressive" => VeryAggressive, _ => Average
    };
    public bool Allows(string category) => Enabled && (category == "PMC" && Pmc || category == "PlayerScav" && PlayerScav);
    public void Validate()
    {
        MapBehaviorOverride.Range(StepTimeout, 30, 900, "Multi-step travel timeout");
        MapBehaviorOverride.Range(LootDuration, 10, 600, "Multi-step loot duration");
        MapBehaviorOverride.Range(RegroupTimeout, 10, 180, "Multi-step regroup timeout");
        if (Weights == null) throw new ArgumentException("Multi-step weights must be an object.");
        foreach (var value in Weights.Values) MapBehaviorOverride.Range(value, 0, 100, "Multi-step weight");
        foreach (var style in new[] { Timmy, Cautious, Average, Aggressive, VeryAggressive, PlayerScavStyle })
            (style ?? throw new ArgumentException("Multi-step personalities must be objects.")).Validate();
    }
}

public sealed class MultiStepStyle
{
    public float LootMainChance { get; set; } = .35f;
    public float ExtractionChance { get; set; } = .5f;
    public float DisableAlarmChance { get; set; } = .65f;
    public MultiStepStyle() { }
    public MultiStepStyle(float loot, float extract, float alarm)
    { LootMainChance = loot; ExtractionChance = extract; DisableAlarmChance = alarm; }
    public void Validate()
    {
        MapBehaviorOverride.Range(LootMainChance, 0, 1, "Multi-step loot main chance");
        MapBehaviorOverride.Range(ExtractionChance, 0, 1, "Multi-step extraction chance");
        MapBehaviorOverride.Range(DisableAlarmChance, 0, 1, "Multi-step alarm chance");
    }
}
