using UnityEngine;

namespace Orbit.Entities;

/// <summary>
/// Long-term goal categories for a squad's main-objective list. Drives
/// the per-squad force attraction in the waypoint system and the
/// completion logic in the dispatch strategy.
/// </summary>
public enum MainObjectiveType
{
    /// <summary>Roam a PvP hotspot (anchor = a positive-Force zone from
    /// Config). Two phases: approach (inverse-distance pull) then roam
    /// (constant-magnitude pull) for a per-main rolled duration.</summary>
    Kills,
    /// <summary>Clean a precomputed high-value cell (anchor = top-quartile
    /// cell by total Container + LooseLoot value). Completes when all
    /// loot POIs in the cell are looted/blacklisted, all members are
    /// inventory-full, or the timeout elapses.</summary>
    LootValue,
    /// <summary>Visit a specific Quest trigger position (anchor = the
    /// quest's TriggerWithId nav-point). Visible to PickFromCell only for
    /// the squad whose main owns the trigger ID. Completes when a member
    /// reaches the trigger.</summary>
    Quest,
}

/// <summary>
/// A single squad-level long-term goal. Squads are assigned a list of
/// 1-5 of these at creation; execution is opportunistic (the squad's
/// force-attraction sums inverse-distance pulls toward every pending
/// main, closest naturally dominating). When all mains in the list are
/// <see cref="Completed"/>, the squad flips ExtractRequested.
/// </summary>
public class MainObjective
{
    public MainObjectiveType Type;
    /// <summary>Grid anchor — used by the force-attraction formula.</summary>
    public Vector2Int CellCoords;
    /// <summary>World anchor — the exact point the squad is being pulled
    /// toward. For Kills this is the centre of a Config zone; for
    /// LootValue, the centre of the high-value cell; for Quest, the
    /// trigger's nav-point.</summary>
    public Vector3 Position;
    public bool Completed;

    // ── Kills-type only ──────────────────────────────────────────────
    /// <summary><see cref="Time.time"/> at which the first squad member
    /// entered the anchor cell. 0 while approaching. Once set, the
    /// force-attraction switches from inverse-distance to a constant pull
    /// so the squad oscillates around the anchor for
    /// <see cref="KillsRoamTargetDuration"/> seconds, then completes.</summary>
    public float KillsRoamStartedAt;
    /// <summary>Rolled at generation, 60-300s gaussian. Time the leader
    /// must spend "in roam" after first entry before
    /// <see cref="Completed"/> flips.</summary>
    public float KillsRoamTargetDuration;

    // ── LootValue-type only ──────────────────────────────────────────
    /// <summary><see cref="Time.time"/> at which the squad strategy first
    /// ticked this main (used for the timeout safety net).</summary>
    public float LootValueStartedAt;
    /// <summary><see cref="Time.time"/> at which the first squad member
    /// entered the anchor cell. 0 while in approach phase. Surfaced to
    /// the raid-review overlay to render started LootValue mains with a
    /// thicker highlighted ring vs pending ones.</summary>
    public float LootValueEnteredAt;
    /// <summary>Cumulative seconds the squad has been "engaged" on this
    /// main — engaged = at least one member in the anchor cell AND the
    /// squad is not in combat. The timeout fires when this reaches the
    /// configured budget, NOT when absolute time-since-entry does — so a
    /// 3-min firefight in the loot cell doesn't eat into the loot budget.</summary>
    public float LootValueElapsedEngaged;
    /// <summary><see cref="Time.time"/> of the last engaged tick, used to
    /// compute the next delta. 0 when not currently engaged (combat,
    /// out-of-cell, before cell entry).</summary>
    public float LootValueLastEngagedAt;
    /// <summary>True when the squad WAS engaged on this main but has
    /// since paused — combat broke out or the bot left the cell. Goes
    /// back to false when engagement resumes. Surfaced to the raid-
    /// review viz so the main's marker shows an "interrupted" visual.</summary>
    public bool LootValueInterrupted;
    /// <summary>Total rouble value of all Container + LooseLoot POIs in
    /// the anchor cell at raid start. Precomputed at generation; only
    /// used by the raid-review viz to label the cell. Doesn't drive
    /// dispatch logic.</summary>
    public float LootValueTotal;

    // ── Quest-type only ──────────────────────────────────────────────
    /// <summary>The <c>TriggerWithId.Id</c> of the EFT quest zone this
    /// main owns. Drives the owner-only dispatch filter — other squads
    /// can't pick this Quest POI as a secondary objective.</summary>
    public string QuestTriggerId;
    /// <summary>Human-readable quest title for the raid-review tooltip.
    /// Falls back to the trigger ID if the SPT quest-template resolver
    /// isn't wired.</summary>
    public string QuestTitle;
}
