using System.Collections.Generic;
using Orbit.Entities;
using Orbit.Helpers;
using Orbit.Navigation;
using Orbit.Settings;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Orbit.Systems;

internal sealed class AmbushDirector(WaypointSystem waypoints)
{
    private readonly List<Squad> _active = new();
    private readonly List<AmbushSite> _candidates = new();
    private readonly List<Vector3> _occupied = new();
    private readonly Dictionary<Agent, CoverPoint> _positions = new();
    private float _nextPlan;

    internal static bool Available(Squad squad) => UnavailableReason(squad) == null;

    private static string UnavailableReason(Squad squad)
    {
        if (squad.Leader == null || squad.Size == 0) return "no leader or members";
        if (squad.Operation?.Active == true) return "multi-step active";
        if (MainObjective.HasPendingRush(squad.MainObjectives)) return "rush pending";
        if (squad.ExtractRequested) return "extract requested";
        if (squad.SainResolutionPending) return "personality unresolved";
        if (squad.CombatCallerMemberIdx >= 0 || Time.time < squad.GhostFightUntil) return "combat";
        if (squad.CorpseEscort.Active) return "corpse escort";
        if (squad.PreInterruptObjectiveLocation != null) return "interrupted objective";
        if (squad.InvestigateNoisePosition.HasValue) return "investigating noise";
        if (squad.Objective.Location?.Category is WaypointCategory.Exfil or WaypointCategory.Corpse) return "extract or corpse objective";
        foreach (var agent in squad.Members)
        {
            if (agent == null || !agent.IsActive || agent.Player?.HealthController?.IsAlive != true) return "member unavailable";
            if (agent.SoloExtractRequested || agent.LootExtractSweep != null) return "member extracting";
            if (CorpseEscort.InFlight(agent) && squad.Camp.Looter != agent) return "member looting";
            if (agent.Bot?.Memory?.HaveEnemy == true || agent.Bot?.Memory?.IsUnderFire == true) return "member in combat";
        }
        return null;
    }

    private static void Decision(Squad squad, AmbushSite site, string source, string reason, float? roll = null)
    {
        if (!Log.InfoEnabled && !PerformanceJournal.Enabled) return;
        var distance = -1f;
        if (site != null && squad.Leader != null)
        { var delta = squad.Leader.Position - site.Position; distance = Mathf.Sqrt(delta.x * delta.x + delta.z * delta.z); }
        var detail = $"source={source} reason={reason} leader={squad.Leader} members={squad.Size} style={squad.Personality?.Archetype} chance={Style(squad).AirdropChance:F3} roll={(roll.HasValue ? roll.Value.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) : "none")} distance={distance:F1}m radius={ServerConfig.Ambush.Airdrops.SearchRadius:F0}m target={site?.Position}";
        Log.Info($"AIRDROP DECISION: {squad} {detail}");
        PerformanceJournal.Event("airdrop-decision", squad.Leader?.Bot?.Profile?.Id, detail, squad.Id);
    }

    internal void Prune(IList<Squad> squads)
    {
        for (var i = _active.Count - 1; i >= 0; i--)
        {
            var squad = _active[i];
            if (!squads.Contains(squad)) squad.Camp.End(squad, "group removed");
            if (!squad.Camp.Active && squad.Camp.PendingAirdrop == null) _active.RemoveAt(i);
        }
    }

    internal static AmbushStyleSettings Style(Squad squad)
        => ServerConfig.Ambush.Style(squad.Personality?.Archetype.ToString(), squad.Leader?.BotCategory == "PlayerScav");

    internal void OnAirdropReleased(AmbushSite site, IList<Squad> squads)
    {
        var cfg = ServerConfig.Ambush;
        if (site == null) return;
        // Decide at the release event. Expensive cover/path work remains paced by TryStartPending.
        foreach (var squad in squads)
        {
            var reason = !cfg.Airdrops.Enabled ? "airdrops disabled"
                : !cfg.Allows(squad.Leader?.BotCategory) ? "category disabled"
                : squad.Camp.Active || squad.Camp.PendingAirdrop != null ? "camp already assigned"
                : UnavailableReason(squad)
                    ?? (Time.time < squad.Camp.AirdropCooldownUntil ? "cooldown" : null)
                    ?? (squad.Camp.VisitedAirdrops.Contains(site) ? "already visited" : null);
            if (reason != null) { Decision(squad, site, "release", reason); continue; }
            var delta = squad.Leader.Position - site.Position;
            if (delta.x * delta.x + delta.z * delta.z > cfg.Airdrops.SearchRadius * cfg.Airdrops.SearchRadius)
            { Decision(squad, site, "release", "out of range"); continue; }
            var roll = Random.value;
            if (roll >= Style(squad).AirdropChance) { Decision(squad, site, "release", "roll rejected", roll); continue; }
            Decision(squad, site, "release", "selected", roll);
            squad.Camp.SelectAirdrop(squad, site);
            if (!_active.Contains(squad)) _active.Add(squad);
        }
    }

