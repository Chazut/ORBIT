using System;
using System.Collections.Generic;
using EFT;
using EFT.Interactive;
using Orbit.Entities;
using Orbit.Helpers;
using Orbit.Navigation;
using Orbit.Settings;
using Orbit.Zones;
using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Systems;

internal sealed class AmbushSite
{
    internal CampSiteKind Kind;
    internal Vector3 Position;
    internal ZoneScope Scope;
    internal Waypoint Exfil;
    internal LootableContainer Drop;
    internal float ExpiresAt;
    internal int Generation;
    internal readonly List<CoverPoint> Covers = new();
    internal bool CoversReady;
}

public partial class WaypointSystem
{
    private readonly List<AmbushSite> _campSites = new();
    private readonly List<CoverPoint> _campCoverScratch = new();
    private readonly NavMeshPath _campPath = new();
    private bool _campCatalogReady;
    private int _campGeneration;

    internal void InvalidateAmbushHotspots()
    {
        _campGeneration++;
        _campCatalogReady = false;
        _campSites.RemoveAll(site => site.Kind == CampSiteKind.Hotspot);
    }

    internal void RegisterAmbushAirdrop(LootableContainer container)
    {
        if (container == null || !ServerConfig.Ambush.Enabled) return;
        foreach (var site in _campSites) if (site.Drop == container) return;
        // Landing callbacks, never a per-bot scene scan. Limit stale drops to this raid's recent events.
        _campSites.RemoveAll(site => site.Kind == CampSiteKind.Airdrop && (site.Drop == null || Time.time >= site.ExpiresAt));
        if (_campSites.Count > 512) return;
        _campSites.Add(new AmbushSite { Kind = CampSiteKind.Airdrop, Position = container.transform.position,
            Drop = container, ExpiresAt = Time.time + 1200f });
        Log.Info($"AMBUSH: airdrop landed at {container.transform.position}");
    }

    private void BuildAmbushCatalog()
    {
        if (_campCatalogReady) return;
        _campCatalogReady = true;
        // Static exfils are indexed once. Availability is checked again before selection and while camping.
        foreach (var cell in _cells)
            foreach (var point in cell.Waypoints)
                if (point.Category == WaypointCategory.Exfil)
                {
                    var known = false;
                    foreach (var site in _campSites) if (site.Exfil == point) { known = true; break; }
                    if (!known) _campSites.Add(new AmbushSite { Kind = CampSiteKind.Extract, Position = point.Position, Exfil = point });
                }
        foreach (var zone in _zones)
        {
            if (zone.Force <= 0 || ServerConfig.Zones.ZoneForceScale <= 0) continue;
            // Use a real point on the zone's selected floor, rather than sampling a 2D center at Y=0.
            var center = WorldToCell(zone.WorldPosition);
            var radius = Mathf.Min(150f, zone.Radius * ServerConfig.Zones.ZoneRadiusScale);
            var window = Mathf.CeilToInt(radius / _cellSize);
            Waypoint nearest = null;
            var best = radius * radius;
            for (var x = Math.Max(0, center.x - window); x <= Math.Min(_gridSize.x - 1, center.x + window); x++)
            for (var y = Math.Max(0, center.y - window); y <= Math.Min(_gridSize.y - 1, center.y + window); y++)
                foreach (var point in _cells[x, y].Waypoints)
                {
                    if (point.Category is WaypointCategory.Exfil or WaypointCategory.Corpse) continue;
                    if (!MatchesZoneFloor(zone.Scope?.FloorId, point.Position)) continue;
                    var distance = XzDistanceSqr(point.Position, zone.WorldPosition);
                    if (distance >= best) continue;
                    best = distance;
                    nearest = point;
                }
            if (nearest != null) _campSites.Add(new AmbushSite { Kind = CampSiteKind.Hotspot, Position = nearest.Position,
                Scope = zone.Scope, Generation = _campGeneration });
        }
    }

    internal bool IsAmbushSiteAvailable(AmbushSite site, string botType)
    {
        if (site == null) return false;
        if (site.Kind == CampSiteKind.Hotspot)
            return site.Generation == _campGeneration && (site.Scope == null || site.Scope.Allows(botType));
        if (site.Kind == CampSiteKind.Airdrop) return site.Drop != null && Time.time < site.ExpiresAt;
        if (site.Exfil?.Target is not ExfiltrationPoint exit) return false;
        // Camping is distinct from extracting: an enemy's active exit is a valid ambush target.
        return exit.Status is EExfiltrationStatus.RegularMode or EExfiltrationStatus.Countdown;
    }

