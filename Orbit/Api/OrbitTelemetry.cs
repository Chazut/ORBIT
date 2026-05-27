using System.Collections.Generic;
using Comfort.Common;
using Orbit.Core;
using Orbit.Entities;

namespace Orbit.Api;

/// <summary>
/// Stable public read-only telemetry surface. External consumers (e.g.
/// raid-review) reference ORBIT.dll directly and call these methods rather
/// than reflecting against internal types. DTO types in this namespace are
/// the API contract — internal renames or restructuring inside <c>Orbit.*</c>
/// must not change them without a major version bump.
/// </summary>
public static class OrbitTelemetry
{
    /// <summary>
    /// True when an OrbitManager singleton has been created (raid in
    /// progress). Cheap call — safe to invoke every tick.
    /// </summary>
    public static bool IsAvailable => Singleton<OrbitManager>.Instance != null;

    /// <summary>
    /// Resolve a bot's current objective by profile id. Returns null when
    /// no ORBIT-managed agent matches, the agent is inactive, or it has no
    /// objective. Lookup is O(N) over the live agent list — fine at the
    /// pacing raid-review uses but caller may want to batch.
    /// </summary>
    public static OrbitBotObjective GetBotObjective(string profileId)
    {
        if (string.IsNullOrEmpty(profileId)) return null;
        var manager = Singleton<OrbitManager>.Instance;
        if (manager?.AgentData == null) return null;

        Agent agent = null;
        var agents = manager.AgentData.Entities.Values;
        for (var i = 0; i < agents.Count; i++)
        {
            var a = agents[i];
            if (a?.Player != null && a.Player.ProfileId == profileId)
            {
                agent = a;
                break;
            }
        }
        if (agent == null || !agent.IsActive) return null;

        var obj = agent.Objective;
        if (obj == null) return null;

        var loc = obj.Location;
        var category = loc != null ? loc.Category.ToString() : "";
        var x = loc?.Position.x ?? 0f;
        var y = loc?.Position.y ?? 0f;
        var z = loc?.Position.z ?? 0f;

        var extractReason = "";
        var squad = agent.Squad;
        if (squad != null && squad.ExtractRequested)
        {
            extractReason = squad.ExtractRequestedReason ?? "";
        }

        return new OrbitBotObjective
        {
            Status = obj.Status.ToString(),
            Category = category,
            IsLeader = agent.IsLeader,
            ObjectiveX = x,
            ObjectiveY = y,
            ObjectiveZ = z,
            ExtractReason = extractReason,
        };
    }

    /// <summary>
    /// Snapshot of the advection field + hot zones. Returns null when the
    /// WaypointSystem isn't ready yet OR the snapshot would be empty (no
    /// non-zero advection cells AND no zones).
    /// </summary>
    public static OrbitFieldSnapshot GetFieldSnapshot()
    {
        var manager = Singleton<OrbitManager>.Instance;
        var ws = manager?.WaypointSystem;
        if (ws == null) return null;

        var advectionField = ws.AdvectionField;
        var gridSize = ws.GridSize;
        var advection = new List<OrbitFieldCell>();
        if (advectionField != null)
        {
            for (var x = 0; x < gridSize.x; x++)
            {
                for (var y = 0; y < gridSize.y; y++)
                {
                    var v = advectionField[x, y];
                    if (v.x * v.x + v.y * v.y < 0.0001f) continue;
                    advection.Add(new OrbitFieldCell { X = x, Y = y, Fx = v.x, Fz = v.y });
                }
            }
        }

        var zones = new List<OrbitFieldZone>();
        var zonesList = ws.Zones;
        if (zonesList != null)
        {
            for (var i = 0; i < zonesList.Count; i++)
            {
                var z = zonesList[i];
                zones.Add(new OrbitFieldZone
                {
                    X = z.Coords.x,
                    Y = z.Coords.y,
                    Radius = z.Radius,
                    Force = z.Force,
                    Decay = z.Decay,
                });
            }
        }

        if (advection.Count == 0 && zones.Count == 0) return null;

        return new OrbitFieldSnapshot
        {
            GridCols = gridSize.x,
            GridRows = gridSize.y,
            WorldMinX = ws.WorldMin.x,
            WorldMinZ = ws.WorldMin.y,
            CellSize = ws.CellSize,
            Advection = advection,
            Zones = zones,
        };
    }

