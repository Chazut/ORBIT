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

    internal static bool Available(Squad squad)
    {
        if (squad.Leader == null || squad.Size == 0 || squad.ExtractRequested || squad.SainResolutionPending
            || squad.CombatCallerMemberIdx >= 0 || squad.CorpseEscort.Active
            || squad.PreInterruptObjectiveLocation != null || squad.InvestigateNoisePosition.HasValue
            || squad.Objective.Location?.Category is WaypointCategory.Exfil or WaypointCategory.Corpse) return false;
        foreach (var agent in squad.Members)
            if (agent == null || !agent.IsActive || agent.Player?.HealthController?.IsAlive != true
                || agent.SoloExtractRequested || agent.LootExtractSweep != null || CorpseEscort.InFlight(agent)
                || agent.Bot?.Memory?.HaveEnemy == true || agent.Bot?.Memory?.IsUnderFire == true) return false;
        return true;
    }

    internal void Prune(IList<Squad> squads)
    {
        for (var i = _active.Count - 1; i >= 0; i--)
        {
            var squad = _active[i];
            if (!squads.Contains(squad)) squad.Camp.End(squad, "group removed");
            if (!squad.Camp.Active) _active.RemoveAt(i);
        }
    }

    internal bool TryStart(Squad squad)
    {
        var cfg = ServerConfig.Ambush;
        if (squad.Camp.Active || !cfg.Allows(squad.Leader?.BotCategory) || !Available(squad)) return false;
        if (squad.Camp.NextCheck == 0)
        {
            squad.Camp.NextCheck = Time.time + Random.Range(15f, cfg.CheckInterval);
            return false;
        }
        if (Time.time < squad.Camp.NextCheck || Time.time < _nextPlan || _active.Count >= cfg.MaxActiveSquads
            || squad.Camp.Visits >= cfg.MaxCampsPerSquad) return false;
        _nextPlan = Time.time + .5f; // At most one formation plan per half second across all squads.
        squad.Camp.NextCheck = Time.time + cfg.CheckInterval * Random.Range(.8f, 1.2f);
        var first = Random.Range(0, 3);
        var pathBudget = 24;
        for (var k = 0; k < 3; k++)
        {
            var kind = (CampSiteKind)((first + k) % 3);
            var rule = cfg.For(kind);
            if (!rule.Enabled || rule.Chance <= 0 || Random.value >= rule.Chance) continue;
            waypoints.CollectAmbushSites(squad, kind, rule.SearchRadius, _candidates);
            foreach (var site in _candidates)
            {
                var reserved = 0;
                _occupied.Clear();
                foreach (var other in _active)
                {
                    if (!other.Camp.Active) continue;
                    if (other.Camp.Site == site || (other.Camp.Site.Position - site.Position).sqrMagnitude < 2500f) reserved++;
                    foreach (var slot in other.Camp.Slots.Values) _occupied.Add(slot.Position);
                }
                if (reserved >= rule.MaxSquads) continue;
                _positions.Clear();
                foreach (var member in squad.Members)
                {
                    if (!waypoints.TryPickAmbushCover(member, site, rule, _occupied, ref pathBudget, out var cover)) break;
                    _positions.Add(member, cover);
                    _occupied.Add(cover.Position);
                }
                if (_positions.Count == squad.Size)
                {
                    squad.Camp.Begin(squad, site, _positions, waypoints);
                    _active.Add(squad);
                    return true;
                }
                if (pathBudget <= 0) return false;
            }
        }
        return false;
    }
}
