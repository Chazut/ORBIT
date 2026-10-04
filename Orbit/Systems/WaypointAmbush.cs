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
    internal Vector3 ZoneCenter;
    internal float ZoneRadius;
    internal Waypoint Exfil;
    internal LootableContainer Drop;
    internal bool Landed;
    internal Waypoint LootWaypoint;
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
        foreach (var site in _campSites)
            if (site.Drop == container)
            {
                if (site.Landed) return;
                site.Landed = true;
                site.Position = container.transform.position;
                site.Covers.Clear();
                site.CoversReady = false;
                Orbit.Api.OrbitTelemetry.MainObjectivesRevision++;
                Log.Info($"AMBUSH: airdrop landed at {site.Position}");
                return;
            }
        // Landing callbacks, never a per-bot scene scan. Limit stale drops to this raid's recent events.
        _campSites.RemoveAll(site => site.Kind == CampSiteKind.Airdrop && (site.Drop == null || Time.time >= site.ExpiresAt));
        if (_campSites.Count > 512) return;
        _campSites.Add(new AmbushSite { Kind = CampSiteKind.Airdrop, Position = container.transform.position,
            Drop = container, Landed = true, ExpiresAt = Time.time + 1200f });
        Log.Info($"AMBUSH: airdrop landed at {container.transform.position}");
    }

    internal AmbushSite RegisterReleasedAirdrop(LootableContainer container, Vector3 ground)
    {
        if (container == null || !ServerConfig.Ambush.Enabled || !ServerConfig.Ambush.Airdrops.Enabled) return null;
        foreach (var site in _campSites) if (site.Drop == container) return null;
        _campSites.RemoveAll(site => site.Kind == CampSiteKind.Airdrop && (site.Drop == null || Time.time >= site.ExpiresAt));
        if (_campSites.Count > 512) return null;
        var released = new AmbushSite { Kind = CampSiteKind.Airdrop, Position = ground,
            Drop = container, ExpiresAt = Time.time + 1200f };
        _campSites.Add(released);
        Log.Info($"AMBUSH: airdrop released, estimated landing at {ground}");
        return released;
    }

    internal bool TryGetAmbushLoot(Agent agent, AmbushSite site, out Waypoint point)
    {
        point = null;
        if (site?.Kind != CampSiteKind.Airdrop || !site.Landed || !IsAmbushSiteAvailable(site, agent.BotCategory)) return false;
        if (site.LootWaypoint == null)
        {
            site.LootWaypoint = AirdropPoint(site.Drop);
            if (site.LootWaypoint == null) return false;
        }
        var target = site.LootWaypoint;
        if (agent.Squad.CompletedPoiIds.Contains(target.Id) || agent.ValueSkippedPoiIds.Contains(target.Id)
            || IsClaimedByOther(target.Id, agent.Id)
            || !Orbit.Tasks.Actions.GotoObjectiveAction.IsLootableForAgent(agent, target)
            || !NavMesh.CalculatePath(agent.Position, target.Position, NavMesh.AllAreas, _campPath)
            || _campPath.status != NavMeshPathStatus.PathComplete) return false;
        point = target;
        return true;
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
    }

    internal MainObjective RollExtractCampMain(Squad squad, AmbushStyleSettings style)
    {
        BuildAmbushCatalog();
        if (_campSites.Count == 0) return null;
        var start = UnityEngine.Random.Range(0, _campSites.Count);
        var paths = 0;
        for (var i = 0; i < _campSites.Count; i++)
        {
            var site = _campSites[(start + i) % _campSites.Count];
            if (site.Kind != CampSiteKind.Extract || !IsAmbushSiteAvailable(site, squad.Leader.BotCategory)) continue;
            if (++paths > 8) break;
            if (!IsReachableFromPosition(squad.SpawnPosition, site.Position)) continue;
            return new MainObjective
            {
                Type = MainObjectiveType.ExtractCamp, Position = site.Position, CellCoords = WorldToCell(site.Position),
                CampSite = site, CampTargetDuration = UnityEngine.Random.Range(style.ExtractDurationMin, style.ExtractDurationMax)
            };
        }
        return null;
    }

    internal AmbushSite CreateKillMainCampSite(MainObjective main)
    {
        if (main.Type != MainObjectiveType.Kills || !main.KillAmbush || main.KillZoneRadius <= 0) return null;
        // Unscoped main anchors may have Y=0. Use a real POI inside THIS main's zone and on its floor.
        var center = WorldToCell(main.KillZoneCenter);
        var window = Mathf.CeilToInt(main.KillZoneRadius / _cellSize);
        Waypoint nearest = null;
        var best = float.MaxValue;
        for (var x = Math.Max(0, center.x - window); x <= Math.Min(_gridSize.x - 1, center.x + window); x++)
        for (var y = Math.Max(0, center.y - window); y <= Math.Min(_gridSize.y - 1, center.y + window); y++)
            foreach (var point in _cells[x, y].Waypoints)
            {
                if (point.Category is WaypointCategory.Exfil or WaypointCategory.Corpse
                    || !MatchesZoneFloor(main.ZoneFloorId, point.Position)
                    || XzDistanceSqr(point.Position, main.KillZoneCenter) > main.KillZoneRadius * main.KillZoneRadius) continue;
                var distance = XzDistanceSqr(point.Position, main.Position);
                if (distance >= best) continue;
                best = distance;
                nearest = point;
            }
        if (nearest == null) return null;
        return new AmbushSite
        {
            Kind = CampSiteKind.Hotspot, Position = nearest.Position,
            Scope = new ZoneScope { FloorId = main.ZoneFloorId }, Generation = _campGeneration,
            ZoneCenter = main.KillZoneCenter, ZoneRadius = main.KillZoneRadius
        };
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
        var firstCover = site.Covers.Count > 0 ? UnityEngine.Random.Range(0, site.Covers.Count) : 0;
        for (var category = CoverCategory.Hard; category <= CoverCategory.Soft; category++)
            for (var i = 0; i < site.Covers.Count && pathBudget > 0; i++)
            {
                var cover = site.Covers[(firstCover + i) % site.Covers.Count];
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

    internal bool TryPickAirdropApproach(Agent agent, AmbushSite site, List<Vector3> occupied,
        ref int budget, out CoverPoint chosen)
    {
        chosen = default;
        if (site.Kind != CampSiteKind.Airdrop) return false;
        var spacing = ServerConfig.Ambush.MemberSpacing;
        for (var i = 0; i <= 8 && budget > 0; i++)
        {
            var angle = i * Mathf.PI / 4f;
            // The landing point itself may be the only reachable point on a narrow platform.
            var radius = i == 8 ? 0 : Mathf.Max(6f, spacing * 2f);
            var sample = site.Position + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * radius;
            budget--;
            if (!NavMesh.SamplePosition(sample, out var hit, 3f, NavMesh.AllAreas)
                || Mathf.Abs(hit.position.y - site.Position.y) > 3f) continue;
            var crowded = false;
            foreach (var used in occupied)
                if ((used - hit.position).sqrMagnitude < spacing * spacing) { crowded = true; break; }
            if (crowded) continue;
            if (!NavMesh.CalculatePath(agent.Position, hit.position, NavMesh.AllAreas, _campPath)
                || _campPath.status != NavMeshPathStatus.PathComplete
                || PathHelper.TotalLength(_campPath.corners) > ServerConfig.Ambush.Airdrops.SearchRadius * 1.5f + 50f) continue;
            chosen = new CoverPoint(hit.position, (site.Position - hit.position).normalized, CoverCategory.None, CoverLevel.Stay);
            return true;
        }
        return false;
    }

    private bool ValidateAmbushPosition(Agent agent, AmbushSite site, CampSiteSettings rule, Vector3 candidate,
        List<Vector3> occupied, ref int budget, out Vector3 position)
    {
        position = candidate;
        if (site.Kind == CampSiteKind.Hotspot
            && XzDistanceSqr(candidate, site.ZoneCenter) > site.ZoneRadius * site.ZoneRadius) return false;
        var delta = candidate - site.Position;
        if (Mathf.Abs(delta.y) > 2f || delta.sqrMagnitude < rule.DistanceMin * rule.DistanceMin
            || delta.sqrMagnitude > rule.DistanceMax * rule.DistanceMax || !MatchesZoneFloor(site.Scope?.FloorId, candidate)) return false;
        var spacing = ServerConfig.Ambush.MemberSpacing;
        foreach (var used in occupied) if ((used - candidate).sqrMagnitude < spacing * spacing) return false;
        budget--;
        if (!NavMesh.SamplePosition(candidate, out var hit, .75f, NavMesh.AllAreas)
            || (hit.position - candidate).sqrMagnitude > .25f) return false;
        if (site.Kind == CampSiteKind.Hotspot
            && XzDistanceSqr(hit.position, site.ZoneCenter) > site.ZoneRadius * site.ZoneRadius) return false;
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
