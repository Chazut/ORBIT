using System.Text.Json;
using EFT;
using EFT.Interactive;
using EFT.Quests;
using Orbit.Entities;
using Orbit.Navigation;
using Orbit.Systems;
using UnityEngine;

var checks = 0;
void Check(bool ok, string description)
{
    if (!ok) throw new InvalidOperationException(description);
    checks++;
}

Quest TaskWith(params ConditionCounterCreator[] counters)
{
    var quest = new Quest();
    quest.Template.Conditions[EQuestStatus.AvailableForFinish] = counters;
    foreach (var counter in counters) quest.ProgressCheckers[counter] = false;
    return quest;
}

TriggerWithId Box(string id, float x, float y = 0)
    => new(id, new BoxCollider { bounds = new(new(x - 10, y - 1, -10), new(x + 10, y + 3, 10)) });

var system = new QuestScavAttraction("bigmap", 700);
system.Register(Box("Killoldoil", 0));
system.Register(Box("Killnewoil", 200));
var oldGas = new ConditionCounterCreator(new ConditionKills("Savage"), new ConditionInZone("Killoldoil"));
var newGas = new ConditionCounterCreator(new ConditionKills("Savage"), new ConditionInZone("Killnewoil"));
var quest = TaskWith(oldGas, newGas);
var player = new Player();
player.QuestController.Quests.Add(quest);
var world = new GameWorld { AllAlivePlayersList = [player] };
var scav = new Squad();
var oldPoint = new Waypoint(1, new(0, 0, 0));
var newPoint = new Waypoint(2, new(200, 0, 0));
system.Refresh(world);
Check(system.AppliesTo(scav), "ordinary scav patrol");
Check(system.Pull(new(25, 0, 0), out _).x < 0, "prefer the closest gas station");
Check(system.Pull(new(175, 0, 0), out _).x > 0, "approach the other station from the other side");
Check(system.Pull(new(-710, 0, 0), out _) == default, "no pull at the distance limit");
Check(!system.InRange(new(-710, 0, 0)) && system.InRange(new(-709, 0, 0)),
    "map-wide fallback only weights waypoints when the squad itself is in range");
Check(system.Pull(new(-20, 0, 0), out _).x > system.Pull(new(-600, 0, 0), out _).x, "distance falloff");
Check(system.PatrolWeight(oldPoint) > 1 && system.PatrolWeight(newPoint) > 1, "both unfinished zones attract");
scav.Leader.Bot.Profile.IsPlayerScav = true;
Check(!system.AppliesTo(scav), "player scav");
scav.Leader.Bot.Profile.IsPlayerScav = false;
scav.Leader.Bot.Profile.Info.Settings.Role = Role.pmc;
Check(!system.AppliesTo(scav), "PMC");
scav.Leader.Bot.Profile.Info.Settings.Role = Role.assaultGroup;
scav.ExtractRequested = true;
Check(!system.AppliesTo(scav), "extracting scav");
scav.ExtractRequested = false;

var friend = new Player();
friend.QuestController.Quests.Add(TaskWith(oldGas));
world.AllAlivePlayersList.Add(friend);
quest.ProgressCheckers[oldGas] = true;
system.Refresh(world);
Check(system.PatrolWeight(oldPoint) > 1, "another human still needs Old Gas");
friend.HealthController.IsAlive = false;
system.Refresh(world);
Check(system.PatrolWeight(oldPoint) == 1 && system.PatrolWeight(newPoint) > 1, "refresh cached weights per objective");
quest.CompletedConditions.Add(newGas.id);
system.Refresh(world);
Check(!system.AppliesTo(scav), "all kills completed even while a handover remains");

quest.CompletedConditions.Clear();
quest.Hidden.Add(newGas);
system.Refresh(world);
Check(!system.AppliesTo(scav), "hidden task stage");
quest.Hidden.Clear();
quest.ProgressCheckers.Remove(newGas);
system.Refresh(world);
Check(!system.AppliesTo(scav), "uninitialised tracker");
quest.ProgressCheckers[newGas] = false;
quest.QuestStatus = EQuestStatus.AvailableForFinish;
system.Refresh(world);
Check(!system.AppliesTo(scav), "finished quest");
quest.QuestStatus = EQuestStatus.Started;
player.Profile.Side = EPlayerSide.Savage;
system.Refresh(world);
Check(!system.AppliesTo(scav), "human scav");
player.Profile.Side = EPlayerSide.Bear;
player.IsAI = true;
system.Refresh(world);
Check(!system.AppliesTo(scav), "AI profile");
player.IsAI = false;

