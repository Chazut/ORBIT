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
    internal float CoversRadius;
    internal int CoverProbe;
}

public partial class WaypointSystem
{
    private readonly List<AmbushSite> _campSites = new();
    private readonly List<CoverPoint> _campCoverScratch = new();
    private readonly NavMeshPath _campPath = new();
    private readonly Dictionary<string, int> _campRejected = new();
    internal void BeginAmbushSearch() => _campRejected.Clear();
    internal string AmbushRejections => _campRejected.Count == 0 ? "no cover candidates" : string.Join(",", _campRejected);
    private bool RejectAmbush(string reason)
    { _campRejected.TryGetValue(reason, out var count); _campRejected[reason] = count + 1; return false; }
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
        AmbushSite selected = null;
        var eligible = 0;
        foreach (var site in _campSites)
        {
            if (site.Kind != CampSiteKind.Extract || !IsAmbushSiteAvailable(site, squad.Leader.BotCategory)) continue;
            // Equal chance among exits, independent of spawn, path length or this bot's own exits.
            // Navigation is attempted when the main starts, including staged approaches.
            if (++eligible == 1 || UnityEngine.Random.value * eligible < 1f) selected = site;
        }
        return selected == null ? null : new MainObjective
        {
            Type = MainObjectiveType.ExtractCamp, Position = selected.Position, CellCoords = WorldToCell(selected.Position),
            CampSite = selected
        };
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

    internal bool TickExtractCampApproach(Squad squad)
    {
        if (squad.MainObjectives == null) return false;
        foreach (var main in squad.MainObjectives)
            if (main.Type == MainObjectiveType.ExtractCamp && main.CanPursue(squad.MainObjectives))
            {
                main.CampApproach ??= new ExtractCampApproach(main);
                return main.CampApproach.Tick(squad, this);
            }
        return false;
    }

