using System.Collections.Generic;
using UnityEngine;

namespace Orbit.Entities;

/// <summary>
/// Long-term goal categories for a squad's main-objective list. Drives the per-squad force attraction in the
/// waypoint system and the completion logic in the dispatch strategy.
/// </summary>
public enum MainObjectiveType
{
    /// <summary>Roam a PvP hotspot (anchor = a positive-Force zone from
    /// Config). Two phases: approach (inverse-distance pull) then roam (constant-magnitude pull) for a
    /// per-main rolled duration.</summary>
    Kills,
    /// <summary>Clean a precomputed high-value cell (anchor = top-quartile
    /// cell by total Container + LooseLoot value). Completes when all loot POIs in the cell are
    /// looted/blacklisted, all members are inventory-full, or the timeout elapses.</summary>
    LootValue,
    /// <summary>Visit a specific Quest trigger position (anchor = the
    /// quest's TriggerWithId nav-point). Visible to PickFromCell only for the squad whose main owns the
    /// trigger ID. Completes when a member reaches the trigger.</summary>
    Quest,
    /// <summary>After the other mains, hold cover near an exit until time-based extraction.</summary>
    ExtractCamp,
    /// <summary>A persistent chain of world interactions, counted as one main objective.</summary>
    MultiStep,
    Rush,
}

/// <summary>
/// A single squad-level long-term goal. Squads are assigned a list of 1-5 of these at creation; execution is
/// opportunistic (the squad's force-attraction sums inverse-distance pulls toward every pending main, closest
/// naturally dominating). When all mains in the list are
/// <see cref="Completed"/>, the squad flips ExtractRequested.
/// </summary>
public class MainObjective
{
    internal Orbit.Systems.OperationPlan Operation;
    internal Orbit.Systems.RushPlan Rush;
    public MainObjectiveType Type;
    /// <summary>Grid anchor — used by the force-attraction formula.</summary>
    public Vector2Int CellCoords;
    /// <summary>World anchor — the exact point the squad is being pulled
    /// toward. For Kills this is the centre of a Config zone; for LootValue, the centre of the high-value
    /// cell; for Quest, the trigger's nav-point.</summary>
    public Vector3 Position;
    public bool Completed;
    public bool KillAmbush;
    public float KillZoneRadius;
    public Vector3 KillZoneCenter;
    public float CampElapsed;
    public float CampStartedAt;
    public float CampTargetDuration;
    public string CampState;
    public string CampFailure;
    public int CampSearchAttempt;
    public float CampSearchRadius;

    internal void SetCampState(string state, string failure = null)
    {
        if (CampState == state && CampFailure == failure) return;
        CampState = state; CampFailure = failure;
        Orbit.Api.OrbitTelemetry.MainObjectivesRevision++;
    }
    internal float CampRetryAt;
    internal int CampRouteFailures;
    internal Orbit.Systems.AmbushSite CampSite;
    internal Orbit.Systems.ExtractCampApproach CampApproach;
    public bool IsCampMain => Type == MainObjectiveType.ExtractCamp || Type == MainObjectiveType.Kills && KillAmbush;

    public bool CanPursue(IReadOnlyList<MainObjective> mains)
    {
        if (Completed) return false;
        if (Type != MainObjectiveType.Rush && HasPendingRush(mains)) return false;
        if (Type != MainObjectiveType.ExtractCamp) return true;
        if (mains != null)
            for (var i = 0; i < mains.Count; i++)
                if (!mains[i].Completed && mains[i].Type != MainObjectiveType.ExtractCamp) return false;
        return true;
    }

    public static bool HasPendingRush(IReadOnlyList<MainObjective> mains)
    {
        if (mains != null)
            for (var i = 0; i < mains.Count; i++)
                if (!mains[i].Completed && mains[i].Type == MainObjectiveType.Rush) return true;
        return false;
    }

    public static bool HasPendingExtractCamp(IReadOnlyList<MainObjective> mains)
    {
        if (mains != null)
            for (var i = 0; i < mains.Count; i++)
                if (!mains[i].Completed && mains[i].Type == MainObjectiveType.ExtractCamp) return true;
        return false;
    }

    // Null preserves the original all-floor main semantics for existing zone files.
    public string ZoneFloorId;
    public float KillsFloorElapsed;
    public float KillsFloorLastTick;

    // ── Kills-type only ──────────────────────────────────────────────
    /// <summary><see cref="Time.time"/> at which the first squad member
    /// entered the anchor cell. 0 while approaching. Once set, the force-attraction switches from
    /// inverse-distance to a constant pull so the squad oscillates around the anchor for
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
    /// entered the anchor cell. 0 while in approach phase. Surfaced to the raid-review overlay to render
    /// started LootValue mains with a thicker highlighted ring vs pending ones.</summary>
    public float LootValueEnteredAt;
    // A follower can enter first; cached paths must be checked again once the leader is local.
    public bool LootValueLocalValidation;
    /// <summary>Cumulative seconds the squad has been "engaged" on this
    /// main — engaged = at least one member in the anchor cell AND the squad is not in combat. The timeout
    /// fires when this reaches the configured budget, NOT when absolute time-since-entry does — so a 3-min
    /// firefight in the loot cell doesn't eat into the loot budget.</summary>
    public float LootValueElapsedEngaged;
    /// <summary><see cref="Time.time"/> of the last engaged tick, used to
    /// compute the next delta. 0 when not currently engaged (combat, out-of-cell, before cell
    /// entry).</summary>
    public float LootValueLastEngagedAt;
    /// <summary>True when the squad WAS engaged on this main but has
    /// since paused — combat broke out or the bot left the cell. Goes back to false when engagement resumes.
    /// Surfaced to the raid- review viz so the main's marker shows an "interrupted" visual.</summary>
    public bool LootValueInterrupted;
    /// <summary>Total rouble value of all Container + LooseLoot POIs in
    /// the anchor cell at raid start. Precomputed at generation; only used by the raid-review viz to label
    /// the cell. Doesn't drive dispatch logic.</summary>
    public float LootValueTotal;

    // ── Quest-type only ──────────────────────────────────────────────
    /// <summary>The <c>TriggerWithId.Id</c> of the EFT quest zone this
    /// main owns. Drives the owner-only dispatch filter — other squads can't pick this Quest POI as a
    /// secondary objective.</summary>
    public string QuestTriggerId;
    /// <summary>Human-readable quest title for the raid-review tooltip.
    /// Falls back to the trigger ID if the SPT quest-template resolver isn't wired.</summary>
    public string QuestTitle;
}
