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
    private readonly Dictionary<Squad, CampFormationSearch> _formations = new();
    private readonly List<Squad> _expiredFormations = new();
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
        _expiredFormations.Clear();
        foreach (var pair in _formations)
            if (!squads.Contains(pair.Key) || !pair.Value.Valid) _expiredFormations.Add(pair.Key);
        foreach (var squad in _expiredFormations) CancelFormation(squad);

        for (var i = _active.Count - 1; i >= 0; i--)
        {
            var squad = _active[i];
            if (!squads.Contains(squad)) squad.Camp.End(squad, "group removed");
            if (!squad.Camp.Active && squad.Camp.PendingAirdrop == null && squad.Camp.RetryMain == null) _active.RemoveAt(i);
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
                : squad.Camp.Active || squad.Camp.PendingAirdrop != null || _formations.ContainsKey(squad) ? "camp already assigned"
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
            CancelFormation(squad);
            squad.Camp.End(squad, "unavailable or formation timeout");
            return false;
        }
        if (_formations.ContainsKey(squad)) return ContinueFormation(squad) || squad.Camp.PendingAirdrop != null;
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
        if (squad.Camp.Active) return false;
        if (!cfg.Allows(squad.Leader?.BotCategory))
        { squad.Camp.CancelMainRetry(); CancelFormation(squad); return false; }
        var unavailable = UnavailableReason(squad);
        if (unavailable != null)
        {
            squad.Camp.CancelMainRetry();
            if (cfg.Airdrops.Enabled && Time.time >= squad.Camp.NextAirdropDiagnostic)
            {
                squad.Camp.NextAirdropDiagnostic = Time.time + cfg.CheckInterval;
                Decision(squad, null, "periodic", unavailable);
            }
            return false;
        }
        var wasRetrying = squad.Camp.RetryMain != null;
        var retry = squad.Camp.GetMainRetry(squad);
        if (wasRetrying && retry == null) CancelFormation(squad);
        if (_formations.ContainsKey(squad)) return ContinueFormation(squad);
        if (Time.time < _nextPlan || retry != null && Time.time < retry.CampRetryAt) return retry != null;

        // A main owns both its target and its mode. No periodic roll can turn another zone into a hotspot camp.
        MainObjective pending = retry;
        var nearest = float.MaxValue;
        if (pending == null && squad.MainObjectives != null)
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
            return pending.CampSite != null && TryFormation(squad, pending.CampSite, pending);
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
        var search = new CampFormationSearch(waypoints, squad, site, main);
        _formations.Add(squad, search); waypoints.ScheduleFrameSearch(search);
        return ContinueFormation(squad);
    }

    private void CancelFormation(Squad squad)
    {
        if (!_formations.Remove(squad, out var search)) return;
        waypoints.CancelFrameSearch(search); search.Dispose();
        // An interrupted calculation is not an exhausted cover attempt.
        if (search.Main != null)
        {
            if (search.Main.Type == MainObjectiveType.ExtractCamp)
                search.Main.CampSearchAttempt = System.Math.Max(0, search.Main.CampSearchAttempt - 1);
            search.Main.CampRetryAt = Time.time + 1;
        }
    }

    private bool ContinueFormation(Squad squad)
    {
        var search = _formations[squad];
        if (!search.Valid || search.Cancelled) { CancelFormation(squad); return false; }
        waypoints.PumpFrameSearches();
        if (search.Cancelled || !search.Valid) { CancelFormation(squad); return false; }
        if (!search.Done) return true;
        _formations.Remove(squad); waypoints.CancelFrameSearch(search);
        if (search.Success)
        {
            squad.Camp.Begin(squad, search.Site, search.Positions, waypoints, search.Main, search.DirectLoot);
            if (!_active.Contains(squad)) _active.Add(squad);
            return !search.DirectLoot || squad.Camp.Tick(squad, waypoints);
        }
        var main = search.Main;
        if (main != null && squad.Camp.RetryMain == main)
        {
            squad.Camp.RetryMainAfterFailure(squad, main, "cover search exhausted: " + search.Rejections);
            return squad.Camp.RetryMain != null;
        }
        if (main?.Type == MainObjectiveType.ExtractCamp)
        {
            Log.Info($"AMBUSH SEARCH: {squad} attempt={main.CampSearchAttempt}/4 radius={main.CampSearchRadius:F1}m rejected={search.Rejections}");
            if (main.CampSearchAttempt >= 4)
                (main.CampApproach ??= new ExtractCampApproach(main)).Fail(squad, waypoints, "cover search exhausted: " + search.Rejections);
        }
        if (main != null)
            Log.Debug($"AMBUSH: {squad} main={main.Type} waiting for reachable cover at {main.Position}");
        else Decision(squad, search.Site, squad.Camp.PendingAirdrop != null ? "release" : "periodic", "formation unavailable");
        return false;
    }
}