var reserve = new QuestScavAttraction("rezervbase", 700);
reserve.Register(Box("lijnik_storage_area_1", 0, -12));
foreach (var target in new[] { "AnyPmc", "Savage", "Any" })
{
    var radius = new ConditionInZone("lijnik_storage_area_1");
    var location = new ConditionLocation("RezervBase", "develop");
    var role = target == "AnyPmc" ? new ConditionKills(target) : new ConditionKills(target, "pmcBot");
    player.QuestController.Quests = [TaskWith(new(role, location, radius),
        new(new ConditionKills("Savage"), new ConditionLocation("bigmap"), radius),
        new(new ConditionKills("Savage"), location, new ConditionInZone("other-map")),
        new(new ConditionKills("Savage")), new(radius))];
    reserve.Refresh(world);
    Check(!reserve.AppliesTo(scav), "wrong victim, role, map, zone or objective type");
}
player.QuestController.Quests = [TaskWith(new ConditionCounterCreator(new ConditionKills("Savage"),
    new ConditionInZone("lijnik_storage_area_1"), new ConditionLocation("RezervBase", "develop")))];
reserve.Refresh(world);
Check(reserve.AppliesTo(scav), "Safe Corridor counts on Reserve");
reserve.Pull(new(0, 0, 0), out var above);
Check(above, "try local reachable waypoints when above a bunker");
Check(reserve.PatrolWeight(new(1, new(0, -12, 0))) > 1 && reserve.PatrolWeight(new(2, new(0, 0, 0))) == 1,
    "only the actual underground volume gets the weight");
reserve.Refresh(null);
Check(!reserve.AppliesTo(scav), "no player left");

// Optionally exercise the same selection against the installed SPT quest database.
if (args.Length == 1)
{
    using var db = JsonDocument.Parse(File.ReadAllText(args[0]));
    string[] Values(JsonElement element, string property) =>
        element.GetProperty(property).EnumerateArray().Select(x => x.GetString()).ToArray();
    foreach (var (id, map, zones) in new (string, string, string[])[]
    {
        ("675c1ec7a46173572a0bf20a", "bigmap", ["Killoldoil", "Killnewoil"]),
        ("6193850f60b34236ee0483de", "Lighthouse", ["qlight_br_secure_road"]),
        ("6089743983426423753cd58a", "RezervBase", ["lijnik_storage_area_1"]),
        ("608a768d82e40b3c727fd17d", "RezervBase", ["eger_barracks_area_1", "eger_barracks_area_2"])
    })
    {
        var counters = new List<ConditionCounterCreator>();
        foreach (var objective in db.RootElement.GetProperty(id).GetProperty("conditions")
                     .GetProperty("AvailableForFinish").EnumerateArray())
        {
            if (!objective.TryGetProperty("counter", out var counter)) continue;
            var conditions = new List<Condition>();
            foreach (var c in counter.GetProperty("conditions").EnumerateArray())
                conditions.Add(c.GetProperty("conditionType").GetString() switch
                {
                    "Kills" => new ConditionKills(c.GetProperty("target").GetString(), Values(c, "savageRole")),
                    "Location" => new ConditionLocation(Values(c, "target")),
                    "InZone" => new ConditionInZone(Values(c, "zoneIds")),
                    _ => new Condition()
                });
            counters.Add(new(conditions.ToArray()));
        }
        var derived = new QuestScavAttraction(map, 700);
        player.QuestController.Quests = [TaskWith(counters.ToArray())];
        for (int i = 0; i < zones.Length; i++) derived.Register(Box(zones[i], i * 80));
        derived.Refresh(world);
        for (int i = 0; i < zones.Length; i++)
            Check(derived.PatrolWeight(new(i, new(i * 80, 0, 0))) > 1, $"installed zone {zones[i]}");
    }
}
Console.WriteLine($"{checks} quest attraction checks passed.");
