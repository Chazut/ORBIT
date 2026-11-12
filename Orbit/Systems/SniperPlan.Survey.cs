using System;
using EFT;
using Orbit.Helpers;
using Orbit.Navigation;
using Orbit.Zones;
using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Systems;

internal sealed partial class SniperPlan
{
    private SniperSightline.Result _surveyView;
    private Vector3 _surveyEye;
    private int _surveyRays, _surveyViews, _surveyAdjustedViews;
    private bool _surveyHasView;
    private string _terrainRecovery = "not-tried";
    private RaycastHit _terrainHit;
    private bool _terrainHitFound;
    private Vector3? _terrainNav;

    private bool TryTerrainPost(ref Vector3 sample, out NavMeshHit nav)
    {
        nav = default;
        // Outdoor terrain does not share the author's constant Y across a wide
        // sniper radius. Only recover onto actual terrain, never another storey
        // or a roof. Recorded climbs and explicitly authored floors keep their Y.
        if (_recording != null || _site.Elevated || !string.IsNullOrEmpty(_site.FloorId))
        { _terrainRecovery = "authored-height"; return false; }
        if (!Physics.Raycast(sample + Vector3.up * 32, Vector3.down, out var ground, 64,
            LayersMaskController.HighPolyWithTerrainMask, QueryTriggerInteraction.Ignore))
        { _terrainRecovery = "ray-missed"; return false; }
        _terrainHitFound = true; _terrainHit = ground;
        if (!(ground.collider is TerrainCollider))
        { _terrainRecovery = "not-terrain"; return false; }
        if (!NavMesh.SamplePosition(ground.point, out nav, 1.5f, NavMesh.AllAreas))
        { _terrainRecovery = "mesh-missing"; return false; }
        _terrainNav = nav.position;
        if (Mathf.Abs(nav.position.y - ground.point.y) > 1.25f)
        { _terrainRecovery = "mesh-height-mismatch"; return false; }
        _terrainRecovery = "recovered";
        ReportSurvey("terrain-projection", _candidate, "terrain height recovered", sample, nav.position);
        sample = ground.point;
        return true;
    }

    private void ResetViewSurvey()
    { _surveyRays = _surveyViews = _surveyAdjustedViews = 0; _surveyHasView = false; _surveyView = default;
        _terrainRecovery = "not-tried"; _terrainHitFound = false; _terrainHit = default; _terrainNav = null; }

    private bool ProbeView(Vector3 head, Vector3 direction, out Vector3 visible)
    {
        var result = SniperSightline.Probe(head, direction, _site.WatchDistance, ServerConfig.Rush.SniperDisengageDistance);
        _surveyRays += result.LevelTried ? 2 : 1;
        if (result.Usable) { _surveyViews++; if (result.LevelTried) _surveyAdjustedViews++; }
        // Preserve one representative view: a successful correction, otherwise the longest
        // visible ray. A failed survey therefore identifies its best rejected view, not an
        // arbitrary first wall. No strings or collider hierarchy walks with logging off.
        if (!_surveyHasView || result.Usable && result.LevelTried && !(_surveyView.Usable && _surveyView.LevelTried)
            || !(_surveyView.Usable && _surveyView.LevelTried) && result.ClearDistance > _surveyView.ClearDistance)
        { _surveyView = result; _surveyEye = head; _surveyHasView = true; }
        visible = result.Direction;
        return result.Usable;
    }

    private bool RejectPost(string reason, Vector3 sample, Vector3? nav)
    { ReportSurvey("survey", _candidate, reason, sample, nav); return RejectSurvey(reason); }

    // One record per candidate, never per frame/ray: at most 25 for an initial survey,
    // and 16 for an optional post shift. Route success is reported separately.
    private void ReportSurvey(string stage, int candidate, string result, Vector3 sample, Vector3? nav)
    {
        if (!Log.InfoEnabled && !PerformanceJournal.Enabled) return;
        var map = string.IsNullOrEmpty(_waypoints.RecordedMap) ? null : FloorCatalog.For(_waypoints.RecordedMap);
        var seedFloor = map?.Resolve(_site.X, _site.Y, _site.Z);
        var navFloor = nav.HasValue ? map?.Resolve(nav.Value.x, nav.Value.y, nav.Value.z) : null;
        var floorMismatch = map != null && !string.IsNullOrEmpty(_site.FloorId)
            && !map.Matches(_site.FloorId, _site.X, _site.Y, _site.Z);
        bool survey = stage == "survey" || stage == "terrain-projection";
        var detail = FormattableString.Invariant($"site={Step} siteId={_site.Id} map={_waypoints.RecordedMap} stage={stage} candidate={candidate} result={result} seed={Point(new Vector3(_site.X, _site.Y, _site.Z))} sample={Point(sample)} nav={(nav.HasValue ? Point(nav.Value) : "none")} heightDelta={(nav.HasValue ? (nav.Value.y - sample.y).ToString("F3", System.Globalization.CultureInfo.InvariantCulture) : "none")} labelFloor={_site.FloorId ?? "auto"} seedFloor={seedFloor ?? "unknown"} navFloor={navFloor ?? "unknown"} labelMismatch={floorMismatch} elevated={_site.Elevated} snapRadius={(survey ? 1.5f : .75f):F2} heightTolerance={(survey ? 1.25f : RoofHeightTolerance):F2} required={Mathf.Min(_site.WatchDistance, ServerConfig.Rush.SniperDisengageDistance):F2} watchDistance={_site.WatchDistance:F2} yaw={_site.WatchYaw:F1} arc={_site.WatchArc:F1} pitch={_site.WatchPitch:F1} rays={_surveyRays} usableViews={_surveyViews} adjustedViews={_surveyAdjustedViews}");
        if (survey) detail += $" terrainRecovery={_terrainRecovery} terrainHit={Hit(_terrainHitFound, _terrainHit)} terrainNav={(_terrainNav.HasValue ? Point(_terrainNav.Value) : "none")}";
        if (stage == "reposition") detail += $" roofOrigin={Point(_roofOrigin)}";
        if (_surveyHasView)
            detail += FormattableString.Invariant($" representative={_surveyView.Outcome} eye={Point(_surveyEye)} direction={Point(_surveyView.Direction)} clear={_surveyView.ClearDistance:F2} preferredHit={Hit(_surveyView.PreferredBlocked, _surveyView.PreferredHit)} levelHit={(_surveyView.LevelTried ? Hit(_surveyView.LevelBlocked, _surveyView.LevelHit) : "not-tried")}");
        Log.Info($"SNIPER POST: {_squad} {_actor} {detail}");
        PerformanceJournal.Event("sniper-post", _actor?.Bot.ProfileId, detail, _squad.Id);
    }

    private static string Point(Vector3 p) => FormattableString.Invariant($"({p.x:F3},{p.y:F3},{p.z:F3})");

    private static string Hit(bool blocked, RaycastHit hit)
    {
        if (!blocked) return "none";
        var collider = hit.collider;
        var path = collider == null ? "unknown" : collider.name;
        var parent = collider == null ? null : collider.transform.parent;
        for (var i = 0; parent != null && i < 4; i++, parent = parent.parent) path = parent.name + "/" + path;
        return FormattableString.Invariant($"[{path};distance={hit.distance:F2};point={Point(hit.point)};normal={Point(hit.normal)};layer={(collider == null ? -1 : collider.gameObject.layer)}]");
    }
}
