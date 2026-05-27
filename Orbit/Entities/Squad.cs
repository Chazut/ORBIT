using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Orbit.Helpers;
using Orbit.Navigation;
using Orbit.Sain;
using UnityEngine;

namespace Orbit.Entities;

/// <summary>
/// A coordinated bot group. Holds its member list, the current shared
/// objective, per-squad caches (blacklists, cooldowns, door-roll history)
/// and the long-term goals rolled at squad creation.
/// </summary>
public class Squad(int id, float[] taskScores, int targetMembersCount) : Entity(id, taskScores)
{
    public readonly List<Agent> Members = new(6);
    public readonly SquadObjective Objective = new();
    public readonly int TargetMembersCount = targetMembersCount;

    /// <summary>
    /// POIs this squad considers permanently done. Populated when:
    ///   - LootContainerAction successfully loots a ContainerLoot or Corpse.
    ///   - A LooseLoot pickup attempt yielded nothing (filtered by value
    ///     threshold etc) and the item is still in the world.
    ///   - The squad reaches a Quest POI (the trigger was hit).
    /// LooseLoot actually picked up goes through RemoveWaypoint globally
    /// instead; it never lands here. Filtered out in PickFromCell — a
    /// hard constraint that survives the fallback re-pick relaxations.
    /// </summary>
    public readonly HashSet<int> CompletedPoiIds = new();

    /// <summary>
    /// PMC-only loot cooldown: cell coords → expiry time. Armed when a PMC
    /// squad *leaves* a cell where it picked loot. For the next 10 min,
    /// loot POIs in that cell are skipped for this squad. The squad CAN
    /// still enter the cell for Quest / Synthetic / Exfil POIs, and a
    /// runtime loot POI (fresh kill) lifts the cooldown for that cell
    /// automatically. Scavs are exempt — they wander, revisiting is fine.
    /// </summary>
    public readonly Dictionary<Vector2Int, float> LootCellCooldowns = new();

    /// <summary>
    /// Cell where this squad most recently picked a loot POI. Used to
    /// detect "left the cell" — when the squad's next pick lands in a
    /// different cell, the previous one is armed in LootCellCooldowns.
    /// </summary>
    public Vector2Int? LastLootCell;

    /// <summary>
    /// Cell the leader occupied at the previous strategy tick. Drives the
    /// rolling unreachability-cache refresh: when this changes, the
    /// strategy re-evaluates per-squad unreachable verdicts in the 3x3
    /// cell window around the new cell (stale verdicts cached from spawn
    /// become wrong once the leader has moved).
    /// </summary>
    public Vector2Int? LastKnownCell;

    /// <summary>
    /// Per-cell timestamp of the most recent unreachability refresh for
    /// this squad. Skips cells already refreshed within the configured
    /// cooldown — without this dedup, a leader oscillating between two
    /// adjacent cells would re-clear every POI in their shared 3x3
    /// neighbours on every transition.
    /// </summary>
    public readonly Dictionary<Vector2Int, float> RecentlyRefreshedCells = new();

    /// <summary>
    /// Cell coordinates where this squad's leader spawned. Scavs only —
    /// biases prefDirection back toward home so they stay in their spawn
    /// quartier even when chained loot neighbour-hops would otherwise
    /// drift them across the map. Lazy init: null until the first
    /// RequestNear call, then frozen for the rest of the raid.
    /// </summary>
    public Vector2Int? SpawnCell;

    /// <summary>
    /// True once any member has crossed their faction's extract-at-loot-
    /// value threshold. From then on the dispatcher bypasses normal POI
    /// selection and routes the squad straight to the nearest eligible
    /// exfil. One-way flag — stays true for the rest of the raid.
    /// </summary>
    public bool ExtractRequested;

    /// <summary>
    /// Short human-readable string describing WHY this squad flipped
    /// <see cref="ExtractRequested"/>. Set at the same moment the flag
    /// goes true; null/empty until then. Surfaced to raid-review.
    /// </summary>
    public string ExtractRequestedReason;