    internal bool IsAmbushSiteAvailable(AmbushSite site, string botType)
    {
        if (site == null) return false;
        if (site.Kind == CampSiteKind.Hotspot)
            return site.Generation == _campGeneration && (site.Scope == null || site.Scope.Allows(botType));
        if (site.Kind == CampSiteKind.Airdrop) return site.Drop != null && Time.time < site.ExpiresAt;
        if (site.Exfil?.Target is not ExfiltrationPoint exit || !ExtractCampEligibility.Allows(exit)) return false;
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

    // Synchronous adapters retained for callers that explicitly perform an offline search.
    internal bool TryPickAmbushCover(Agent agent, AmbushSite site, CampSiteSettings rule, List<Vector3> occupied,
        ref int pathBudget, out CoverPoint chosen, float distanceMax = 0)
    {
        var budget = new AmbushSearchBudget(pathBudget); chosen = default;
        foreach (var cover in SearchAmbushCover(agent, site, rule, occupied, budget, distanceMax))
            if (cover.HasValue) { chosen = cover.Value; pathBudget = budget.Remaining; CopyRejections(budget); return true; }
        pathBudget = budget.Remaining; CopyRejections(budget); return false;
    }

    private void CopyRejections(AmbushSearchBudget budget)
    {
        foreach (var pair in budget.Rejected)
        { _campRejected.TryGetValue(pair.Key, out var count); _campRejected[pair.Key] = count + pair.Value; }
    }

    internal IEnumerable<CoverPoint?> SearchAmbushCover(Agent agent, AmbushSite site, CampSiteSettings rule,
        List<Vector3> occupied, AmbushSearchBudget budget, float distanceMax = 0)
    {
        if (distanceMax <= 0) distanceMax = rule.DistanceMax;
        if (distanceMax > site.CoversRadius + .1f)
        { site.CoversRadius = distanceMax; site.CoversReady = false; site.CoverProbe = 0; }
        // A shared target can be queried by several squads. Publish readiness only after all probes.
        while (!site.CoversReady && site.Covers.Count < 512 && budget.Remaining > 0)
        {
            yield return null;
            if (site.CoverProbe >= 256 || !AmbushCoverProbe.Sample(site.Position, site.CoversRadius, site.CoverProbe++, out var sample))
            { site.CoversReady = true; break; }
            if (site.Kind == CampSiteKind.Extract && NavMesh.SamplePosition(sample, out var ground, 12f, NavMesh.AllAreas))
                sample = ground.position;
            CollectCorpseEscortCover(sample, _campCoverScratch, ambush: true);
            foreach (var cover in _campCoverScratch)
                if (!site.Covers.Contains(cover) && site.Covers.Count < 512) site.Covers.Add(cover);
        }
        if (site.Covers.Count >= 512) site.CoversReady = true;
        var covers = site.Covers.ToArray();
        var firstCover = covers.Length > 0 ? UnityEngine.Random.Range(0, covers.Length) : 0;
        for (var category = CoverCategory.Hard; category <= CoverCategory.Soft; category++)
            for (var i = 0; i < covers.Length && budget.Remaining > 0; i++)
            {
                // Bound cheap rejected candidates as well as physics/path queries.
                if ((i & 31) == 0) yield return null;
                var cover = covers[(firstCover + i) % covers.Length];
                if (cover.Category != category) continue;
                // Keep a few path queries for physical cover when baked hints are disconnected.
                if (site.Kind == CampSiteKind.Extract && budget.Remaining <= 4) break;
                yield return null;
                if (ValidateAmbushPosition(agent, site, rule, cover.Position, occupied, budget, out var position, distanceMax))
                { yield return new CoverPoint(position, cover.Direction, cover.Category, cover.Level); yield break; }
            }
        // Some scenes have sparse or missing baked cover hints. Discover nearby physical shelter
        // only after native candidates fail, and validate it with the same floor/path/spacing rules.
        if (site.Kind == CampSiteKind.Extract)
            for (var probe = 0; probe < 256 && budget.Remaining > 0
                && AmbushCoverProbe.Sample(site.Position, distanceMax, probe, out var sample); probe++)
                for (var heading = 0; heading < 4 && budget.Remaining > 0; heading++)
                {
                    yield return null;
                    if (!AmbushCoverProbe.Geometry(sample, heading, out var cover)) continue;
                    yield return null;
                    if (!ValidateAmbushPosition(agent, site, rule, cover.Position, occupied, budget, out var position, distanceMax)) continue;
                    if (site.Covers.Count < 512 && !site.Covers.Contains(cover)) site.Covers.Add(cover);
                    Log.Info($"AMBUSH COVER: {agent.Squad} source=geometry category={cover.Category} position={position}");
                    yield return new CoverPoint(position, cover.Direction, cover.Category, cover.Level);
                    yield break;
                }
        if (!ServerConfig.Ambush.RequireCover)
            for (var i = 0; i < 12 && budget.Remaining > 0; i++)
            {
                yield return null;
                var angle = i * Mathf.PI / 6f;
                var radius = (rule.DistanceMin + distanceMax) * .5f;
                var sample = site.Position + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * radius;
                if (ValidateAmbushPosition(agent, site, rule, sample, occupied, budget, out var position, distanceMax))
                { yield return new CoverPoint(position, (site.Position - position).normalized, CoverCategory.None, CoverLevel.Stay); yield break; }
            }
        if (budget.Remaining <= 0) budget.Reject("query budget");
    }

    internal bool TryPickAirdropApproach(Agent agent, AmbushSite site, List<Vector3> occupied,
        ref int budget, out CoverPoint chosen)
    {
        var work = new AmbushSearchBudget(budget); chosen = default;
        foreach (var cover in SearchAirdropApproach(agent, site, occupied, work))
            if (cover.HasValue) { chosen = cover.Value; budget = work.Remaining; return true; }
        budget = work.Remaining; return false;
    }

    internal IEnumerable<CoverPoint?> SearchAirdropApproach(Agent agent, AmbushSite site, List<Vector3> occupied,
        AmbushSearchBudget budget)
    {
        if (site.Kind != CampSiteKind.Airdrop) yield break;
        var spacing = ServerConfig.Ambush.MemberSpacing;
        for (var i = 0; i <= 8 && budget.Remaining > 0; i++)
        {
            yield return null;
            var angle = i * Mathf.PI / 4f;
            var radius = i == 8 ? 0 : Mathf.Max(6f, spacing * 2f);
            var sample = site.Position + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * radius;
            budget.Remaining--;
            if (!NavMesh.SamplePosition(sample, out var hit, 3f, NavMesh.AllAreas)
                || Mathf.Abs(hit.position.y - site.Position.y) > 3f) continue;
            var crowded = false;
            foreach (var used in occupied)
                if ((used - hit.position).sqrMagnitude < spacing * spacing) { crowded = true; break; }
            if (crowded) continue;
            if (!NavMesh.CalculatePath(agent.Position, hit.position, NavMesh.AllAreas, _campPath)
                || _campPath.status != NavMeshPathStatus.PathComplete
                || PathHelper.TotalLength(_campPath.corners) > ServerConfig.Ambush.Airdrops.SearchRadius * 1.5f + 50f) continue;
            yield return new CoverPoint(hit.position, (site.Position - hit.position).normalized, CoverCategory.None, CoverLevel.Stay);
            yield break;
        }
    }

    private bool ValidateAmbushPosition(Agent agent, AmbushSite site, CampSiteSettings rule, Vector3 candidate,
        List<Vector3> occupied, AmbushSearchBudget budget, out Vector3 position, float distanceMax)
    {
        position = candidate;
        if (site.Kind == CampSiteKind.Hotspot
            && XzDistanceSqr(candidate, site.ZoneCenter) > site.ZoneRadius * site.ZoneRadius) return budget.Reject("outside main zone");
        var delta = candidate - site.Position;
        var heightLimit = site.Kind == CampSiteKind.Extract ? 12f : 2f;
        if (Mathf.Abs(delta.y) > heightLimit || !MatchesZoneFloor(site.Scope?.FloorId, candidate)) return budget.Reject("height or floor");
        if (XzDistanceSqr(candidate, site.Position) < rule.DistanceMin * rule.DistanceMin
            || XzDistanceSqr(candidate, site.Position) > distanceMax * distanceMax) return budget.Reject("range");
        var spacing = ServerConfig.Ambush.MemberSpacing;
        foreach (var used in occupied) if ((used - candidate).sqrMagnitude < spacing * spacing) return budget.Reject("member spacing");
        budget.Remaining--;
        if (!NavMesh.SamplePosition(candidate, out var hit, .75f, NavMesh.AllAreas)
            || (hit.position - candidate).sqrMagnitude > .25f) return budget.Reject("cover off mesh");
        if (site.Kind == CampSiteKind.Hotspot
            && XzDistanceSqr(hit.position, site.ZoneCenter) > site.ZoneRadius * site.ZoneRadius) return budget.Reject("outside main zone");
        delta = hit.position - site.Position;
        if (Mathf.Abs(delta.y) > heightLimit || !MatchesZoneFloor(site.Scope?.FloorId, hit.position)) return budget.Reject("height or floor");
        if (XzDistanceSqr(hit.position, site.Position) < rule.DistanceMin * rule.DistanceMin
            || XzDistanceSqr(hit.position, site.Position) > distanceMax * distanceMax) return budget.Reject("range");
        foreach (var used in occupied) if ((used - hit.position).sqrMagnitude < spacing * spacing) return budget.Reject("member spacing");
        if (!NavMesh.CalculatePath(agent.Position, hit.position, NavMesh.AllAreas, _campPath)
            || _campPath.status != NavMeshPathStatus.PathComplete
            || PathHelper.TotalLength(_campPath.corners) > Mathf.Max(rule.SearchRadius, distanceMax) * 1.5f + 50f) return budget.Reject("incomplete or excessive path");
        // Extract campers can hear arrivals from cover without seeing the exit trigger.
        // Hotspots and airdrops retain their sightline requirement.
        if (site.Kind != CampSiteKind.Extract
            && Physics.Linecast(hit.position + Vector3.up * 1.4f, site.Position + Vector3.up * 1.2f,
                LayersMaskController.HighPolyWithTerrainMask)) return budget.Reject("blocked sightline");
        position = hit.position;
        return true;
    }
}