    /// <summary>
    /// Per-squad main-objective list snapshot. Empty list when no squad
    /// carries any mains (e.g. raid hasn't started, or only boss/raider
    /// squads exist which skip the system entirely).
    /// </summary>
    public static List<OrbitSquadMainObjectives> GetMainObjectivesSnapshot()
    {
        var result = new List<OrbitSquadMainObjectives>();
        var manager = Singleton<OrbitManager>.Instance;
        if (manager?.SquadData == null) return result;

        var squads = manager.SquadData.Entities.Values;
        for (var i = 0; i < squads.Count; i++)
        {
            var squad = squads[i];
            if (squad?.MainObjectives == null || squad.MainObjectives.Count == 0) continue;

            var memberIds = new List<string>(squad.Members.Count);
            for (var m = 0; m < squad.Members.Count; m++)
            {
                var pid = squad.Members[m]?.Player?.ProfileId;
                if (!string.IsNullOrEmpty(pid)) memberIds.Add(pid);
            }

            var mains = new List<OrbitMainObjective>(squad.MainObjectives.Count);
            for (var mi = 0; mi < squad.MainObjectives.Count; mi++)
            {
                var m = squad.MainObjectives[mi];
                mains.Add(new OrbitMainObjective
                {
                    Type = m.Type.ToString(),
                    CellX = m.CellCoords.x,
                    CellY = m.CellCoords.y,
                    X = m.Position.x,
                    Y = m.Position.y,
                    Z = m.Position.z,
                    Completed = m.Completed,
                    KillsRoamStartedAt = m.KillsRoamStartedAt,
                    KillsRoamTargetDuration = m.KillsRoamTargetDuration,
                    LootValueEnteredAt = m.LootValueEnteredAt,
                    LootValueTotal = m.LootValueTotal,
                    LootValueInterrupted = m.LootValueInterrupted,
                    QuestTriggerId = m.QuestTriggerId,
                    QuestTitle = m.QuestTitle,
                });
            }

            result.Add(new OrbitSquadMainObjectives
            {
                SquadId = squad.Id,
                MemberProfileIds = memberIds,
                MainObjectives = mains,
            });
        }
        return result;
    }
}

public class OrbitBotObjective
{
    public string Status;
    public string Category;
    public bool IsLeader;
    public float ObjectiveX;
    public float ObjectiveY;
    public float ObjectiveZ;
    /// <summary>Non-empty only when the squad has flipped ExtractRequested.</summary>
    public string ExtractReason;
}

public class OrbitFieldSnapshot
{
    public int GridCols;
    public int GridRows;
    public float WorldMinX;
    public float WorldMinZ;
    public float CellSize;
    public List<OrbitFieldCell> Advection;
    public List<OrbitFieldZone> Zones;
}

public class OrbitFieldCell
{
    public int X;
    public int Y;
    public float Fx;
    public float Fz;
}

public class OrbitFieldZone
{
    public int X;
    public int Y;
    public float Radius;
    public float Force;
    public float Decay;
}

public class OrbitSquadMainObjectives
{
    public int SquadId;
    public List<string> MemberProfileIds;
    public List<OrbitMainObjective> MainObjectives;
}

public class OrbitMainObjective
{
    /// <summary>"Kills" | "LootValue" | "Quest".</summary>
    public string Type;
    public int CellX;
    public int CellY;
    public float X;
    public float Y;
    public float Z;
    public bool Completed;
    public float KillsRoamStartedAt;
    public float KillsRoamTargetDuration;
    public float LootValueEnteredAt;
    public float LootValueTotal;
    public bool LootValueInterrupted;
    public string QuestTriggerId;
    public string QuestTitle;
}
