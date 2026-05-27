using System.Collections.Generic;
using System.Text;
using Orbit.Entities;
using Orbit.Helpers;
using Orbit.Navigation;
using Orbit.Systems;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Orbit.Sain;

/// <summary>
/// Rolls a squad's <see cref="Squad.MainObjectives"/> list at creation
/// time. Player-blind — no read of <c>MainPlayer.Position</c> or any
/// other player state. Anchors come from static map data
/// (positive-Force zones for <see cref="MainObjectiveType.Kills"/>;
/// top-quartile loot cells for <see cref="MainObjectiveType.LootValue"/>;
/// <c>TriggerWithId</c> POI positions for
/// <see cref="MainObjectiveType.Quest"/>).
///
/// Only PMCs and PlayerScavs receive main objectives. Bot scavs, bosses,
/// raiders, goons, cultists, bloodhounds skip the system entirely.
/// </summary>
public static class MainObjectiveBuilder
{
    public static void Generate(Squad squad, WaypointSystem waypointSystem)
    {
        if (squad?.Leader?.Bot?.Profile?.Info?.Settings == null) return;
        if (!Plugin.MainObjectivesEnabled.Value)
        {
            Log.Debug($"{squad} skipping main objectives — master toggle OFF");
            return;
        }
        var role = squad.Leader.Bot.Profile.Info.Settings.Role;

        var isPmc = role.IsPMC();
        var isPlayerScav = squad.Leader.Bot.Profile.WillBeAPlayerScav();

        if (!isPmc && !isPlayerScav)
        {
            Log.Info($"{squad} skipping main objectives — role {role} is not PMC or PlayerScav");
            return;
        }
        if (isPmc && !Plugin.MainObjectivesEnabledForPmc.Value)
        {
            Log.Debug($"{squad} skipping main objectives — PMC opt-out toggle");
            return;
        }
        if (isPlayerScav && !Plugin.MainObjectivesEnabledForPlayerScav.Value)
        {
            Log.Debug($"{squad} skipping main objectives — PlayerScav opt-out toggle");
            return;
        }

        // Per-squad count source. PMC with SAIN personality → already
        // rolled into squad.Personality.MainCount. Otherwise (PlayerScav
        // or PMC with master toggle OFF) → use the global F12 range.
        int count;
        if (squad.Personality != null)
        {
            count = Mathf.Max(1, squad.Personality.MainCount);
        }
        else
        {
            var rawMin = isPmc ? Plugin.MainObjectivesCountMinPmc.Value : Plugin.MainObjectivesCountMinPlayerScav.Value;
            var rawMax = isPmc ? Plugin.MainObjectivesCountMaxPmc.Value : Plugin.MainObjectivesCountMaxPlayerScav.Value;
            var minCount = Mathf.Max(1, rawMin);
            var maxCount = Mathf.Max(minCount, rawMax);
            count = Random.Range(minCount, maxCount + 1);
        }
        squad.MainObjectives = new List<MainObjective>(count);

        // Lazy pools — only compute the ones we need.
        List<Vector2Int> topLootCells = null;
        List<Vector3> killZoneAnchors = null;
        List<Waypoint> questPool = null;

        // Resolve mix weights — PMC with SAIN personality uses
        // archetype-specific weights, PlayerScav and personality-OFF
        // PMCs fall back to the global F12 weights. Auto-normalised.
        float wQuest, wKills, wLootValue;
        if (squad.Personality != null)
        {
            wQuest = Mathf.Max(0f, squad.Personality.MainMixQuestWeight);
            wKills = Mathf.Max(0f, squad.Personality.MainMixKillsWeight);
            wLootValue = Mathf.Max(0f, squad.Personality.MainMixLootValueWeight);
        }
        else if (isPmc)
        {
            wQuest = Mathf.Max(0f, Plugin.MainObjectivesPmcQuestWeight.Value);
            wKills = Mathf.Max(0f, Plugin.MainObjectivesPmcKillsWeight.Value);
            wLootValue = Mathf.Max(0f, Plugin.MainObjectivesPmcLootValueWeight.Value);
        }
        else
        {
            wQuest = Mathf.Max(0f, Plugin.MainObjectivesPlayerScavQuestWeight.Value);
            wKills = Mathf.Max(0f, Plugin.MainObjectivesPlayerScavKillsWeight.Value);
            wLootValue = Mathf.Max(0f, Plugin.MainObjectivesPlayerScavLootValueWeight.Value);
        }
        var wSum = wQuest + wKills + wLootValue;
        if (wSum < 0.0001f) { wLootValue = 1f; wSum = 1f; } // degenerate: default LootValue
        var thQuest = wQuest / wSum;
        var thKills = thQuest + wKills / wSum;

        // Per-squad cell-use tracking. Without this, two RollKills or
        // RollLootValue calls can land on the same anchor cell because
        // the underlying pools (positive-Force zones, top-loot cells)
        // are small. A duplicate is redundant (the squad would do the
        // same thing twice) and inflates the count toward the timeout
        // fallback. Each roll retries up to MaxRolls times against this
        // set; if every retry collides we accept the duplicate as a
        // last resort rather than dropping the main entirely.
        var usedCells = new HashSet<Vector2Int>();
        const int MaxRetriesOnDuplicate = 6;

        for (var i = 0; i < count; i++)
        {
            var roll = Random.value;
            MainObjectiveType type;
            if (roll < thQuest) type = MainObjectiveType.Quest;
            else if (roll < thKills) type = MainObjectiveType.Kills;
            else type = MainObjectiveType.LootValue;

            MainObjective main = null;
            for (var attempt = 0; attempt < MaxRetriesOnDuplicate && main == null; attempt++)
            {
                MainObjective candidate = null;
                switch (type)
                {
                    case MainObjectiveType.Quest:
                        questPool ??= BuildQuestPool(waypointSystem);
                        candidate = RollQuest(questPool, waypointSystem, squad);
                        break;
                    case MainObjectiveType.Kills:
                        killZoneAnchors ??= waypointSystem.GetPositiveForceZoneAnchors();
                        candidate = RollKills(killZoneAnchors, waypointSystem, squad);
                        break;
                }

                // LootValue path. Used both for explicit LootValue rolls
                // and as a fallback when Quest/Kills couldn't find an
                // anchor (questPool exhausted for this squad, or map has
                // no PvP zones). Loops here too so a duplicate LootValue
                // cell gets retried against `usedCells`.
                if (candidate == null)
                {
                    topLootCells ??= waypointSystem.GetTopLootCells();
                    candidate = RollLootValue(topLootCells, waypointSystem, squad);
                }
                if (candidate == null) break; // pool empty, give up on this slot

                if (!usedCells.Contains(candidate.CellCoords))
                    main = candidate;
                // else: collision with an existing cell within THIS
                // squad — retry the same type. Cross-squad collisions
                // are intentional (multiple squads can target the same
                // Quest / Kills / LootValue cell, which is exactly how
                // friction points emerge in PvP).
            }

            if (main != null)
            {
                squad.MainObjectives.Add(main);
                usedCells.Add(main.CellCoords);
            }
        }

        if (squad.MainObjectives.Count == 0)
        {
            squad.MainObjectives = null;
            Log.Warning($"{squad} generated zero main objectives (no eligible anchors); will behave like baseline dispatch");
            return;
        }

        var summary = new StringBuilder();
        for (var i = 0; i < squad.MainObjectives.Count; i++)
        {
            if (i > 0) summary.Append(", ");
            var m = squad.MainObjectives[i];
            if (m.Type == MainObjectiveType.Quest)
                summary.Append($"Quest:{m.QuestTriggerId}@{m.CellCoords}");
            else
                summary.Append($"{m.Type}@{m.CellCoords}");
        }
        Log.Info($"{squad} role={role} mains[{squad.MainObjectives.Count}]: {summary}");
    }

