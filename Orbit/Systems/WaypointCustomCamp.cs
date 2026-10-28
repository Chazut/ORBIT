using System.Collections.Generic;
using Orbit.Entities;
using Orbit.Helpers;
using Orbit.Navigation;
using Orbit.Settings;
using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Systems;

public partial class WaypointSystem
{
    // Each yield gives the shared objective-work queue another frame. No scene-wide
    // scans, and at most one path query per step, including rejected candidates.
    internal IEnumerable<CoverPoint?> SearchCustomCamp(Agent agent, RushPoint site, List<Vector3> occupied)
    {
        var center = new Vector3(site.X, site.Y, site.Z);
        var radius = site.Radius;
        var path = new NavMeshPath();
        var covers = new List<CoverPoint>();
        var scratch = new List<CoverPoint>();
        var rotation = UnityEngine.Random.Range(0f, 2f * Mathf.PI);
        for (var probe = 0; probe < 9 && covers.Count < 128; probe++)
        {
            yield return null;
            var angle = rotation + probe * 2.399963f;
            var distance = probe == 0 ? 0 : radius * Mathf.Sqrt(probe / 9f);
            CollectCorpseEscortCover(center + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * distance,
                scratch, ambush: true);
            foreach (var cover in scratch)
            {
                if (CustomCampInZone(site, cover.Position) && !covers.Contains(cover)) covers.Add(cover);
                if (covers.Count >= 128) break;
            }
        }
        var start = covers.Count == 0 ? 0 : UnityEngine.Random.Range(0, covers.Count);
        var paths = 0;
        for (var category = CoverCategory.Hard; category <= CoverCategory.Soft; category++)
            for (var i = 0; i < covers.Count && paths < 24; i++)
            {
                yield return null;
                var candidate = covers[(start + i) % covers.Count];
                if (candidate.Category != category) continue;
                if (!TryCustomCampPosition(agent, site, candidate.Position, occupied, path, ref paths, out var point)) continue;
                var selected = new CoverPoint(point, candidate.Direction, candidate.Category, candidate.Level);
                ReportCustomCamp(agent, site, selected, "native");
                yield return selected;
                yield break;
            }

        // Sparse cover catalogues: prefer physical shelter beside a wall or door.
        // Keep the first reachable random point in reserve if no shelter is usable.
        CoverPoint? fallback = null;
        for (var probe = 0; probe < 16; probe++)
        {
            var offset = UnityEngine.Random.insideUnitCircle * radius;
            var sample = center + new Vector3(offset.x, 0, offset.y);
            for (var heading = 0; heading < 4; heading++)
            {
                yield return null;
                if (!AmbushCoverProbe.Geometry(sample, heading, out var cover) || cover.Category != CoverCategory.Hard) continue;
                yield return null;
                if (!TryCustomCampPosition(agent, site, cover.Position, occupied, path, ref paths, out var point)) continue;
                var selected = new CoverPoint(point, cover.Direction, cover.Category, cover.Level);
                ReportCustomCamp(agent, site, selected, "geometry");
                yield return selected;
                yield break;
            }
            yield return null;
            if (!fallback.HasValue && TryCustomCampPosition(agent, site, sample, occupied, path, ref paths, out var randomPoint))
            {
                var direction = randomPoint - center;
                if (direction.sqrMagnitude < .01f) direction = new Vector3(Mathf.Cos(rotation), 0, Mathf.Sin(rotation));
                fallback = new CoverPoint(randomPoint, direction.normalized, CoverCategory.None, CoverLevel.Stay);
            }
        }
        if (fallback.HasValue)
        {
            ReportCustomCamp(agent, site, fallback.Value, "random");
            yield return fallback;
        }
    }

    private bool CustomCampInZone(RushPoint site, Vector3 point)
        => Mathf.Abs(point.y - site.Y) <= 2f && MatchesZoneFloor(site.FloorId, point)
            && XzDistanceSqr(point, new Vector3(site.X, site.Y, site.Z)) <= site.Radius * site.Radius;

    private bool TryCustomCampPosition(Agent agent, RushPoint site, Vector3 candidate, List<Vector3> occupied,
        NavMeshPath path, ref int paths, out Vector3 point)
    {
        point = default;
        if (!CustomCampInZone(site, candidate) || !NavMesh.SamplePosition(candidate, out var hit, .75f, NavMesh.AllAreas)
            || !CustomCampInZone(site, hit.position) || (hit.position - candidate).sqrMagnitude > .75f * .75f) return false;
        var spacing = Mathf.Min(2f, site.Radius * .5f);
        foreach (var used in occupied) if ((used - hit.position).sqrMagnitude < spacing * spacing) return false;
        paths++;
        if (!NavMesh.CalculatePath(agent.Position, hit.position, NavMesh.AllAreas, path)
            || path.status != NavMeshPathStatus.PathComplete
            || PathHelper.TotalLength(path.corners) > Vector3.Distance(agent.Position, hit.position) + site.Radius * 3f + 20f) return false;
        point = hit.position;
        return true;
    }

    private static void ReportCustomCamp(Agent agent, RushPoint site, CoverPoint cover, string source)
    {
        var detail = $"site={site.Name} source={source} category={cover.Category} position={cover.Position} radius={site.Radius:F1}m";
        Log.Info($"RUSH CAMP: {agent.Squad} member={agent.Id} {detail}");
        PerformanceJournal.Event("rush-camp-position", agent.Bot?.Profile?.Id, detail, agent.Squad.Id);
    }
}
