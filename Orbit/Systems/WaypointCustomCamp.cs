using System.Collections.Generic;
using Orbit.Entities;
using Orbit.Helpers;
using Orbit.Navigation;
using Orbit.Settings;
using Orbit.Zones;
using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Systems;

public partial class WaypointSystem
{
    private sealed class CustomCampDiagnostics
    {
        internal int Paths, Native, Geometry;
        internal Vector3 Source, Last;
        internal float Length, Limit;
        internal readonly Dictionary<string, int> Rejections = new();
        internal bool Reject(string reason, Vector3 point)
        { Last = point; Rejections.TryGetValue(reason, out var count); Rejections[reason] = count + 1; return false; }
        internal string Summary => $"paths={Paths} native={Native} geometry={Geometry} sourcePosition={Source}"
            + $" lastRejected={Last} lastPath={Length:F1}m limit={Limit:F1}m rejected=[{string.Join(",", Rejections)}]";
    }

    // Each yield gives the shared objective-work queue another frame. No scene-wide
    // scans, and at most one path query per step, including rejected candidates.
    internal IEnumerable<CoverPoint?> SearchCustomCamp(Agent agent, RushPoint site, List<Vector3> occupied)
    {
        var center = new Vector3(site.X, site.Y, site.Z);
        var radius = site.Radius;
        var path = new NavMeshPath();
        var covers = new List<CoverPoint>();
        var scratch = new List<CoverPoint>();
        var diagnostics = new CustomCampDiagnostics();
        var rotation = UnityEngine.Random.Range(0f, 2f * Mathf.PI);
        for (var probe = 0; probe < 9 && covers.Count < 128; probe++)
        {
            yield return null;
            var angle = rotation + probe * 2.399963f;
            var distance = probe == 0 ? 0 : radius * Mathf.Sqrt(probe / 9f);
            var probePoint = center + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * distance;
            if (!TryCustomCampGround(site, probePoint, 1.25f, diagnostics, out var ground)) continue;
            CollectCorpseEscortCover(ground, scratch, ambush: true);
            foreach (var cover in scratch)
            {
                diagnostics.Native++;
                if (CustomCampScope(site, cover.Position, diagnostics) && !covers.Contains(cover)) covers.Add(cover);
                if (covers.Count >= 128) break;
            }
        }
        var start = covers.Count == 0 ? 0 : UnityEngine.Random.Range(0, covers.Count);
        for (var category = CoverCategory.Hard; category <= CoverCategory.Soft; category++)
            for (var i = 0; i < covers.Count && diagnostics.Paths < 24; i++)
            {
                yield return null;
                var candidate = covers[(start + i) % covers.Count];
                if (candidate.Category != category) continue;
                if (!TryCustomCampPosition(agent, site, candidate.Position, occupied, path, diagnostics, out var point)) continue;
                var selected = new CoverPoint(point, candidate.Direction, candidate.Category, candidate.Level);
                ReportCustomCamp(agent, site, selected, "native", diagnostics);
                yield return selected;
                yield break;
            }

        // Sparse cover catalogues: prefer physical shelter beside a wall or door.
        // Spread probes across the disk. Keep the current reachable position as a
        // fallback too, so a narrow corridor cannot be missed by every random roll.
        CoverPoint? fallback = null;
        for (var probe = 0; probe < 16; probe++)
        {
            yield return null;
            var angle = rotation + probe * 2.399963f;
            var distance = radius * Mathf.Sqrt((probe + 1) / 16f);
            var sample = probe == 0 && CustomRushInZone(site, agent.Position) && XzDistanceSqr(agent.Position, center) > .25f ? agent.Position
                : center + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * distance;
            if (!TryCustomCampGround(site, sample, 2f, diagnostics, out var ground)) continue;
            for (var heading = 0; heading < 4; heading++)
            {
                yield return null;
                diagnostics.Geometry++;
                if (!AmbushCoverProbe.Geometry(ground, heading, out var cover, .75f) || cover.Category != CoverCategory.Hard) continue;
                yield return null;
                if (!TryCustomCampPosition(agent, site, cover.Position, occupied, path, diagnostics, out var point)) continue;
                var selected = new CoverPoint(point, cover.Direction, cover.Category, cover.Level);
                ReportCustomCamp(agent, site, selected, "geometry", diagnostics);
                yield return selected;
                yield break;
            }
            yield return null;
            if (!fallback.HasValue && TryCustomCampPosition(agent, site, ground, occupied, path, diagnostics, out var randomPoint))
            {
                var direction = randomPoint - center;
                if (direction.sqrMagnitude < .01f) direction = new Vector3(Mathf.Cos(rotation), 0, Mathf.Sin(rotation));
                fallback = new CoverPoint(randomPoint, direction.normalized, CoverCategory.None, CoverLevel.Stay);
            }
        }
        if (fallback.HasValue)
        {
            ReportCustomCamp(agent, site, fallback.Value, "random", diagnostics);
            yield return fallback;
        }
        else ReportCustomCampSearch(agent, site, "failed", diagnostics);
    }