    internal bool TryStartPending(Squad squad)
    {
        var site = squad.Camp.PendingAirdrop;
        if (site == null) return false;
        squad.Camp.PauseMission(squad);
        if (!ServerConfig.Ambush.Allows(squad.Leader?.BotCategory) || !ServerConfig.Ambush.Airdrops.Enabled
            || !Available(squad) || !waypoints.IsAmbushSiteAvailable(site, squad.Leader?.BotCategory)
            || Time.time - squad.Camp.PendingSince >= ServerConfig.Ambush.TravelTimeout)
        {
            squad.Camp.End(squad, "unavailable or formation timeout");
            return false;
        }
        if (Time.time < _nextPlan || Time.time < squad.Camp.FormationRetryAt) return true;
        _nextPlan = Time.time + .5f;
        squad.Camp.FormationRetryAt = Time.time + 5f;
        if (!TryFormation(squad, site, null) && squad.Camp.PendingAirdrop != null)
            Log.Debug($"AMBUSH: {squad} released airdrop waiting for reachable cover at {site.Position}");
        return squad.Camp.Active || squad.Camp.PendingAirdrop != null;
    }

    internal bool TryStart(Squad squad)
    {
        if (squad.Camp.PendingAirdrop != null) return TryStartPending(squad);
        var cfg = ServerConfig.Ambush;
        if (squad.Camp.Active || !cfg.Allows(squad.Leader?.BotCategory)) return false;
        var unavailable = UnavailableReason(squad);
        if (unavailable != null)
        {
            if (cfg.Airdrops.Enabled && Time.time >= squad.Camp.NextAirdropDiagnostic)
            {
                squad.Camp.NextAirdropDiagnostic = Time.time + cfg.CheckInterval;
                Decision(squad, null, "periodic", unavailable);
            }
            return false;
        }
        if (Time.time < _nextPlan) return false;

        // A main owns both its target and its mode. No periodic roll can turn another zone into a hotspot camp.
        MainObjective pending = null;
        var nearest = float.MaxValue;
        if (squad.MainObjectives != null)
            foreach (var main in squad.MainObjectives)
            {
                if (!main.CanPursue(squad.MainObjectives) || !main.IsCampMain || Time.time < main.CampRetryAt) continue;
                var rule = cfg.For(main.Type == MainObjectiveType.ExtractCamp ? CampSiteKind.Extract : CampSiteKind.Hotspot);
                var delta = main.Position - squad.Leader.Position;
                var distance = delta.x * delta.x + delta.z * delta.z;
                if (!rule.Enabled || distance > rule.SearchRadius * rule.SearchRadius || distance >= nearest) continue;
                nearest = distance;
                pending = main;
            }
        if (pending != null)
        {
            // Finish the staged approach before consuming the local cover attempts.
            var extract = pending.Type == MainObjectiveType.ExtractCamp;
            var rule = cfg.For(extract ? CampSiteKind.Extract : CampSiteKind.Hotspot);
            if (extract && Vector3.Distance(squad.Leader.Position, pending.Position) > rule.DistanceMax + 10f) return false;
            _nextPlan = Time.time + .5f;
            pending.CampRetryAt = Time.time + 15f;
            pending.CampSite ??= waypoints.CreateKillMainCampSite(pending);
            if (extract)
            {
                pending.CampSearchAttempt++;
                pending.CampSearchRadius = rule.DistanceMax * (1f + .5f * (pending.CampSearchAttempt - 1));
                pending.SetCampState("searching cover");
                Orbit.Api.OrbitTelemetry.MainObjectivesRevision++;
            }
            waypoints.BeginAmbushSearch();
            if (pending.CampSite != null && TryFormation(squad, pending.CampSite, pending)) return true;
            if (extract)
            {
                Log.Info($"AMBUSH SEARCH: {squad} attempt={pending.CampSearchAttempt}/4 radius={pending.CampSearchRadius:F1}m rejected={waypoints.AmbushRejections}");
                if (pending.CampSearchAttempt >= 4)
                    (pending.CampApproach ??= new ExtractCampApproach(pending)).Fail(squad, waypoints,
                        "cover search exhausted: " + waypoints.AmbushRejections);
            }
            Log.Debug($"AMBUSH: {squad} main={pending.Type} waiting for reachable cover at {pending.Position}");
            return false;
        }

        // Later opportunities still use periodic checks, including squads entering range after release.
        if (!cfg.Airdrops.Enabled) return false;
        if (squad.Camp.NextCheck == 0)
        {
            squad.Camp.NextCheck = Time.time + Random.Range(15f, cfg.CheckInterval);
            return false;
        }
        if (Time.time < squad.Camp.NextCheck) return false;
        squad.Camp.NextCheck = Time.time + cfg.CheckInterval * Random.Range(.8f, 1.2f);
        waypoints.CollectAmbushSites(squad, CampSiteKind.Airdrop, cfg.Airdrops.SearchRadius, _candidates);
        _candidates.RemoveAll(site => squad.Camp.VisitedAirdrops.Contains(site));
        if (_candidates.Count == 0) { Decision(squad, null, "periodic", "no unvisited candidate in range"); return false; }
        var roll = Random.value;
        if (roll >= Style(squad).AirdropChance) { Decision(squad, null, "periodic", "roll rejected", roll); return false; }
        _nextPlan = Time.time + .5f;
        // One bounded formation attempt per opportunity, irrespective of nearby drop count.
        var selected = _candidates[Random.Range(0, _candidates.Count)];
        var started = TryFormation(squad, selected, null);
        Decision(squad, selected, "periodic", started ? "selected" : "formation unavailable", roll);
        return started;
    }

