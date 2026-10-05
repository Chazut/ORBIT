using System.Collections.Generic;
using Orbit.Entities;
using Orbit.Helpers;
using Orbit.Navigation;
using Orbit.Settings;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Orbit.Systems;

/// <summary>Holds a main objective or an airdrop detour from separate, stable cover positions.</summary>
internal sealed class CampPlan
{
    internal readonly Dictionary<Agent, Waypoint> Slots = new();
    internal AmbushSite Site;
    internal AmbushSite PendingAirdrop { get; private set; }
    internal float PendingSince { get; private set; }
    internal float FormationRetryAt;
    internal float AirdropCooldownUntil;
    internal AmbushSite AirdropTarget => PendingAirdrop ?? (Site?.Kind == CampSiteKind.Airdrop ? Site : null);
    internal string AirdropStage => AirdropTarget == null ? null : Looter != null ? "loot" : Holding ? "waiting" : "approach";
    internal float Held => _held;
    internal float Duration => _duration;
    internal bool Active => Site != null;
    internal bool Holding { get; private set; }
    internal Agent Looter { get; private set; }
    internal float NextCheck;
    internal readonly HashSet<AmbushSite> VisitedAirdrops = new();
    internal MainObjective Main { get; private set; }
    private float _startedAt, _lastTick, _holdTick, _held, _duration;
    private Waypoint _mission;
    private WaypointSystem _waypoints;
    private float _missionDuration, _lootStartedAt;
    private bool _directLoot;

    internal bool Owns(Agent agent) => Active && Slots.TryGetValue(agent, out var point) && agent.Objective.Location == point;

    internal Orbit.Api.OrbitMainObjective GetAirdropObjective()
    {
        var target = AirdropTarget;
        if (target == null) return null;
        return new Orbit.Api.OrbitMainObjective
        {
            Type = "Airdrop", X = target.Position.x, Y = target.Position.y, Z = target.Position.z,
            AirdropStage = AirdropStage, AirdropLanded = target.Landed,
            AirdropId = target.Drop != null ? target.Drop.GetInstanceID().ToString() : "",
            CampElapsed = Mathf.Min(_held, _duration), CampTargetDuration = _duration, CampHolding = Holding,
        };
    }

    internal void SelectAirdrop(Squad squad, AmbushSite site)
    {
        PendingAirdrop = site;
        PendingSince = FormationRetryAt = _lastTick = Time.time;
        _mission = squad.Objective.Location;
        _held = 0;
        var style = AmbushDirector.Style(squad);
        _duration = Random.Range(style.AirdropDurationMin, style.AirdropDurationMax);
        VisitedAirdrops.Add(site);
        Orbit.Api.OrbitTelemetry.MainObjectivesRevision++;
        Log.Info($"AMBUSH: {squad} selected released airdrop target={site.Position} hold={_duration:F0}s");
    }

    internal void Begin(Squad squad, AmbushSite site, Dictionary<Agent, CoverPoint> positions, WaypointSystem waypoints,
        MainObjective main = null, bool directLoot = false)
    {
        if (main?.Type == MainObjectiveType.ExtractCamp)
            (main.CampApproach ??= new ExtractCampApproach(main)).Release(squad, waypoints);
        Site = site;
        Main = main;
        main?.SetCampState("approach");
        _held = _holdTick = 0;
        Holding = false;
        _directLoot = directLoot && site.Kind == CampSiteKind.Airdrop;
        _mission = squad.Objective.Location;
        _missionDuration = squad.Objective.Duration;
        _waypoints = waypoints;
        _startedAt = _lastTick = Time.time;
        var style = AmbushDirector.Style(squad);
        if (main != null)
        {
            if (main.Type == MainObjectiveType.Kills) main.CampTargetDuration = main.KillsRoamTargetDuration;
            _duration = main.Type == MainObjectiveType.ExtractCamp ? 0 : Mathf.Max(0, main.CampTargetDuration - main.CampElapsed);
        }
        else if (PendingAirdrop != site) _duration = Random.Range(style.AirdropDurationMin, style.AirdropDurationMax);
        if (_directLoot) _duration = 0;
        PendingAirdrop = null;
        if (site.Kind == CampSiteKind.Airdrop) VisitedAirdrops.Add(site);
        foreach (var pair in positions)
        {
            var agent = pair.Key;
            var point = new Waypoint(waypoints.NewRuntimeWaypointId(), WaypointCategory.Synthetic,
                "Ambush_" + site.Kind, pair.Value.Position, 2.25f, new(), new(), null);
            if (agent.Objective.Location != null) waypoints.ReleaseClaim(agent.Objective.Location.Id, agent.Id);
            Slots.Add(agent, point);
            agent.Objective.Location = point;
            agent.Objective.SplinterParent = null;
            agent.Objective.ArrivalPath = null;
            agent.Objective.Status = ObjectiveStatus.None;
            agent.Objective.DispatchTime = Time.time;
            agent.Guard.CoverPoint = pair.Value;
        }
        var hold = main?.Type == MainObjectiveType.ExtractCamp ? "until-time-extract" : $"{_duration:F0}s";
        Log.Info($"AMBUSH: {squad} approach kind={site.Kind} target={site.Position} members={Slots.Count} hold={hold}");
        if (_directLoot) Log.Info($"AMBUSH: {squad} direct airdrop loot reason=no reachable cover landed={site.Landed}");
        if (site.Kind == CampSiteKind.Airdrop) Orbit.Api.OrbitTelemetry.MainObjectivesRevision++;
    }