    private static MainObjective RollKills(List<Vector3> anchors, WaypointSystem waypointSystem, Squad squad)
    {
        if (anchors.Count == 0) return null;
        var pos = anchors[Random.Range(0, anchors.Count)];
        // Per-archetype roam-duration range if the squad has a SAIN
        // personality; falls back to the global F12 range for
        // PlayerScavs and personality-OFF PMCs.
        var roamRange = squad.Personality != null
            ? squad.Personality.KillsRoamDuration
            : Plugin.MainObjectivesKillsRoamDuration.Value;
        return new MainObjective
        {
            Type = MainObjectiveType.Kills,
            Position = pos,
            CellCoords = waypointSystem.WorldToCell(pos),
            KillsRoamTargetDuration = Random.Range(roamRange.x, roamRange.y),
        };
    }

    private static MainObjective RollLootValue(List<Vector2Int> topCells, WaypointSystem waypointSystem, Squad squad)
    {
        if (topCells.Count == 0) return null;
        // Per-archetype slice of the global top-loot pool. PMC with SAIN
        // personality restricts to the first N entries (e.g. top-3 for
        // VeryAggressive, top-10 for Cautious). PlayerScav / OFF uses
        // the full pool.
        var slice = squad.Personality != null
            ? Mathf.Clamp(squad.Personality.TopLootCellsMax, 1, topCells.Count)
            : topCells.Count;
        var cell = topCells[Random.Range(0, slice)];
        return new MainObjective
        {
            Type = MainObjectiveType.LootValue,
            CellCoords = cell,
            Position = waypointSystem.CellToWorld(cell),
            // One-shot rouble-value compute for the raid-review viz
            // tooltip / sidebar label. Static — the cell's loot doesn't
            // get richer over the raid.
            LootValueTotal = waypointSystem.SumCellLootValue(cell),
        };
    }