    internal bool CustomRushInZone(RushPoint site, Vector3 point)
        => Mathf.Abs(point.y - site.Y) <= 2f && MatchesZoneFloor(CustomRushFloor(site), point)
            && XzDistanceSqr(point, new Vector3(site.X, site.Y, site.Z)) <= site.Radius * site.Radius;

    private string CustomRushFloor(RushPoint site)
        => string.IsNullOrEmpty(site.FloorId) ? FloorCatalog.For(_zoneKey)?.Resolve(site.X, site.Y, site.Z) : site.FloorId;

    private bool CustomCampScope(RushPoint site, Vector3 point, CustomCampDiagnostics diagnostics)
    {
        if (Mathf.Abs(point.y - site.Y) > 2f) return diagnostics.Reject("height", point);
        if (!MatchesZoneFloor(CustomRushFloor(site), point)) return diagnostics.Reject("floor", point);
        if (XzDistanceSqr(point, new Vector3(site.X, site.Y, site.Z)) > site.Radius * site.Radius)
            return diagnostics.Reject("outside-zone", point);
        return true;
    }

    private bool TryCustomCampGround(RushPoint site, Vector3 sample, float radius,
        CustomCampDiagnostics diagnostics, out Vector3 point)
    {
        point = default;
        if (!NavMesh.SamplePosition(sample, out var hit, radius, NavMesh.AllAreas)) return diagnostics.Reject("no-mesh", sample);
        if ((hit.position - sample).sqrMagnitude > radius * radius) return diagnostics.Reject("mesh-drift", hit.position);
        if (!CustomCampScope(site, hit.position, diagnostics)) return false;
        point = hit.position;
        return true;
    }

    private bool TryCustomCampPosition(Agent agent, RushPoint site, Vector3 candidate, List<Vector3> occupied,
        NavMeshPath path, CustomCampDiagnostics diagnostics, out Vector3 point)
    {
        point = default;
        if (!CustomCampScope(site, candidate, diagnostics)
            || !TryCustomCampGround(site, candidate, .75f, diagnostics, out var destination)) return false;
        var spacing = Mathf.Min(2f, site.Radius * .5f);
        foreach (var used in occupied) if ((used - destination).sqrMagnitude < spacing * spacing) return diagnostics.Reject("occupied", destination);
        // The bot's standing origin can be slightly above the mesh. Never project it
        // through a floor, or treat a truncated path as reaching the selected post.
        if (!NavMesh.SamplePosition(agent.Position, out var origin, 1.25f, NavMesh.AllAreas)
            || (origin.position - agent.Position).sqrMagnitude > 1.25f * 1.25f) return diagnostics.Reject("source-mesh", agent.Position);
        diagnostics.Source = origin.position;
        diagnostics.Paths++;
        if (!NavMesh.CalculatePath(origin.position, destination, NavMesh.AllAreas, path)) return diagnostics.Reject("path-query", destination);
        if (path.status != NavMeshPathStatus.PathComplete) return diagnostics.Reject("path-" + path.status, destination);
        if (path.corners == null || path.corners.Length == 0
            || Vector3.Distance(path.corners[path.corners.Length - 1], destination) > .75f
            || Vector3.Distance(path.corners[0], origin.position) > 1.25f) return diagnostics.Reject("path-endpoint", destination);
        diagnostics.Length = PathHelper.TotalLength(path.corners);
        diagnostics.Limit = Vector3.Distance(origin.position, destination) + site.Radius * 3f + 20f
            + Mathf.Abs(origin.position.y - destination.y) * 12f;
        if (diagnostics.Length > diagnostics.Limit) return diagnostics.Reject("path-too-long", destination);
        point = destination;
        return true;
    }

    private static void ReportCustomCamp(Agent agent, RushPoint site, CoverPoint cover, string source, CustomCampDiagnostics diagnostics)
    {
        if (!Log.InfoEnabled && !PerformanceJournal.Enabled) return;
        var detail = $"site={site.Name} source={source} category={cover.Category} position={cover.Position} radius={site.Radius:F1}m";
        Log.Info($"RUSH CAMP: {agent.Squad} member={agent.Id} {detail}");
        PerformanceJournal.Event("rush-camp-position", agent.Bot?.Profile?.Id, detail, agent.Squad.Id);
        ReportCustomCampSearch(agent, site, "selected", diagnostics);
    }

    private static void ReportCustomCampSearch(Agent agent, RushPoint site, string state, CustomCampDiagnostics diagnostics)
    {
        if (!Log.InfoEnabled && !PerformanceJournal.Enabled) return;
        var detail = $"member={agent.Id} site={site.Name} id={site.Id} mode={site.CustomMode} state={state}"
            + $" from={agent.Position} center={new Vector3(site.X, site.Y, site.Z)} floor={site.FloorId ?? "auto"} radius={site.Radius:F1}m {diagnostics.Summary}";
        Log.Info($"RUSH CAMP SEARCH: {agent.Squad} {detail}");
        PerformanceJournal.Event("rush-camp-search", agent.Bot?.Profile?.Id, detail, agent.Squad.Id);
    }
}
