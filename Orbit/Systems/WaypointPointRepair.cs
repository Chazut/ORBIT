using System;
using System.Collections.Generic;
using Orbit.Entities;
using Orbit.Helpers;
using Orbit.Navigation;
using Orbit.Settings;
using UnityEngine;

namespace Orbit.Systems;

public partial class WaypointSystem
{
    // A WaypointSystem belongs to one raid/map variant. Only successful positions are shared.
    // A disconnected actor or temporarily closed route cannot poison another squad's search.
    private readonly Dictionary<(string Id, Vector3 Seed, string Part), Vector3> _pointRepairs = new();
    private readonly HashSet<(string Id, Vector3 Seed, string Part, string Outcome)> _pointReports = new();

    internal bool RepairPoint(RushPoint site, string part, Vector3 authored, Vector3 seed, Agent actor,
        Func<Vector3, bool> scope, ref AuthoredPointSearch search, out Vector3? result)
    {
        var key = (site.Id, authored, part);
        result = null;
        if (search == null)
        {
            search = new AuthoredPointSearch(seed, site.Radius, scope);
            if (_pointRepairs.TryGetValue(key, out var cached))
            {
                if (search.Reachable(actor.Position, cached)) { result = cached; return true; }
                return false;
            }
        }
        if (!search.Step(actor.Position)) return false;
        if (search.Outcome == "corrected")
        {
            result = search.Result;
            _pointRepairs[key] = result.Value;
        }
        if ((Log.InfoEnabled || PerformanceJournal.Enabled) && _pointReports.Add((site.Id, authored, part, search.Outcome)))
        {
            var detail = $"map={_zoneKey} kind={site.Kind} site={site.Name} id={site.Id} part={part} state={search.Outcome}"
                + $" configured={authored} seed={seed} resolved={(result.HasValue ? result.Value.ToString() : "none")} floor={site.FloorId ?? "auto"} samples={search.Samples} paths={search.Paths}"
                + $" outsideScope={search.RejectedScope} blockedSpace={search.BlockedSpace} incompletePath={search.IncompletePath} shortPath={search.ShortPath}";
            Log.Info($"RUSH POINT REPAIR: {detail}");
            PerformanceJournal.Event("rush-point-repair", actor.Bot.Profile.Id, detail, actor.Squad.Id);
        }
        return true;
    }

    internal bool RepairFloor(RushPoint site, Vector3 point) => MatchesZoneFloor(site.FloorId, point);
}