    private static MainObjective RollQuest(List<Waypoint> questPool, WaypointSystem waypointSystem, Squad squad)
    {
        // Walk the pool in random order, pick the first trigger that is
        // REACHABLE from the squad's spawn position AND not already
        // owned by this same squad. Cross-squad collisions are
        // intentional: two squads holding the same Quest main means
        // they'll both route to the same trigger position — that's the
        // PvP friction the main-objective system is designed to create.
        // The reachability check (NavMesh.CalculatePath) prevents a
        // Quest main on a navmesh fragment that's disconnected from the
        // squad's spawn — bot would otherwise roam the entire map
        // without ever being able to touch the trigger.
        if (questPool == null || questPool.Count == 0) return null;
        var startIdx = Random.Range(0, questPool.Count);
        for (var i = 0; i < questPool.Count; i++)
        {
            var loc = questPool[(startIdx + i) % questPool.Count];
            var triggerId = loc.Name;
            if (string.IsNullOrEmpty(triggerId)) continue;

            // Intra-squad dedup: don't roll the same Quest trigger
            // twice for the same squad (cross-squad reuse is fine).
            if (SquadAlreadyHasQuest(squad, triggerId)) continue;

            // Reachability gate. Uses squad.SpawnPosition (captured at
            // squad creation) as the source. If unreachable, skip and
            // try the next pool entry.
            if (squad != null && squad.SpawnPosition != Vector3.zero
                && !waypointSystem.IsReachableFromPosition(squad.SpawnPosition, loc.Position))
            {
                Log.Info($"{squad} skipping Quest main {triggerId}@{waypointSystem.WorldToCell(loc.Position)} — NavMesh.CalculatePath from spawn {squad.SpawnPosition} returned non-Complete");
                continue;
            }

            return new MainObjective
            {
                Type = MainObjectiveType.Quest,
                Position = loc.Position,
                CellCoords = waypointSystem.WorldToCell(loc.Position),
                QuestTriggerId = triggerId,
                // Title resolution against the SPT quest DB is future
                // work; fall back to the trigger ID for now (readable
                // enough for the raid-review tooltip).
                QuestTitle = triggerId,
            };
        }
        return null; // pool exhausted (every reachable quest already in this squad's mains)
    }

    private static bool SquadAlreadyHasQuest(Squad squad, string triggerId)
    {
        if (squad?.MainObjectives == null) return false;
        for (var i = 0; i < squad.MainObjectives.Count; i++)
        {
            var m = squad.MainObjectives[i];
            if (m.Type == MainObjectiveType.Quest && m.QuestTriggerId == triggerId) return true;
        }
        return false;
    }

    private static List<Waypoint> BuildQuestPool(WaypointSystem waypointSystem)
    {
        var pool = new List<Waypoint>();
        var cells = waypointSystem.Cells;
        var size = waypointSystem.GridSize;
        for (var x = 0; x < size.x; x++)
        {
            for (var y = 0; y < size.y; y++)
            {
                ref var cell = ref cells[x, y];
                if (!cell.HasWaypoints) continue;
                for (var k = 0; k < cell.Waypoints.Count; k++)
                {
                    var loc = cell.Waypoints[k];
                    if (loc.Category == WaypointCategory.Quest) pool.Add(loc);
                }
            }
        }
        return pool;
    }
}