    internal void CollectAmbushSites(Squad squad, CampSiteKind kind, float radius, List<AmbushSite> result)
    {
        BuildAmbushCatalog();
        result.Clear();
        var origin = squad.Leader.Position;
        foreach (var site in _campSites)
        {
            if (site.Kind != kind || !IsAmbushSiteAvailable(site, squad.Leader.BotCategory)
                || XzDistanceSqr(origin, site.Position) > radius * radius) continue;
            // Keep the four nearest targets without allocating/sorting a map-sized candidate list.
            var index = 0;
            while (index < result.Count && XzDistanceSqr(origin, result[index].Position) <= XzDistanceSqr(origin, site.Position)) index++;
            if (index >= 4) continue;
            result.Insert(index, site);
            if (result.Count > 4) result.RemoveAt(4);
        }
    }

    internal bool TryPickAmbushCover(Agent agent, AmbushSite site, CampSiteSettings rule, List<Vector3> occupied,
        ref int pathBudget, out CoverPoint chosen)
    {
        if (!site.CoversReady)
        {
            site.CoversReady = true;
            // Bounded local cover queries, cached per target for the raid.
            var radius = (rule.DistanceMin + rule.DistanceMax) * .5f;
            for (var i = 0; i < 8 && site.Covers.Count < 128 && pathBudget > 0; i++)
            {
                pathBudget--;
                var angle = i * Mathf.PI / 4f;
                var sample = site.Position + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * radius;
                CollectCorpseEscortCover(sample, _campCoverScratch);
                foreach (var cover in _campCoverScratch)
                    if (!site.Covers.Contains(cover) && site.Covers.Count < 128) site.Covers.Add(cover);
            }
        }
        for (var category = CoverCategory.Hard; category <= CoverCategory.Soft; category++)
            for (var i = 0; i < site.Covers.Count && pathBudget > 0; i++)
            {
                var cover = site.Covers[i];
                if (cover.Category != category) continue;
                if (ValidateAmbushPosition(agent, site, rule, cover.Position, occupied, ref pathBudget, out var position))
                { chosen = new CoverPoint(position, cover.Direction, cover.Category, cover.Level); return true; }
            }
        if (!ServerConfig.Ambush.RequireCover)
            for (var i = 0; i < 12 && pathBudget > 0; i++)
            {
                var angle = i * Mathf.PI / 6f;
                var radius = (rule.DistanceMin + rule.DistanceMax) * .5f;
                var sample = site.Position + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * radius;
                if (ValidateAmbushPosition(agent, site, rule, sample, occupied, ref pathBudget, out var position))
                { chosen = new CoverPoint(position, (site.Position - position).normalized, CoverCategory.None, CoverLevel.Stay); return true; }
            }
        chosen = default;
        return false;
    }

    private bool ValidateAmbushPosition(Agent agent, AmbushSite site, CampSiteSettings rule, Vector3 candidate,
        List<Vector3> occupied, ref int budget, out Vector3 position)
    {
        position = candidate;
        var delta = candidate - site.Position;
        if (Mathf.Abs(delta.y) > 2f || delta.sqrMagnitude < rule.DistanceMin * rule.DistanceMin
            || delta.sqrMagnitude > rule.DistanceMax * rule.DistanceMax || !MatchesZoneFloor(site.Scope?.FloorId, candidate)) return false;
        var spacing = ServerConfig.Ambush.MemberSpacing;
        foreach (var used in occupied) if ((used - candidate).sqrMagnitude < spacing * spacing) return false;
        budget--;
        if (!NavMesh.SamplePosition(candidate, out var hit, .75f, NavMesh.AllAreas)
            || (hit.position - candidate).sqrMagnitude > .25f) return false;
        delta = hit.position - site.Position;
        if (Mathf.Abs(delta.y) > 2f || delta.sqrMagnitude < rule.DistanceMin * rule.DistanceMin
            || delta.sqrMagnitude > rule.DistanceMax * rule.DistanceMax || !MatchesZoneFloor(site.Scope?.FloorId, hit.position)) return false;
        foreach (var used in occupied) if ((used - hit.position).sqrMagnitude < spacing * spacing) return false;
        if (!NavMesh.CalculatePath(agent.Position, hit.position, NavMesh.AllAreas, _campPath)
            || _campPath.status != NavMeshPathStatus.PathComplete
            || PathHelper.TotalLength(_campPath.corners) > rule.SearchRadius * 1.5f + 50f) return false;
        // Require a sightline toward the target; a closed room behind it is not a useful ambush position.
        if (Physics.Linecast(hit.position + Vector3.up * 1.4f, site.Position + Vector3.up * 1.2f,
                LayersMaskController.HighPolyWithTerrainMask)) return false;
        position = hit.position;
        return true;
    }
}