    private bool TryFormation(Squad squad, AmbushSite site, MainObjective main)
    {
        if (!waypoints.IsAmbushSiteAvailable(site, squad.Leader.BotCategory)) return false;
        var rule = ServerConfig.Ambush.For(site.Kind);
        var pathBudget = 24;
        _occupied.Clear();
        _positions.Clear();
        // Spacing belongs to this formation only. Other squads never reserve a target or its surroundings.
        foreach (var member in squad.Members)
        {
            if (!waypoints.TryPickAmbushCover(member, site, rule, _occupied, ref pathBudget, out var cover,
                    main?.CampSearchRadius ?? 0)) break;
            _positions.Add(member, cover);
            _occupied.Add(cover.Position);
        }
        if (_positions.Count != squad.Size)
        {
            if (site.Kind != CampSiteKind.Airdrop) return false;
            return TryDirectAirdrop(squad, site);
        }
        // Even when cover is optional, an entirely uncovered airdrop formation should just loot.
        if (site.Kind == CampSiteKind.Airdrop)
        {
            var hasCover = false;
            foreach (var position in _positions.Values) hasCover |= position.Category != CoverCategory.None;
            if (!hasCover) return TryDirectAirdrop(squad, site);
        }
        squad.Camp.Begin(squad, site, _positions, waypoints, main);
        if (!_active.Contains(squad)) _active.Add(squad);
        return true;
    }

    private bool TryDirectAirdrop(Squad squad, AmbushSite site)
    {
        _positions.Clear();
        _occupied.Clear();
        var pathBudget = 24;
        var canApproach = false;
        foreach (var member in squad.Members)
        {
            // Approach the landing area without requiring an ambush sightline or cover.
            if (waypoints.TryPickAirdropApproach(member, site, _occupied, ref pathBudget, out var position))
                canApproach = true;
            else
                position = new CoverPoint(member.Position, (site.Position - member.Position).normalized,
                    CoverCategory.None, CoverLevel.Stay);
            _positions.Add(member, position);
            _occupied.Add(position.Position);
        }
        if (!canApproach) return false;
        squad.Camp.Begin(squad, site, _positions, waypoints, directLoot: true);
        if (!_active.Contains(squad)) _active.Add(squad);
        // A landed crate can be claimed immediately, while the other members approach.
        return squad.Camp.Tick(squad, waypoints);
    }
}