    /// <summary>
    /// Per-squad randomization factor (0.75 - 1.25, set at squad creation)
    /// applied to the extract-at-loot-value threshold so two same-faction
    /// squads in the same raid don't both flip at the exact same rouble
    /// count.
    /// </summary>
    public float ExtractValueRandomization = 1f;

    /// <summary>
    /// How many consecutive times AssignNewObjective came back null (no
    /// eligible POI anywhere reachable from this squad). Reset whenever a
    /// member actually reaches an objective. Past a small threshold the
    /// squad is treated as 'islanded' and pinned to its current cell so it
    /// stops ping-ponging unreachable objectives.
    /// </summary>
    public int ConsecutiveDispatchFailures;

    /// <summary>
    /// How many consecutive times every squad member arrived at the "all
    /// members failed their objective en-route" branch. Distinct from
    /// <see cref="ConsecutiveDispatchFailures"/> which counts null returns
    /// from RequestNear. Past the blacklist threshold the squad blacklists
    /// the POI for itself and re-dispatches normally — no teleport rescue.
    /// </summary>
    public int ConsecutiveFailedDispatches;

    /// <summary>
    /// Per-Synthetic-POI short-term visit cooldown (loc.Id → expiry time).
    /// Synthetic POIs are patrol fillers with no natural "done" semantics;
    /// without this guard a squad whose own cell only contained one
    /// Synthetic could ping-pong on it forever between wait timers.
    /// </summary>
    public readonly Dictionary<int, float> RecentlyVisitedPoiCooldowns = new();

    /// <summary>
    /// Time-left-in-raid (in seconds) at which this squad will flip
    /// ExtractRequested even without hitting the loot-value trigger.
    /// Rolled lazily on first check; faction- and map-dependent.
    /// <see cref="float.NaN"/> = not yet rolled. Other factions never roll
    /// one (they're not allowed to extract).
    /// </summary>
    public float TimeExtractThresholdSeconds = float.NaN;

    /// <summary>
    /// Long-term goals assigned at squad creation, 1-5 entries (or 0 if
    /// the squad is a boss/raider — they skip the system entirely).
    /// Execution is opportunistic: the force-attraction sums inverse-
    /// distance pulls toward every pending main, closest naturally
    /// dominating. When every main is Completed, the squad flips
    /// <see cref="ExtractRequested"/>.
    /// </summary>
    public List<MainObjective> MainObjectives;

    // ── Combat convergence (active during Kills roam / LootValue) ──
    /// <summary>Index in <see cref="Members"/> of the squad member who
    /// most recently triggered combat detection. -1 = no caller active.</summary>
    public int CombatCallerMemberIdx = -1;

    /// <summary><see cref="Time.time"/> of the last combat detection.
    /// Used for the 5-second grace before clearing the caller state.</summary>
    public float CombatCallerLastSeenAt;

    /// <summary>Position captured when the combat caller was detected.
    /// Stable anchor — even if the caller moves, others converge on where
    /// they first saw the threat.</summary>
    public Vector3 CombatCallerPosition;

    /// <summary>
    /// Objective the squad was on when an opportunistic corpse loot
    /// interrupted it. Null when no interrupt is in progress. Restored to
    /// <see cref="SquadObjective.Location"/> when the interrupting loot
    /// finishes, then cleared.
    /// </summary>
    public Waypoint PreInterruptObjectiveLocation;

    /// <summary>
    /// Time.time of the last opportunistic-corpse scan run for this squad.
    /// Paces the scan (it's an O(members × nearby corpses) raycast loop,
    /// so we don't want to fire every frame). 0 = never run.
    /// </summary>
    public float LastOpportunisticCorpseScanTime;

    /// <summary>
    /// World position where this squad's leader spawned (captured once at
    /// squad creation). Used by the waypoint system to derive an
    /// Infiltration name from the closest SpawnPointMarker when the bot's
    /// <c>Profile.Info.EntryPoint</c> is empty — covers PMC bots spawned
    /// by faction mods (Legion / UNTAR / RUAF) and special-spawn vanilla
    /// bots that BSG never assigns an entry to.
    /// </summary>
    public Vector3 SpawnPosition;

