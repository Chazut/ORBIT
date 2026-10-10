using System;
using System.Collections.Generic;
using Orbit.Entities;
using Orbit.Helpers;
using Orbit.Navigation;
using Orbit.Settings;
using UnityEngine;

namespace Orbit.Systems;

internal sealed class CampFormationSearch : IFrameSearch, IDisposable
{
    internal readonly Squad Squad;
    internal readonly AmbushSite Site;
    internal readonly MainObjective Main;
    internal readonly Dictionary<Agent, CoverPoint> Positions = new();
    private readonly List<Vector3> _occupied = new();
    private readonly List<Agent> _members;
    private readonly List<Vector3> _origins = new();
    private readonly Vector3 _target;
    private readonly WaypointSystem _waypoints;
    private readonly IEnumerator<bool> _steps;
    private readonly AmbushSearchBudget _budget = new();
    private readonly float _started;
    private readonly int _firstFrame;
    private int _slices;
    internal bool Done, Success, DirectLoot, Cancelled;
    internal bool TimedOut => Time.time - _started >= ServerConfig.Ambush.TravelTimeout;
    internal string Rejections => _budget.Rejections;
    internal bool Valid
    {
        get
        {
            var cfg = ServerConfig.Ambush;
            if (!cfg.Allows(Squad.Leader?.BotCategory) || !cfg.For(Site.Kind).Enabled || !AmbushDirector.Available(Squad)
                || ! _waypoints.IsAmbushSiteAvailable(Site, Squad.Leader?.BotCategory)
                || Main != null && !Main.CanPursue(Squad.MainObjectives)
                || Time.time - _started >= cfg.TravelTimeout || (Site.Position - _target).sqrMagnitude > 1f
                || Squad.Size != _members.Count) return false;
            for (var i = 0; i < _members.Count; i++)
                if (!Squad.Members.Contains(_members[i]) || (_members[i].Position - _origins[i]).sqrMagnitude > 100f) return false;
            return true;
        }
    }

    internal CampFormationSearch(WaypointSystem waypoints, Squad squad, AmbushSite site, MainObjective main)
    {
        _waypoints = waypoints; Squad = squad; Site = site; Main = main;
        _target = site.Position; _started = Time.time; _firstFrame = Time.frameCount;
        _members = new List<Agent>(squad.Members);
        foreach (var member in _members) _origins.Add(member.Position);
        _steps = Search().GetEnumerator();
    }

    public bool Step()
    {
        if (!Valid) { Cancelled = true; Dispose(); return false; }
        using var timing = PerformanceJournal.Measure(TransitionPhase.CampSearch, "camp-search", squad: Squad.Id);
        _slices++;
        try
        {
            if (_steps.MoveNext()) return true;
            Dispose();
            if (PerformanceJournal.Enabled)
            {
                var detail = $"squad={Squad.Id} kind={Site.Kind} frames={Time.frameCount - _firstFrame + 1} slices={_slices} success={Success} directLoot={DirectLoot} rejected={Rejections}";
                Log.Info("PERF CAMP SEARCH: " + detail);
                PerformanceJournal.Event("camp-search-complete", Squad.Leader?.Bot?.Profile?.Id, detail, Squad.Id);
            }
            return false;
        }
        catch { Dispose(); throw; }
    }

    private IEnumerable<bool> Search()
    {
        var rule = ServerConfig.Ambush.For(Site.Kind);
        var hasCover = false;
        foreach (var member in _members)
        {
            foreach (var cover in _waypoints.SearchAmbushCover(member, Site, rule, _occupied, _budget, Main?.CampSearchRadius ?? 0))
            {
                if (!cover.HasValue) { yield return true; continue; }
                Positions.Add(member, cover.Value); _occupied.Add(cover.Value.Position);
                hasCover |= cover.Value.Category != CoverCategory.None;
                break;
            }
            if (!Positions.ContainsKey(member)) break;
            yield return true;
        }
        if (Positions.Count == _members.Count && (Site.Kind != CampSiteKind.Airdrop || hasCover))
        { Success = true; yield break; }
        if (Site.Kind != CampSiteKind.Airdrop) yield break;
        Positions.Clear(); _occupied.Clear();
        var budget = new AmbushSearchBudget();
        var canApproach = false;
        DirectLoot = true;
        foreach (var member in _members)
        {
            foreach (var cover in _waypoints.SearchAirdropApproach(member, Site, _occupied, budget))
            {
                if (!cover.HasValue) { yield return true; continue; }
                Positions.Add(member, cover.Value); canApproach = true; break;
            }
            if (!Positions.ContainsKey(member))
                Positions.Add(member, new CoverPoint(member.Position, (Site.Position - member.Position).normalized, CoverCategory.None, CoverLevel.Stay));
            _occupied.Add(Positions[member].Position);
            yield return true;
        }
        Success = canApproach;
    }

    public void Dispose() { if (Done) return; Done = true; _steps.Dispose(); }
}
