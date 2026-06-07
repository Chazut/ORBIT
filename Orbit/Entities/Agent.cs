using System.Collections.Generic;
using System.Runtime.CompilerServices;
using EFT;
using UnityEngine;

namespace Orbit.Entities;

/// <summary>
/// One bot. Holds the BotOwner / Player handles, the cached body transform for cheap <see cref="Position"/>
/// reads, and one instance of every per-agent component (Movement, Stuck, Look, Objective, Guard) — those
/// live on the Agent rather than in side arrays because every agent has exactly one of each. <see
/// cref="Squad"/> is set after squad assignment;
/// <see cref="IsLeader"/> is mirrored from the squad's first-member position
/// for fast lookups inside per-tick loops.
/// </summary>
public class Agent(int id, BotOwner bot, float[] taskScores) : Entity(id, taskScores)
{
    public bool IsActive;
    public bool IsLeader;
    public Squad Squad;

    public readonly BotOwner Bot = bot;
    public readonly Player Player = bot.Mover.Player;

    public readonly Movement Movement = new();
    public readonly Stuck Stuck = new();
    public readonly Look Look = new();

    public readonly Objective Objective = new();
    public readonly Guard Guard = new();

    /// <summary>
    /// Id of the POI on which this agent's most recent Goto attempt failed via the "stopped outside arrival
    /// radius" branch, or -1 if the most recent dispatch ended cleanly. Used by GotoObjectiveAction to detect
    /// repeated arrival failures on the SAME POI by this agent. Distinct from the squad-level
    /// <c>ConsecutiveFailedDispatches</c>: that counter targets the SQUAD anchor when it blacklists, so when
    /// the bot is failing on a SPLINTER whose parent is the squad anchor, the squad blacklists the parent —
    /// the splinter itself stays a valid candidate for the next pick and the loop continues. The per-agent
    /// counter blacklists the specific POI the bot is physically failing to reach.
    /// </summary>
    public int LastFailedPoiId = -1;

    /// <summary>
    /// Counter incremented every time Goto fails on
    /// <see cref="LastFailedPoiId"/>. Reset to 1 when the agent fails on a
    /// DIFFERENT POI. Past the blacklist threshold the POI is added to the squad's <c>CompletedPoiIds</c> so
    /// future picks skip it.
    /// </summary>
    public int ConsecutiveSamePoiFailures;

    /// <summary>
    /// POI id this agent is currently failing arrival on because Physics.Raycast LoS is blocked (within the
    /// arrival radius but a wall sits between bot and target). -1 = not tracking. Together with <see
    /// cref="LoSBlockedSinceTime"/> this drives a time-based blacklist for Quest waypoints whose target
    /// position sits inside a wall — the standard TrackArrivalFailure counter never fires there because the
    /// bot keeps "moving" instead of stopping.
    /// </summary>
    public int LoSBlockedPoiId = -1;

    /// <summary>Time.time at which the current LoS-blocked tracking window started. -1 = not tracking.</summary>
    public float LoSBlockedSinceTime = -1f;

    /// <summary>
    /// This agent's own extract-loot threshold, rolled from their OWN SAIN brain's archetype range (for PMCs)
    /// or set to the faction-default global knob (PlayerScavs). 0 = not yet resolved (SAIN async attach still
    /// pending, or just never tried). Resolved lazily inside the loot routine the first time the squad's loot
    /// crosses the relevant threshold. The squad-wide extract trigger sums alive members' resolved thresholds
    /// — a mixed Rat+Chad squad needs Rat-range + Chad-range total, not 2× the leader's range. Death of a
    /// member drops their contribution; the surviving members' summed threshold becomes the new bar.
    /// </summary>
    public float OwnExtractLootThreshold;

    /// <summary>
    /// Per-archetype mini-loot value threshold, lazily resolved from the agent's own SAIN brain. 0 until
    /// resolved; falls back to the squad personality (or <see
    /// cref="OrbitLootHandler.DefaultMinPickupPrice"/>) when SAIN attach is pending.
    /// </summary>
    public float OwnMiniLootValueThreshold;

    /// <summary>
    /// POIs this agent has personally value-rejected. Filtered against in dispatch and sweep so the agent
    /// isn't sent back, while softer-gated squad members can still be dispatched (squad-level
    /// <see cref="Squad.CompletedPoiIds"/> is untouched).
    /// </summary>
    public readonly HashSet<int> ValueSkippedPoiIds = new();

    private readonly BifacialTransform _bodyTransform = bot.Mover.Player.PlayerBones.BodyTransform;

    public Vector3 Position
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _bodyTransform.position;
    }

    public override string ToString()
    {
        return $"Agent(Id: {Id}, BsgId: {Bot.Id}, Name: {Bot.Profile.Nickname})";
    }
}