    internal bool Tick(Squad squad, WaypointSystem waypoints)
    {
        if (!Active) return false;
        PauseMission(squad);
        var cfg = ServerConfig.Ambush;
        var reason = !cfg.Allows(squad.Leader?.BotCategory) || !cfg.For(Site.Kind).Enabled ? "disabled"
            : !AmbushDirector.Available(squad) ? "combat, extraction or member unavailable"
            : squad.Objective.Location != _mission ? "mission changed"
            : !waypoints.IsAmbushSiteAvailable(Site, squad.Leader.BotCategory) ? "target unavailable" : null;
        if (reason != null) { End(squad, reason); return false; }
        // A late member needs a new formation. Release the detour instead of leaving it behind.
        if (Slots.Count != squad.Size) { End(squad, "group changed"); return false; }
        // Cover failure removes the ambush delay, including when the crate lands during approach.
        if (_directLoot && Looter == null && Site.Landed && !BeginLoot(squad, waypoints))
        { End(squad, "direct airdrop loot unavailable"); return false; }
        var arrived = false;
        foreach (var pair in Slots)
        {
            var agent = pair.Key;
            if (!squad.Members.Contains(agent) || !Owns(agent) || agent.Objective.Status == ObjectiveStatus.Failed)
            { End(squad, "route failed or assignment changed"); return false; }
            if (agent == Looter)
            {
                if (agent.Objective.Status is ObjectiveStatus.Finished or ObjectiveStatus.Failed)
                { End(squad, "airdrop loot completed"); return false; }
                if (Time.time - _lootStartedAt >= cfg.TravelTimeout && !CorpseEscort.InFlight(agent))
                { End(squad, "airdrop loot approach timeout"); return false; }
                continue;
            }
            if (agent.Objective.Status == ObjectiveStatus.Finished)
            {
                if ((agent.Position - pair.Value.Position).sqrMagnitude > 16f)
                { End(squad, "member displaced"); return false; }
                arrived = true;
            }
            else if (Time.time - _startedAt >= cfg.TravelTimeout)
            { End(squad, "travel timeout"); return false; }
        }
        if (!Holding && arrived)
        {
            Holding = true;
            _holdTick = Time.time;
            if (Main != null)
            {
                if (Main.CampStartedAt <= 0) Main.CampStartedAt = Time.time;
                Main.SetCampState("holding");
                if (Main.Type == MainObjectiveType.Kills && Main.KillsRoamStartedAt <= 0) Main.KillsRoamStartedAt = Time.time;
                Orbit.Api.OrbitTelemetry.MainObjectivesRevision++;
            }
            var hold = Main?.Type == MainObjectiveType.ExtractCamp ? "until-time-extract" : $"{_duration:F0}s";
            Log.Info($"AMBUSH: {squad} holding kind={Site.Kind} duration={hold}");
            if (Site.Kind == CampSiteKind.Airdrop) Orbit.Api.OrbitTelemetry.MainObjectivesRevision++;
        }
        if (Looter != null) return true;
        if (Holding && arrived)
        {
            var elapsed = Mathf.Max(0, Time.time - _holdTick);
            _holdTick = Time.time;
            _held += elapsed;
            if (Main != null) Main.CampElapsed += elapsed;
        }
        if (Holding && Main?.Type != MainObjectiveType.ExtractCamp && _held >= _duration)
        {
            // The hold can finish during the descent. Keep cover until the landing callback arrives.
            if (Site.Kind == CampSiteKind.Airdrop && !Site.Landed) return true;
            if (Site.Kind == CampSiteKind.Airdrop && BeginLoot(squad, waypoints)) return true;
            if (Main != null)
            {
                Main.Completed = true;
                Orbit.Api.OrbitTelemetry.MainObjectivesRevision++;
                Log.Info($"AMBUSH: {squad} main={Main.Type} completed hold={Main.CampElapsed:F0}s");
            }
            End(squad, "duration completed");
            return false;
        }
        return true;
    }