    /// <summary>
    /// Derived (or BSG-native) Infiltration name used for the spawn-side
    /// exfil filter. Computed lazily on first call when the bot's
    /// EntryPoint is empty. <see langword="null"/> = not yet resolved;
    /// empty string = resolved but no marker found.
    /// </summary>
    public string DerivedEntryPoint;

    /// <summary>
    /// SAIN-resolved personality archetype for this squad's leader,
    /// captured once at squad registration. Unused for non-PMC squads.
    /// Defaults to Average when SAIN is missing or the brain doesn't
    /// resolve.
    /// </summary>
    public PersonalityArchetype Archetype;

    /// <summary>
    /// Per-squad rolled values from the archetype's F12 table — extract
    /// threshold, loot coverage %, sweep / splinter radii, etc. Null for
    /// scavs / PlayerScavs (they always use the global Orbit knobs even
    /// when SAIN personality is enabled).
    /// </summary>
    public PersonalityProfile Personality;

    /// <summary>
    /// True until SAIN has attached its BotComponent for this leader and
    /// we've resolved the personality + rolled the PersonalityProfile.
    /// SAIN attaches asynchronously after spawn (typical 1-2s), so the
    /// initial registration usually returns null on the brain lookup.
    /// After <see cref="SainResolveDeadline"/> we lock to Average and stop
    /// polling.
    /// </summary>
    public bool SainResolutionPending;

    /// <summary>
    /// <see cref="Time.time"/> by which the SAIN personality must be
    /// resolved or the squad locks to Average. Defaults to the squad's
    /// first-tick time + the configured retry window (5 s — empirically
    /// enough for SAIN to attach).
    /// </summary>
    public float SainResolveDeadline;

    /// <summary>
    /// Agent.Id of the squad member who landed the most recent killing
    /// blow whose corpse is still "fresh" (not yet looted / claimed by
    /// anyone). When the strategy's own-kill priority pick has promoted
    /// the corpse to <see cref="SquadObjective.Location"/>, UpdateAgents
    /// routes THIS specific agent directly to the corpse instead of
    /// rolling a random roam splinter for them. -1 = no pending kill
    /// credit.
    /// </summary>
    public int PendingOwnKillKillerAgentId = -1;

    /// <summary>
    /// Waypoint.Id of the corpse the <see cref="PendingOwnKillKillerAgentId"/>
    /// flag is currently armed for. Compared against the squad's current
    /// objective so a stale credit (squad anchor has since moved to a
    /// different POI) silently no-ops instead of mis-routing the killer.
    /// 0 = no pending kill credit.
    /// </summary>
    public int PendingOwnKillCorpseLocId;

    /// <summary>
    /// Door instance IDs this squad is authorised to force-unlock on
    /// arrival, bypassing the BSG key-inventory check. Populated when a
    /// picked POI has locked doors on its path: each door is rolled
    /// against an unlock probability (100% if the POI is the squad's
    /// current Main anchor, F12-config probability otherwise).
    /// </summary>
    public readonly HashSet<int> ForceUnlockDoorIds = new();

    /// <summary>
    /// Door instance IDs the squad has previously rolled <em>and lost</em>
    /// the force-unlock dice on. Persists for the rest of the raid.
    /// Read by PickFromCell to filter POIs whose path crosses a failed
    /// door. Entries become inert if another squad unlocks the door
    /// world-wide (door state flips to Shut for everyone → filter no-ops).
    /// </summary>
    public readonly HashSet<int> FailedDoorUnlockIds = new();

    public Agent Leader;

    public int Size
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Members.Count;
    }

    public void AddAgent(Agent member) => Members.Add(member);

    public void RemoveAgent(Agent member) => Members.SwapRemove(member);

    public override string ToString() => $"Squad(id: {Id})";
}