    private bool BeginLoot(Squad squad, WaypointSystem waypoints)
    {
        var checkedMembers = 0;
        foreach (var member in squad.Members)
        {
            if (++checkedMembers > 4) break;
            if (!waypoints.TryGetAmbushLoot(member, Site, out var target)
                || !waypoints.TryClaim(target.Id, member.Id)) continue;
            Looter = member;
            _lootStartedAt = Time.time;
            Slots[member] = target;
            member.Objective.Location = target;
            member.Objective.SplinterParent = null;
            member.Objective.ArrivalPath = null;
            member.Objective.Status = ObjectiveStatus.None;
            member.Objective.DispatchTime = Time.time;
            member.Guard.CoverPoint = null;
            member.Look.Target = null;
            Log.Info($"AMBUSH: {squad} looting airdrop looter={member} target={target}");
            Orbit.Api.OrbitTelemetry.MainObjectivesRevision++;
            return true;
        }
        return false;
    }

    internal void PauseMission(Squad squad)
    {
        var elapsed = Mathf.Max(0f, Time.time - _lastTick);
        _lastTick = Time.time;
        if (squad.Objective.Location == _mission) squad.Objective.StartTime += elapsed;
        if (squad.MainObjectives == null) return;
        foreach (var main in squad.MainObjectives)
        {
            if (main.Completed) continue;
            if (main == Main) continue;
            if (main.KillsRoamStartedAt > 0) main.KillsRoamStartedAt += elapsed;
            main.KillsFloorLastTick = 0;
            main.LootValueLastEngagedAt = 0;
        }
    }

    internal void End(Squad squad, string reason)
    {
        if (PendingAirdrop != null)
        {
            PauseMission(squad);
            PendingAirdrop = null;
            Orbit.Api.OrbitTelemetry.MainObjectivesRevision++;
            Log.Info($"AMBUSH: {squad} cancelled released airdrop reason={reason}");
        }
        if (!Active) return;
        PauseMission(squad);
        Log.Info($"AMBUSH: {squad} ended kind={Site.Kind} reason={reason}");
        foreach (var pair in Slots)
        {
            var agent = pair.Key;
            if (agent.Objective.Location == pair.Value)
            {
                agent.Objective.Location = null;
                agent.Objective.SplinterParent = null;
                agent.Objective.Status = ObjectiveStatus.None;
                agent.Guard.CoverPoint = null;
                agent.Look.Target = null;
            }
            _waypoints.ReleaseClaim(pair.Value.Id, agent.Id);
            if (agent == Looter) continue;
            squad.CompletedPoiIds.Remove(pair.Value.Id);
            squad.RecentlyVisitedPoiCooldowns.Remove(pair.Value.Id);
            agent.ArrivalFailures.Forget(pair.Value.Id);
        }
        if (squad.Objective.Location == _mission) squad.Objective.Duration = Main?.Completed == true ? 0 : _missionDuration;
        if (Main != null)
        {
            Main.SetCampState(Main.Completed ? "completed" : "paused", Main.Completed ? null : reason);
            if (Main.Type == MainObjectiveType.ExtractCamp && reason is ("route failed or assignment changed" or "travel timeout" or "member displaced"))
                Main.CampApproach?.Retry(squad, _waypoints, reason);
            if (!Main.Completed) { Main.CampSearchAttempt = 0; Main.CampSearchRadius = 0; }
            Main.CampRetryAt = Time.time + 5f;
            Orbit.Api.OrbitTelemetry.MainObjectivesRevision++;
        }
        if (Site.Kind == CampSiteKind.Airdrop) Orbit.Api.OrbitTelemetry.MainObjectivesRevision++;
        Slots.Clear();
        Looter = null;
        Site = null;
        Main = null;
        Holding = false;
        _directLoot = false;
        NextCheck = Time.time + ServerConfig.Ambush.Cooldown;
        AirdropCooldownUntil = NextCheck;
    }
}
