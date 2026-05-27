using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using EFT;
using EFT.Interactive;
using Orbit.Config;
using Orbit.Entities;
using Orbit.Helpers;
using Orbit.Inventory;
using Orbit.Navigation;
using Orbit.Sain;
using UnityEngine;
using UnityEngine.AI;
using Random = UnityEngine.Random;

namespace Orbit.Systems;

public struct Cell()
{
    public readonly List<Waypoint> Waypoints = [];
    public int Congestion = 0;

    public bool HasWaypoints
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Waypoints.Count > 0;
    }
}

/// <summary>
/// The dispatch core. Owns the 2D cell grid covering the map, every
/// <see cref="Waypoint"/> on it (loot / quest / exfil / synthetic /
/// corpse), and the per-squad reachability + claim + cooldown state
/// that <c>RequestNear</c> consults to assign objectives. Heavy file —
/// most of the routing intelligence lives here.
/// </summary>
public class WaypointSystem
{
    private readonly Cell[,] _cells;
    private readonly float _cellSize;
    private readonly float _cellSubSize;
    private readonly Vector2Int _gridSize;
    private readonly Vector2 _worldMin;

    private readonly Queue<Vector2Int> _validCellQueue;
    private readonly Dictionary<Entity, Vector2Int> _assignments;

    private readonly BotsController _botsController;
    private readonly ConfigBundle<WaypointConfig.MapZone> _zoneConfig;
    private readonly List<Zone> _zones;
    private readonly Vector2[,] _advectionField;
    private readonly string _mapId;

    private readonly List<Vector2Int> _tempCoordsBuffer = [];
    private readonly WaypointGatherer _waypointGatherer;

    // id → cell coords for fast removal. Populated by RegisterWaypointInCell
    // regardless of whether the waypoint came from CollectBuiltinWaypoints,
    // synthetic cell fill, or AddRuntimeWaypoint.
    private readonly Dictionary<int, Vector2Int> _waypointCells = new();

    // Per-waypoint reservation: while a bot is actively looting a waypoint,
    // other bots (same squad or different) cannot start a second loot on it.
    // Cleared by ReleaseClaim or automatically on RemoveWaypoint.
    private readonly Dictionary<int, int> _claims = new();

    // Reverse index: Item.Id (string MongoID) → Waypoint.Id (int), populated
    // only for LooseLoot waypoints whose Target is a LootItem. Lets the
    // inventory-change hook find and prune the matching waypoint when a
    // player or vanilla-AI bot picks up an item outside of our routine.
    private readonly Dictionary<string, int> _lootItemIdToWaypointId = new();

    // Lazy navmesh-reachability cache for waypoints. SPT can RNG-spawn loot
    // items inside disconnected navmesh islands (locked interiors, rooftops
    // accessible only via leaked navmesh). Each is path-checked the first
    // time PickFromCell considers it, from the requesting squad leader's
    // position (bots are navmesh-bound by BSG spawn). Results are cached so
    // every waypoint is at most one NavMesh.CalculatePath per raid.
    private readonly HashSet<int> _pathReachable = new();
    // Per-squad negative cache. KEY = squad.Id, VALUE = set of waypoint ids
    // that CalculatePath failed from that squad's leader position. Used to
    // be a single global HashSet but that was a real bug source: a squad
    // whose leader spawned on a disconnected navmesh fragment would poison
    // the cache for every other squad. Per-squad means each squad re-
    // evaluates from its OWN leader position. Positive cache stays global —
    // if any squad reached a waypoint, the navmesh genuinely connects.
    private readonly Dictionary<int, HashSet<int>> _squadUnreachable = new();
    // Cached list of every door that started the raid Locked. Built lazily
    // on first call to IsWaypointReachable when a PathPartial is detected,
    // so PMC squads can still be dispatched on waypoints behind locked doors
    // (marked rooms etc.) — they roll for a force-unlock at arrival. Null
    // until the first miss; never cleared (door state can change at runtime
    // but the candidate set is fixed by what was locked at raid start).
    private List<Door> _rawLockedDoors;
    // Removal queue for unreachable waypoints detected during PickFromCell
    // iteration. We can't mutate cell.Waypoints mid-iteration so we drain
    // this at the start of each PickFromCell call instead.
    private readonly Queue<int> _pendingRemoval = new();

    public Cell[,] Cells => _cells;
    public Vector2Int GridSize => _gridSize;
    public Vector2 WorldMin => _worldMin;
    public float CellSize => _cellSize;
    public Vector2[,] AdvectionField => _advectionField;
    public List<Zone> Zones => _zones;

    public WaypointSystem(string mapId, WaypointConfig waypointConfig, BotsController botsController)
    {
        _mapId = mapId;
        _zoneConfig = waypointConfig.MapZones[mapId];
        _botsController = botsController;

        Log.Info("Gathering built in waypoints");
        _waypointGatherer = new WaypointGatherer(_cellSize, botsController);
        var builtinWaypoints = _waypointGatherer.CollectBuiltinWaypoints();

        Log.Info("Calculating world geometry");
        var geometryConfig = waypointConfig.MapGeometries.Value[mapId];

        // Calculate bounds from positions
        _worldMin = geometryConfig.Min;
        var worldMax = geometryConfig.Max;

        for (var i = 0; i < builtinWaypoints.Count; i++)
        {
            var pos = builtinWaypoints[i].Position;
            _worldMin.x = Mathf.Min(_worldMin.x, pos.x);
            _worldMin.y = Mathf.Min(_worldMin.y, pos.z);
            worldMax.x = Mathf.Max(worldMax.x, pos.x);
            worldMax.y = Mathf.Max(worldMax.y, pos.z);
        }

        var worldWidth = worldMax.x - _worldMin.x;
        var worldHeight = worldMax.y - _worldMin.y;

        _cellSize = geometryConfig.CellSize;
        _cellSubSize = _cellSize / 2f;

        var cols = Mathf.CeilToInt(worldWidth / _cellSize);
        var rows = Mathf.CeilToInt(worldHeight / _cellSize);

        _gridSize = new Vector2Int(cols, rows);
        _cells = new Cell[cols, rows];

        var searchRadius = Math.Max(worldWidth, worldHeight) / 2f;

        Log.Info("Constructing waypoint system cells");
        for (var x = 0; x < cols; x++)
        {
            for (var y = 0; y < rows; y++)
                _cells[x, y] = new Cell();
        }

        Log.Info("Populating cells with builtin waypoints");
        for (var i = 0; i < builtinWaypoints.Count; i++)
        {
            var waypoint = builtinWaypoints[i];
            var coords = WorldToCell(waypoint.Position);

            if (!IsValidCell(coords))
            {
                Log.Warning($"{waypoint} with coords {coords} doesn't fall inside valid cell (grid size {_gridSize})");
                continue;
            }

            RegisterWaypointInCell(coords, waypoint);
        }

        Log.Debug("Populating cells with synthetic waypoints");
        _validCellQueue = new Queue<Vector2Int>();
        for (var x = 0; x < _gridSize.x; x++)
        {
            for (var y = 0; y < _gridSize.y; y++)
            {
                var cellCoords = new Vector2Int(x, y);
                ref var cell = ref _cells[cellCoords.x, cellCoords.y];

                // Always run PopulateCell, even when builtin waypoints
                // already live in the cell. Skipping left cells with a
                // single Container surrounded by huge dead zones in the
                // synthetic patrol mesh — the dispatcher then had no
                // Synthetic options to pick when the Container was claimed.
                if (cell.HasWaypoints)
                    _validCellQueue.Enqueue(cellCoords);

                Log.Debug($"Cell at [{x}, {y}] attempting to populate synthetic waypoints");

                var worldPos = CellToWorld(cellCoords);

                if (NavMesh.SamplePosition(worldPos, out var hit, searchRadius, NavMesh.AllAreas))
                {
                    if (WorldToCell(hit.position) == cellCoords)
                    {
                        if (PopulateCell(cell, cellCoords, hit.position)) continue;
                    }
                }

                Log.Debug($"Cell {cellCoords}: no reachable synthetic waypoints found");
            }
        }

        _assignments = new Dictionary<Entity, Vector2Int>();

        // Advection
        _zones = [];
        _advectionField = new Vector2[_gridSize.x, _gridSize.y];
        CalculateAdvectionZones();

        Log.Info($"Waypoint grid size: {_gridSize}, cell size: {_cellSize:F1}, waypoints: {builtinWaypoints.Count}");
        Log.Info($"Waypoint grid world bounds: [{_worldMin.x:F0},{_worldMin.y:F0}] -> [{worldMax.x:F0},{worldMax.y:F0}]");
        Log.Info($"Waypoint grid world size: {worldWidth:F0}x{worldHeight:F0} search radius: {searchRadius}");
    }

    public void ReloadConfig() => _zoneConfig.Reload();

    public void CalculateAdvectionZones()
    {
        _zones.Clear();

        for (var i = 0; i < _botsController.BotSpawner.AllBotZones.Length; i++)
        {
            var botZone = _botsController.BotSpawner.AllBotZones[i];

            if (!_zoneConfig.Value.BuiltinZones.TryGetValue(botZone.name, out var builtinZone))
                continue;

            var minRadius = Mathf.Min(builtinZone.Radius.Min, builtinZone.Radius.Max);

            if (minRadius < 1)
                throw new ArgumentException("The zone radius must be greater than or equal to 1");

            var zone = new Zone(
                WorldToCell(botZone.CenterOfSpawnPoints),
                builtinZone.Radius.SampleGaussian(),
                builtinZone.Force.SampleGaussian(),
                builtinZone.Decay
            );
            _zones.Add(zone);
        }

        for (var i = 0; i < _zoneConfig.Value.CustomZones.Count; i++)
        {
            var customZone = _zoneConfig.Value.CustomZones[i];

            var minRadius = Mathf.Min(customZone.Radius.Min, customZone.Radius.Max);

            if (minRadius < 1)
                throw new ArgumentException("The zone radius must be greater than or equal to 1");

            var coords = WorldToCell(customZone.Position);

            if (!IsValidCell(coords))
            {
                Log.Debug($"Custom zone at {customZone.Position} with cell coords {coords} falls outside of bounds {_gridSize}");
                continue;
            }

            var zone = new Zone(
                coords,
                customZone.Radius.SampleGaussian(),
                customZone.Force.SampleGaussian(),
                customZone.Decay
            );
            _zones.Add(zone);
        }

        for (var x = 0; x < _gridSize.x; x++)
        {
            for (var y = 0; y < _gridSize.y; y++)
            {
                var cellCoords = new Vector2(x, y);

                _advectionField[x, y] = Vector2.zero;

                for (var i = 0; i < _zones.Count; i++)
                {
                    var zone = _zones[i];
                    var zoneCoords = (Vector2)zone.Coords;
                    var worldDist = Vector2.Distance(zoneCoords, cellCoords) * _cellSize;
                    var force = Mathf.Clamp01(1f - worldDist / (zone.Radius * Plugin.AdvectionZoneRadiusScale.Value));
                    force = Mathf.Pow(force, zone.Decay * Plugin.AdvectionZoneRadiusDecayScale.Value);
                    force *= zone.Force * Plugin.AdvectionZoneForceScale.Value;
                    _advectionField[x, y] += force * (zoneCoords - cellCoords).normalized;
                }
            }
        }

        foreach (var coords in _assignments.Values)
            PropagateForce(coords, 1f);
    }

    // Threshold for the islanded-pin: after this many consecutive null
    // returns from RequestNear/RequestFar, the squad is treated as
    // stranded and locked to local cell only.
    private const int IslandedFailureThreshold = 3;

    public Waypoint RequestNear(Entity entity, Vector3 worldPos, Waypoint previous)
    {
        // Always try and return assignments first to avoid counting our own
        // influence into the decision.
        Return(entity);

        // Pre-scan: if this squad has any tagged own-kill Corpse waypoint
        // still alive (not claimed, not blacklisted, reachable), bee-line
        // to it before the normal neighbour scan runs. The neighbour scan
        // returns the first cell that yields any pick, so a fresh
        // Synthetic in a closer-to-prefDir neighbour could beat an own-
        // kill Corpse two cells away.
        if (entity is Squad squadForKill)
        {
            var killPick = TryPickOwnKillCorpse(squadForKill);
            if (killPick != null) return killPick;
        }

        var requestCoords = WorldToCell(worldPos);

        if (!IsValidCell(requestCoords))
            return ScavOrIslandedLocalOnly(entity) ? null : RequestFar(entity);

        // Closeness short-circuit: if the squad is currently in the anchor
        // cell of any pending main objective, pick from THIS cell instead
        // of scanning neighbours. Without this, the inverse-distance force
        // pulls the squad into the anchor cell but the standard neighbour
        // scan keeps picking waypoints in surrounding cells — the squad
        // orbits the objective without draining it.
        if (entity is Squad squadInAnchorCell
            && squadInAnchorCell.MainObjectives != null
            && IsCurrentCellAnchorOfPendingMain(squadInAnchorCell, requestCoords))
        {
            var localCellRef = _cells[requestCoords.x, requestCoords.y];
            if (localCellRef.HasWaypoints)
            {
                var localPick = AssignWaypoint(entity, requestCoords);
                if (localPick != null) return localPick;
            }
            // No pick in the anchor cell (all claimed, all blacklisted,
            // Quest reserved for a different squad) — fall through.
        }

        // If this squad has been failing to find anything reachable for a
        // while (scavs spawned on an island, PMC trapped behind a closed
        // door), don't let the dispatcher keep handing them neighbour/
        // map-wide cells they can't reach either. Pin them to their own
        // cell only — they'll wait there until a member naturally drifts
        // or the failure counter resets.
        if (IsSquadIslanded(entity))
        {
            var currentCell = _cells[requestCoords.x, requestCoords.y];
            if (!currentCell.HasWaypoints) return null;
            return AssignWaypoint(entity, requestCoords);
        }

        var previousCoords = previous == null ? requestCoords : WorldToCell(previous.Position);
        _tempCoordsBuffer.Clear();

        // First pass: determine preferential direction
        for (var dx = -1; dx <= 1; dx++)
        {
            for (var dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;

                var direction = new Vector2Int(dx, dy);
                var coords = requestCoords + direction;

                if (!IsValidCell(coords))
                    continue;

                ref var cell = ref _cells[coords.x, coords.y];

                if (!cell.HasWaypoints)
                    continue;

                _tempCoordsBuffer.Add(direction);
            }
        }

        var advectionVector = _advectionField[requestCoords.x, requestCoords.y];
        var randomization = Random.insideUnitCircle;
        randomization *= 0.5f;
        var momentumVector = (Vector2)(requestCoords - previousCoords);
        momentumVector.Normalize();
        momentumVector *= 0.5f;
        var homeVector = ComputeHomeAttraction(entity, requestCoords);
        var mainObjectiveVector = ComputeMainObjectiveAttraction(entity, requestCoords);

        var prefDirection = momentumVector + advectionVector + randomization + homeVector + mainObjectiveVector;

        Log.Debug(
            $"Waypoint search from {requestCoords} direction: {prefDirection} mom: {momentumVector} adv: {advectionVector} rand: {randomization} home: {homeVector} main: {mainObjectiveVector}"
        );

        if (_tempCoordsBuffer.Count == 0 || prefDirection == Vector2.zero)
        {
            Log.Debug("Zero vector preferred direction, trying the current cell, and failing that the map-wide least congested cell");
            var currentCell = _cells[requestCoords.x, requestCoords.y];
            if (currentCell.HasWaypoints)
            {
                var localPick = AssignWaypoint(entity, requestCoords);
                if (localPick != null) return localPick;
            }
            return ScavOrIslandedLocalOnly(entity) ? null : RequestFar(entity);
        }

        prefDirection.Normalize();

        // Sort candidate neighbours by closeness to the preferred direction
        // (lowest angle first). Iterating in priority order lets us try the
        // next-best neighbour when the first pick's waypoints are all
        // filtered — instead of jumping straight to RequestFar across the
        // map.
        _tempCoordsBuffer.Sort((a, b) =>
            Vector2.Angle(a, prefDirection).CompareTo(Vector2.Angle(b, prefDirection)));

        for (var i = 0; i < _tempCoordsBuffer.Count; i++)
        {
            var neighbor = requestCoords + _tempCoordsBuffer[i];
            var pick = AssignWaypoint(entity, neighbor);
            if (pick != null) return pick;
            // Every waypoint in this neighbour was filtered (unreachable from
            // current leader nav position, ineligible exfil, blacklisted)
            // — try the next-best neighbour before giving up on the local
            // area.
        }

        // Local area genuinely exhausted: also try the current cell itself
        // (the neighbour-scan loop skipped it), and only if that also fails
        // do we escalate to the map-wide RequestFar.
        var localCellFinal = _cells[requestCoords.x, requestCoords.y];
        if (localCellFinal.HasWaypoints)
        {
            var localPick = AssignWaypoint(entity, requestCoords);
            if (localPick != null) return localPick;
        }
        return ScavOrIslandedLocalOnly(entity) ? null : RequestFar(entity);
    }

    // Tuning for the per-scav home-attraction force. 3.0 keeps the home
    // vector decisively above momentum (0.5) + randomization (0.5) so
    // scavs that drift via neighbour-hopping get pulled back to their
    // spawn quartier. Distance-for-full-strength left at 5 cells; we want
    // a strong pull only once the squad has already drifted, not from the
    // first step away from spawn.
    private const float HomeAttractionMaxMagnitude = 3.0f;
    private const float HomeAttractionDistanceForFullStrength = 5f; // in cells

    /// <summary>
    /// Per-squad pull back toward the squad leader's spawn cell, for scavs
    /// only (and only when Roaming Scavs is OFF). Without this, scavs that
    /// stay 'local' via the 3x3 neighbour restriction still chain neighbour
    /// hops indefinitely and end up far from their spawn quartier.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector2 ComputeHomeAttraction(Entity entity, Vector2Int currentCoords)
    {
        if (entity is not Squad squad) return Vector2.zero;
        var leaderBot = squad.Leader?.Bot;
        var role = leaderBot?.Profile?.Info?.Settings?.Role;
        if (!role.HasValue || !role.Value.IsScav()) return Vector2.zero;
        // PlayerScavs share WildSpawnType.assault with bot scavs so IsScav()
        // returns true for them too — but the intent is PlayerScavs follow
        // their main objectives like PMCs, not the bot-scav home-pinning.
        // Without this exclusion the home pull (up to magnitude 3.0)
        // competes with the main-objective pull (max 4.0) and PlayerScavs
        // drift around their spawn quartier instead of biasing toward
        // their assigned mains.
        if (leaderBot?.Profile != null && leaderBot.Profile.WillBeAPlayerScav()) return Vector2.zero;
        // When RoamingScavs is ON, scavs are free to wander like PMCs and
        // the home pull is silenced.
        if (Plugin.RoamingScavs.Value) return Vector2.zero;

        if (!squad.SpawnCell.HasValue) squad.SpawnCell = currentCoords;
        var spawn = squad.SpawnCell.Value;
        var delta = (Vector2)(spawn - currentCoords);
        var dist = delta.magnitude;
        if (dist < 0.5f) return Vector2.zero; // already at home cell

        delta /= dist; // normalize
        var strength = Mathf.Clamp01(dist / HomeAttractionDistanceForFullStrength);
        return delta * (strength * HomeAttractionMaxMagnitude);
    }

    // ── Main-objective force attraction ──────────────────────────────
    //
    // Sums inverse-distance-weighted unit vectors toward every pending
    // (non-Completed) main objective. Closest pending naturally dominates
    // (1/d weighting) but distant ones contribute a small bias so the
    // squad doesn't ignore them entirely. Special case: Kills mains in
    // roam phase contribute a constant-magnitude force toward the exact
    // anchor point (no inverse-distance) so the squad oscillates around
    // it for the rolled duration.

    /// <summary>
    /// True iff <paramref name="cellCoords"/> matches the anchor cell of
    /// any pending (non-Completed) main objective on the squad. Used by
    /// <see cref="RequestNear"/> to short-circuit the neighbour scan and
    /// force a local pick when the squad is already in the anchor cell —
    /// prevents the "orbit around objective" pattern where the force
    /// attraction pulls the squad in but the dispatch keeps scanning
    /// surrounding cells.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsCurrentCellAnchorOfPendingMain(Squad squad, Vector2Int cellCoords)
    {
        if (squad.MainObjectives == null) return false;
        for (var i = 0; i < squad.MainObjectives.Count; i++)
        {
            var main = squad.MainObjectives[i];
            if (main.Completed) continue;
            if (main.CellCoords == cellCoords) return true;
        }
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector2 ComputeMainObjectiveAttraction(Entity entity, Vector2Int currentCoords)
    {
        if (entity is not Squad squad || squad.MainObjectives == null) return Vector2.zero;
        var sum = Vector2.zero;
        var roamForce = Plugin.MainObjectivesKillsRoamForceMagnitude.Value;
        for (var i = 0; i < squad.MainObjectives.Count; i++)
        {
            var main = squad.MainObjectives[i];
            if (main.Completed) continue;
            var delta = (Vector2)(main.CellCoords - currentCoords);
            var dist = delta.magnitude;
            if (main.Type == MainObjectiveType.Kills && main.KillsRoamStartedAt > 0f)
            {
                // Roam phase: constant pull toward the exact anchor.
                if (dist > 0.01f) sum += (delta / dist) * roamForce;
                continue;
            }
            // Approach phase (or LootValue / Quest): inverse-distance pull
            if (dist < 1f) continue;            // already in this main's cell — no pull
            sum += (delta / dist) * (1f / dist);
        }
        var mag = sum.magnitude;
        var maxMag = Plugin.MainObjectivesAttractionMagnitude.Value;
        if (mag > maxMag) sum *= maxMag / mag;
        return sum;
    }

    /// <summary>
    /// Decides whether this entity should be denied RequestFar (the map-
    /// wide dispatch fallback). Scavs are pinned by default
    /// (Plugin.RoamingScavs OFF) — keeps them in their spawn quartier.
    /// Goons are NOT pinned by default (Plugin.RoamingGoons ON, since
    /// vanilla Tarkov has them roaming across the map). Everyone else
    /// (PMCs, raiders, bosses, cultists, bloodhounds) roams freely.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ScavOrIslandedLocalOnly(Entity entity)
    {
        if (entity is not Squad squad) return false;
        var leaderBot = squad.Leader?.Bot;
        var role = leaderBot?.Profile?.Info?.Settings?.Role;
        if (!role.HasValue) return false;
        // PlayerScavs share WildSpawnType.assault with bot scavs but are NOT
        // pinned — they follow the same main-objective dispatch as PMCs and
        // need RequestFar to reach mains anywhere on the map.
        var isPlayerScav = leaderBot?.Profile != null && leaderBot.Profile.WillBeAPlayerScav();
        if (role.Value.IsScav() && !isPlayerScav && !Plugin.RoamingScavs.Value) return true;
        if (role.Value.IsGoon() && !Plugin.RoamingGoons.Value) return true;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsSquadIslanded(Entity entity)
    {
        if (entity is not Squad squad) return false;
        if (squad.ConsecutiveDispatchFailures < IslandedFailureThreshold) return false;
        var role = squad.Leader?.Bot?.Profile?.Info?.Settings?.Role;
        // Scav-only: the failure-counter pin is intentionally restricted to
        // scavs because PMCs benefit more from continuing to retry distant
        // cells (they have quest/extract goals across the map).
        return role.HasValue && role.Value.IsScav();
    }

    /// <summary>
    /// Adds a runtime-created Waypoint (e.g. a freshly killed bot's
    /// corpse) to the cell covering its position. No advection recalc —
    /// the waypoint only affects bots that scan a neighbouring cell.
    /// </summary>
    public bool AddRuntimeWaypoint(Waypoint waypoint)
    {
        var coords = WorldToCell(waypoint.Position);
        if (!IsValidCell(coords))
        {
            Log.Warning($"AddRuntimeWaypoint: {waypoint} at {waypoint.Position} → cell {coords} outside grid {_gridSize}");
            return false;
        }

        RegisterWaypointInCell(coords, waypoint);
        Log.Debug($"AddRuntimeWaypoint: {waypoint} added to cell {coords}");
        return true;
    }

    // Runtime-allocated Waypoint.Ids start high enough to never collide
    // with the WaypointGatherer's monotonic counter (which produces a few
    // thousand builtin waypoints at most).
    private int _runtimeIdCounter = 1_000_000;
    public int NewRuntimeWaypointId() => System.Threading.Interlocked.Increment(ref _runtimeIdCounter);

    // Records which squad killed each runtime Corpse waypoint (if any).
    // Populated by the corpse-registration patch when it can resolve the
    // dying player's LastAggressor to an ORBIT-managed bot. Consumed by
    // PickFromCell to satisfy the "corpse requires sight or squad kill"
    // config — a squad always retains the right to loot a body they
    // dropped, regardless of LoS.
    private readonly Dictionary<int, int> _corpseKillerSquadId = new();

    public void TagCorpseKillerSquad(int waypointId, int squadId)
        => _corpseKillerSquadId[waypointId] = squadId;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool WasCorpseKilledBySquad(int waypointId, int squadId)
        => _corpseKillerSquadId.TryGetValue(waypointId, out var owner) && owner == squadId;

    // Max cell-distance from the leader within which an own-kill corpse
    // still trips the bee-line. Beyond this, the kill is considered stale
    // — the squad has drifted too far for the bee-line to make sense.
    private const int OwnKillCorpseMaxCellDistance = 3;

    /// <summary>
    /// Returns the first reachable, unclaimed, non-blacklisted runtime
    /// Corpse waypoint that this squad is credited with the kill on, or
    /// null if none exist.
    /// </summary>
    private Waypoint TryPickOwnKillCorpse(Squad squad)
    {
        if (squad == null) return null;
        var leaderCell = WorldToCell(squad.Leader?.Bot?.Position ?? Vector3.zero);
        foreach (var kvp in _corpseKillerSquadId)
        {
            if (kvp.Value != squad.Id) continue;
            var locId = kvp.Key;
            if (squad.CompletedPoiIds.Contains(locId)) continue;
            if (_claims.ContainsKey(locId)) continue;
            if (!_waypointCells.TryGetValue(locId, out var coords)) continue;
            // Stale-kill gate: if the corpse is too far from the leader,
            // skip the bee-line and let normal dispatch handle it.
            var cellDelta = coords - leaderCell;
            if (Mathf.Abs(cellDelta.x) > OwnKillCorpseMaxCellDistance
                || Mathf.Abs(cellDelta.y) > OwnKillCorpseMaxCellDistance)
            {
                continue;
            }
            var cell = _cells[coords.x, coords.y];
            Waypoint corpse = null;
            for (var i = 0; i < cell.Waypoints.Count; i++)
            {
                if (cell.Waypoints[i].Id == locId)
                {
                    corpse = cell.Waypoints[i];
                    break;
                }
            }
            if (corpse == null) continue;
            if (corpse.Category != WaypointCategory.Corpse) continue;
            if (!IsWaypointReachable(corpse, squad)) continue;
            Log.Info($"RequestNear: {squad} bee-lining to own-kill corpse {corpse} (in cell {coords})");
            return corpse;
        }
        return null;
    }

    /// <summary>
    /// Raycast from the squad leader's eye position to the corpse, using
    /// the same HighPolyWithTerrainMask BSG bots use for cover/vision
    /// checks. Returns true if nothing blocks the line.
    /// </summary>
    private static bool HasLineOfSightToCorpse(Squad squad, Waypoint corpse)
    {
        var leader = squad?.Leader?.Bot;
        if (leader?.LookSensor == null) return false;
        var start = leader.LookSensor.HeadPoint;
        var direction = corpse.Position - start;
        var blocked = Physics.Raycast(start, direction, direction.magnitude, LayerMaskClass.HighPolyWithTerrainMask);
        return !blocked;
    }

    /// <summary>
    /// True iff this squad has a Quest-type main objective whose
    /// <c>QuestTriggerId</c> matches the waypoint's <c>Name</c>. Quest
    /// waypoints are reserved EXCLUSIVELY for main objectives; bots
    /// passing through a quest cell on an unrelated errand never pick
    /// the Quest as a target.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool SquadOwnsQuest(Squad squad, Waypoint loc)
    {
        if (squad?.MainObjectives == null || loc.Name == null) return false;
        for (var i = 0; i < squad.MainObjectives.Count; i++)
        {
            var main = squad.MainObjectives[i];
            if (main.Type != MainObjectiveType.Quest) continue;
            if (main.QuestTriggerId == loc.Name) return true;
        }
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool CorpseRequiresSightOrSquadKillForSquad(Squad squad)
        => LootConfig.CorpseRequiresSightOrSquadKill?.Value ?? false;

    /// <summary>
    /// Looks for an unlooted Corpse waypoint any squad member can see
    /// within <see cref="LootConfig.DetectCorpseDistance"/>. Returns the
    /// first match or null if no opportunistic loot target exists.
    /// </summary>
    public Waypoint TryFindOpportunisticCorpse(Squad squad)
    {
        if (squad == null || squad.Members.Count == 0) return null;
        var leaderRole = squad.Leader?.Bot?.Profile?.Info?.Settings?.Role;
        if (!leaderRole.HasValue) return null;
        if (!(LootConfig.CorpseLootingEnabled?.Value ?? LootingFaction.None).IsBotEnabled(leaderRole.Value)) return null;

        var maxDist = LootConfig.DetectCorpseDistance?.Value ?? 0f;
        var maxDistSqr = maxDist * maxDist;

        for (var m = 0; m < squad.Members.Count; m++)
        {
            var member = squad.Members[m];
            if (member?.Bot?.LookSensor == null) continue;
            var memberPos = member.Position;
            var memberCell = WorldToCell(memberPos);
            if (!IsValidCell(memberCell)) continue;

            // Walk the 3×3 cell window around the member.
            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dy = -1; dy <= 1; dy++)
                {
                    var coords = new Vector2Int(memberCell.x + dx, memberCell.y + dy);
                    if (!IsValidCell(coords)) continue;
                    var cell = _cells[coords.x, coords.y];
                    if (!cell.HasWaypoints) continue;

                    var locs = cell.Waypoints;
                    for (var i = 0; i < locs.Count; i++)
                    {
                        var loc = locs[i];
                        if (loc.Category != WaypointCategory.Corpse) continue;
                        if (squad.CompletedPoiIds.Contains(loc.Id)) continue;
                        if (_claims.ContainsKey(loc.Id)) continue;

                        var delta = loc.Position - memberPos;
                        if (delta.sqrMagnitude > maxDistSqr) continue;

                        // LoS raycast from this member's head to the corpse.
                        var head = member.Bot.LookSensor.HeadPoint;
                        var dir = loc.Position - head;
                        var blocked = Physics.Raycast(head, dir, dir.magnitude, LayerMaskClass.HighPolyWithTerrainMask);
                        if (blocked) continue;

                        return loc;
                    }
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Roam-mode helper: searches a configurable cell window around the
    /// squad's current main-objective anchor for an unclaimed waypoint
    /// matching the supplied category mask. Used during Kills roam /
    /// LootValue active phases to spread squad members across the area
    /// instead of all converging on a single anchor. Each member calls
    /// this from their own position so picks are spatially diverse.
    /// </summary>
    public Waypoint FindRoamSplinterForMember(
        Vector3 memberPos,
        Vector3 searchCenter,
        Squad squad,
        HashSet<int> excludeIds,
        float radius,
        bool allowLooseLoot,
        bool allowContainerLoot,
        bool allowCorpse,
        bool allowSynthetic)
    {
        // Search radius is anchored on `searchCenter` (caller decides:
        // normally memberPos for natural drift, swapped to the main's
        // anchor position when the bot has drifted further than `radius`
        // from the anchor — a leash that lets the bot wander up to ~50m
        // organically but snaps the next pick back into the main's zone
        // if they've strayed too far). Self-exclusion below still uses
        // memberPos.
        var center = WorldToCell(searchCenter);
        // Convert radius to cell window. 50m / 75m cell ≈ 1 cell window
        // → search the 3×3 around member. 75m / 50m cell ≈ 2 cell window
        // for tighter cells.
        var cellWindow = Mathf.Max(1, Mathf.CeilToInt(radius / _cellSize));
        var radSqr = radius * radius;

        // Reservoir-sample uniformly random among ALL eligible waypoints
        // in the radius. Previous version returned the NEAREST — that
        // locked solo bots in place during Kills/LootValue roam: the
        // nearest waypoint was almost always THEMSELVES (or the splinter
        // they just arrived at), so each re-dispatch handed them back the
        // same target. Random picking forces actual roaming.
        //
        // Self-exclusion: any waypoint within ~3m of memberPos is treated
        // as "current location" and skipped, otherwise the random pick
        // still has a non-zero chance of returning the bot's exact spot.
        const float selfExclusionDistSqr = 3f * 3f;
        Waypoint best = null;
        var candidates = 0;
        for (var dx = -cellWindow; dx <= cellWindow; dx++)
        {
            for (var dy = -cellWindow; dy <= cellWindow; dy++)
            {
                var c = center + new Vector2Int(dx, dy);
                if (!IsValidCell(c)) continue;
                var locs = _cells[c.x, c.y].Waypoints;
                for (var i = 0; i < locs.Count; i++)
                {
                    var loc = locs[i];
                    var ok = loc.Category switch
                    {
                        WaypointCategory.LooseLoot => allowLooseLoot,
                        WaypointCategory.ContainerLoot => allowContainerLoot,
                        WaypointCategory.Corpse => allowCorpse,
                        WaypointCategory.Synthetic => allowSynthetic,
                        _ => false, // Quest/Exfil never roam-targets
                    };
                    if (!ok) continue;
                    if (excludeIds != null && excludeIds.Contains(loc.Id)) continue;
                    if (squad != null && squad.CompletedPoiIds.Contains(loc.Id)) continue;
                    if (_claims.ContainsKey(loc.Id)) continue;
                    if (IsSquadKnownUnreachable(squad, loc.Id)) continue;
                    // XZ-only distance: the main anchor is Y=0 (CellToWorld
                    // / custom zones) while waypoints sit at their real
                    // ground Y, so a 3D check would systematically over-
                    // filter when the search centre is the anchor. Self-
                    // exclusion still uses memberPos in 3D since both
                    // ends are real ground points.
                    var distSqrCentre = XzDistanceSqr(loc.Position, searchCenter);
                    if (distSqrCentre > radSqr) continue;
                    var distSqrMember = (loc.Position - memberPos).sqrMagnitude;
                    if (distSqrMember <= selfExclusionDistSqr) continue;
                    candidates++;
                    if (Random.Range(0, candidates) == 0) best = loc;
                }
            }
        }
        return best;
    }

    /// <summary>
    /// Build a transient <see cref="Waypoint"/> at the given world
    /// position, intended as a one-shot dispatch target (used by the
    /// combat-convergence override to redirect every squad member to the
    /// in-combat member's position).
    /// </summary>
    public Waypoint CreateVirtualWaypoint(Vector3 worldPos, string nameTag)
    {
        return new Waypoint(
            NewRuntimeWaypointId(),
            WaypointCategory.Synthetic,
            nameTag,
            worldPos,
            radiusSqr: 4f * 4f, // 4m arrival — "we're close to the caller"
            doors: new List<Door>(),
            coverPoints: new List<CoverPoint>(),
            target: null);
    }

    public Waypoint FindLootSplinterForFollower(Waypoint mainObjective, Squad squad, HashSet<int> excludeIds, float radius)
    {
        if (mainObjective == null) return null;
        // Splinter-spread only makes sense when the squad's anchor is loot.
        // For Quest/Synthetic/Exfil the whole squad needs to converge.
        if (!IsLootCategory(mainObjective.Category)) return null;
        var center = WorldToCell(mainObjective.Position);
        var radSqr = radius * radius;
        // Two parallel best-picks so we prefer a different loot category
        // from the squad's anchor when possible — gives a 4-PMC squad with
        // a ContainerLoot anchor the chance to spread across container +
        // loose loot + corpse.
        Waypoint bestDiff = null;
        var bestDiffDist = float.MaxValue;
        Waypoint bestSame = null;
        var bestSameDist = float.MaxValue;
        for (var dx = -1; dx <= 1; dx++)
        {
            for (var dy = -1; dy <= 1; dy++)
            {
                var c = center + new Vector2Int(dx, dy);
                if (!IsValidCell(c)) continue;
                var locs = _cells[c.x, c.y].Waypoints;
                for (var i = 0; i < locs.Count; i++)
                {
                    var loc = locs[i];
                    if (!IsLootCategory(loc.Category)) continue;
                    if (loc.Id == mainObjective.Id) continue;
                    if (excludeIds != null && excludeIds.Contains(loc.Id)) continue;
                    if (squad != null && squad.CompletedPoiIds.Contains(loc.Id)) continue;
                    if (_claims.ContainsKey(loc.Id)) continue;
                    if (IsSquadKnownUnreachable(squad, loc.Id)) continue;
                    var distSqr = (loc.Position - mainObjective.Position).sqrMagnitude;
                    if (distSqr > radSqr) continue;
                    if (loc.Category != mainObjective.Category)
                    {
                        if (distSqr < bestDiffDist)
                        {
                            bestDiffDist = distSqr;
                            bestDiff = loc;
                        }
                    }
                    else if (distSqr < bestSameDist)
                    {
                        bestSameDist = distSqr;
                        bestSame = loc;
                    }
                }
            }
        }
        // Prefer different category if available; else fall back to same
        // category so we always return SOMETHING when there's eligible
        // loot around.
        return bestDiff ?? bestSame;
    }

    /// <summary>
    /// Map-wide search for the nearest Exfil waypoint the squad is
    /// eligible to use. Two-pass: pass 1 applies the full filter
    /// (faction + status + spawn-side entry); pass 2 drops the entry
    /// check so a squad that derived their EntryPoint wrong (or for
    /// whom no SpawnPointMarker could be resolved) still extracts.
    /// </summary>
    public Waypoint FindNearestEligibleExfil(Squad squad)
    {
        if (squad?.Leader?.Bot == null) return null;
        bool? squadIsPmc = null;
        var role = squad.Leader.Bot.Profile?.Info?.Settings?.Role;
        if (role.HasValue) squadIsPmc = role.Value.IsPMC();
        var leaderPos = squad.Leader.Bot.Position;

        Waypoint best = null;
        var bestDist = float.MaxValue;
        for (var cx = 0; cx < _gridSize.x; cx++)
        {
            for (var cy = 0; cy < _gridSize.y; cy++)
            {
                var locs = _cells[cx, cy].Waypoints;
                for (var i = 0; i < locs.Count; i++)
                {
                    var loc = locs[i];
                    if (loc.Category != WaypointCategory.Exfil) continue;
                    if (!SquadCanUseWaypoint(squad, squadIsPmc, loc)) continue;
                    var distSqr = (loc.Position - leaderPos).sqrMagnitude;
                    if (distSqr < bestDist)
                    {
                        bestDist = distSqr;
                        best = loc;
                    }
                }
            }
        }
        if (best != null) return best;

        // Pass 2: faction-only fallback. Skip MatchesBotSpawnEntry so a
        // stuck squad still gets a route out.
        for (var cx = 0; cx < _gridSize.x; cx++)
        {
            for (var cy = 0; cy < _gridSize.y; cy++)
            {
                var locs = _cells[cx, cy].Waypoints;
                for (var i = 0; i < locs.Count; i++)
                {
                    var loc = locs[i];
                    if (loc.Category != WaypointCategory.Exfil) continue;
                    if (!SquadCanUseWaypointIgnoringEntry(squad, squadIsPmc, loc)) continue;
                    var distSqr = (loc.Position - leaderPos).sqrMagnitude;
                    if (distSqr < bestDist)
                    {
                        bestDist = distSqr;
                        best = loc;
                    }
                }
            }
        }
        if (best != null)
        {
            Log.Warning($"{squad} no spawn-side eligible exfil — falling back to nearest faction-allowed exfil {best} (entry derivation may have failed)");
        }
        return best;
    }

    /// <summary>
    /// Rolls <see cref="Plugin.LootCoveragePct"/> against every loot
    /// waypoint in <paramref name="cellCoords"/> for the given squad.
    /// Each waypoint that loses the roll is added to
    /// <c>squad.CompletedPoiIds</c> so the dispatcher never sends a
    /// member to it. Simulates a real player walking past loot they
    /// didn't notice. Excludes already-blacklisted / claimed waypoints.
    /// Guarantees at least one waypoint survives so a small cell with
    /// bad luck doesn't auto-complete on entry.
    /// </summary>
    public void ApplyLootCoverageRollForCell(Squad squad, Vector2Int cellCoords)
    {
        if (squad == null) return;
        var coverageRaw = squad.Personality != null
            ? squad.Personality.LootCoverage
            : Plugin.LootCoveragePct.Value;
        var coverage = Mathf.Clamp01(coverageRaw);
        if (coverage >= 0.9999f) return; // feature disabled

        var keepOneFallback = (Waypoint)null;
        if (cellCoords.x < 0 || cellCoords.x >= _gridSize.x
            || cellCoords.y < 0 || cellCoords.y >= _gridSize.y) return;
        ref var cell = ref _cells[cellCoords.x, cellCoords.y];
        if (!cell.HasWaypoints) return;
        var waypoints = cell.Waypoints;
        var skipped = 0;
        var kept = 0;
        for (var i = 0; i < waypoints.Count; i++)
        {
            var loc = waypoints[i];
            if (loc.Category != WaypointCategory.ContainerLoot
                && loc.Category != WaypointCategory.LooseLoot
                && loc.Category != WaypointCategory.Corpse) continue;
            if (squad.CompletedPoiIds.Contains(loc.Id)) continue;
            if (_claims.ContainsKey(loc.Id)) continue;
            keepOneFallback ??= loc;
            if (Random.value >= coverage)
            {
                squad.CompletedPoiIds.Add(loc.Id);
                skipped++;
            }
            else
            {
                kept++;
            }
        }
        // Safety net: if the roll blacklisted everything (small cells with
        // bad luck), restore one so cell-clean completion doesn't fire on
        // the same tick the squad arrives.
        if (kept == 0 && keepOneFallback != null)
        {
            squad.CompletedPoiIds.Remove(keepOneFallback.Id);
            skipped--;
            kept = 1;
            Log.Info($"{squad} LootValue cell {cellCoords} coverage rolled 0 kept — restoring {keepOneFallback} as a guaranteed visit");
        }
        if (skipped > 0)
        {
            Log.Info($"{squad} LootValue cell {cellCoords} coverage roll: {kept} kept, {skipped} blacklisted (coverage={coverage:P0})");
        }
    }

    /// <summary>
    /// Clears the per-squad unreachability cache for every waypoint in
    /// the given cell. Called when a main objective transitions to "in
    /// progress" (LootValue cell entered / Kills roam phase armed) — at
    /// that point the squad's leader has physically reached the cell, so
    /// the old reachability verdict (computed from the spawn position via
    /// NavMesh.CalculatePath) is stale.
    /// </summary>
    public void ClearSquadUnreachabilityForCell(Squad squad, Vector2Int cellCoords)
    {
        if (squad == null) return;
        if (!_squadUnreachable.TryGetValue(squad.Id, out var set) || set.Count == 0) return;
        if (cellCoords.x < 0 || cellCoords.x >= _gridSize.x
            || cellCoords.y < 0 || cellCoords.y >= _gridSize.y) return;
        ref var cell = ref _cells[cellCoords.x, cellCoords.y];
        if (!cell.HasWaypoints) return;
        var waypoints = cell.Waypoints;
        var cleared = 0;
        for (var i = 0; i < waypoints.Count; i++)
        {
            if (set.Remove(waypoints[i].Id)) cleared++;
        }
        if (cleared > 0)
        {
            Log.Debug($"{squad} cell {cellCoords} re-evaluating reachability: cleared {cleared} stale waypoint(s) from per-squad unreachable cache");
        }
    }

    public Waypoint FindNearbySweepTarget(Vector3 botPos, Squad squad, float radius)
    {
        var center = WorldToCell(botPos);
        var radSqr = radius * radius;
        Waypoint best = null;
        var bestDist = float.MaxValue;
        for (var dx = -1; dx <= 1; dx++)
        {
            for (var dy = -1; dy <= 1; dy++)
            {
                var c = center + new Vector2Int(dx, dy);
                if (!IsValidCell(c)) continue;
                var locs = _cells[c.x, c.y].Waypoints;
                for (var i = 0; i < locs.Count; i++)
                {
                    var loc = locs[i];
                    if (loc.Category != WaypointCategory.LooseLoot && loc.Category != WaypointCategory.Corpse) continue;
                    if (squad != null && squad.CompletedPoiIds.Contains(loc.Id)) continue;
                    if (_claims.ContainsKey(loc.Id)) continue;
                    if (IsSquadKnownUnreachable(squad, loc.Id)) continue;
                    var distSqr = (loc.Position - botPos).sqrMagnitude;
                    if (distSqr > radSqr) continue;
                    if (distSqr < bestDist)
                    {
                        bestDist = distSqr;
                        best = loc;
                    }
                }
            }
        }
        return best;
    }

    /// <summary>
    /// Permanently removes a waypoint by id from its cell. Used to
    /// consume LooseLoot waypoints once they've been looted (the
    /// physical item is gone). Containers and corpses are NOT consumed
    /// this way — a second bot is allowed to walk to an emptied
    /// container and find nothing.
    /// </summary>
    public bool RemoveWaypoint(int waypointId)
    {
        if (!_waypointCells.Remove(waypointId, out var coords))
        {
            Log.Debug($"RemoveWaypoint: id={waypointId} not tracked, no-op");
            return false;
        }

        _claims.Remove(waypointId);
        _pathReachable.Remove(waypointId);
        foreach (var set in _squadUnreachable.Values) set.Remove(waypointId);
        _corpseKillerSquadId.Remove(waypointId);

        var waypoints = _cells[coords.x, coords.y].Waypoints;
        for (var i = waypoints.Count - 1; i >= 0; i--)
        {
            if (waypoints[i].Id == waypointId)
            {
                var removed = waypoints[i];
                Log.Debug($"RemoveWaypoint: {removed} removed from cell {coords}");
                if (removed.Target is LootItem li && li.Item != null)
                {
                    _lootItemIdToWaypointId.Remove(li.Item.Id);
                }
                waypoints.RemoveAt(i);
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Looks up the LooseLoot waypoint whose Target item has the given
    /// inventory Item.Id and removes it.
    /// </summary>
    public bool RemoveLooseLootByItemId(string itemId)
    {
        if (string.IsNullOrEmpty(itemId)) return false;
        if (!_lootItemIdToWaypointId.TryGetValue(itemId, out var waypointId)) return false;
        return RemoveWaypoint(waypointId);
    }

    /// <summary>
    /// Attempts to reserve a waypoint for an agent. Returns true if the
    /// claim was granted, false if another agent already holds it. Same
    /// agent re-claiming is idempotent.
    /// </summary>
    public bool TryClaim(int waypointId, int agentId)
    {
        if (_claims.TryGetValue(waypointId, out var holder))
        {
            var ok = holder == agentId;
            Log.Debug($"TryClaim: loc={waypointId} agent={agentId} already held by {holder} → {(ok ? "self-reclaim" : "DENIED")}");
            return ok;
        }
        _claims[waypointId] = agentId;
        Log.Debug($"TryClaim: loc={waypointId} agent={agentId} GRANTED");
        return true;
    }

    public void ReleaseClaim(int waypointId, int agentId)
    {
        if (_claims.TryGetValue(waypointId, out var holder) && holder == agentId)
        {
            _claims.Remove(waypointId);
            Log.Debug($"ReleaseClaim: loc={waypointId} agent={agentId} released");
        }
    }

    public bool IsClaimed(int waypointId) => _claims.ContainsKey(waypointId);

    private void RegisterWaypointInCell(Vector2Int coords, Waypoint loc)
    {
        _cells[coords.x, coords.y].Waypoints.Add(loc);
        _waypointCells[loc.Id] = coords;
        if (loc.Category == WaypointCategory.LooseLoot
            && loc.Target is LootItem li
            && li.Item != null)
        {
            _lootItemIdToWaypointId[li.Item.Id] = loc.Id;
        }
        // Subscribe to ExfiltrationPoint status changes so V-Ex (and other
        // one-shot exits) are pruned from the grid the moment they become
        // unusable. Also useful for logging the "real" exfil settings once
        // BSG has finished LoadSettings — our WaypointGatherer runs before
        // that, so at collection time Status=Pending and EligibleEntryPoints
        // is empty. The first non-Pending transition gives us the truth.
        if (loc.Category == WaypointCategory.Exfil && loc.Target is ExfiltrationPoint exfil)
        {
            var locId = loc.Id;
            Action<ExfiltrationPoint, EExfiltrationStatus> handler = null;
            handler = (point, oldStatus) =>
            {
                var entries = point.EligibleEntryPoints != null && point.EligibleEntryPoints.Length > 0
                    ? string.Join("/", point.EligibleEntryPoints)
                    : "<any>";
                Log.Info($"Exfil {point.name} status: {oldStatus} → {point.Status}, entries={entries}");

                if (point.Status != EExfiltrationStatus.NotPresent) return;
                Log.Debug($"Exfil {point.name} became NotPresent, pruning waypoint {locId}");
                RemoveWaypoint(locId);
                point.OnStatusChanged -= handler;
            };
            exfil.OnStatusChanged += handler;
        }
    }

    public void Return(Entity entity)
    {
        if (!_assignments.Remove(entity, out var coords))
            return;

        ref var cell = ref _cells[coords.x, coords.y];

        cell.Congestion--;
        PropagateForce(coords, -1f);

        if (cell.Congestion >= 0) return;

        cell.Congestion = 0;
        Log.Debug($"Returning the assignment for {entity} to the pool resulted in negative congestion");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsSquadPmc(Squad squad)
    {
        var role = squad?.Leader?.Bot?.Profile?.Info?.Settings?.Role;
        return role.HasValue && role.Value.IsPMC();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsLootCategory(WaypointCategory category)
    {
        return category == WaypointCategory.LooseLoot
               || category == WaypointCategory.ContainerLoot
               || category == WaypointCategory.Corpse;
    }

    // Runtime IDs come from NewRuntimeWaypointId (counter starts at 1M).
    // Currently only the corpse-registration patch issues them — i.e.
    // corpses created from a kill during the raid.
    private const int RuntimeWaypointIdStart = 1_000_000;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsRuntimeWaypoint(Waypoint loc) => loc.Id >= RuntimeWaypointIdStart;

    /// <summary>
    /// True if this cell has at least one runtime loot waypoint (i.e. a
    /// fresh kill happened here). When a squad kills someone in a cooled
    /// cell we want them to be able to loot the body AND grab the nearby
    /// static loose loot — a player would, the cell is now "interesting
    /// again". Once the runtime waypoint is consumed and aged out, the
    /// static-loot cooldown kicks back in.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool CellHasRuntimeLootPoi(in Cell cell)
    {
        var waypoints = cell.Waypoints;
        for (var i = 0; i < waypoints.Count; i++)
        {
            var loc = waypoints[i];
            if (IsLootCategory(loc.Category) && IsRuntimeWaypoint(loc)) return true;
        }
        return false;
    }

    /// <summary>
    /// True if the squad currently has at least one Kills main in roam
    /// phase. Used to suppress the PMC loot-cell-cooldown arming so a
    /// squad hunting around a Kills anchor doesn't lock themselves out
    /// of half their zone after picking up one item.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasActiveKillsRoamMain(Squad squad)
    {
        if (squad?.MainObjectives == null) return false;
        for (var i = 0; i < squad.MainObjectives.Count; i++)
        {
            var m = squad.MainObjectives[i];
            if (m == null || m.Completed) continue;
            if (m.Type == MainObjectiveType.Kills && m.KillsRoamStartedAt > 0f) return true;
        }
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsLootCooldownActive(Entity entity, Vector2Int coords)
    {
        if (entity is not Squad squad) return false;
        if (!IsSquadPmc(squad)) return false;
        if (!squad.LootCellCooldowns.TryGetValue(coords, out var expiry)) return false;
        if (Time.time >= expiry)
        {
            squad.LootCellCooldowns.Remove(coords);
            return false;
        }
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector2Int WorldToCell(Vector3 worldPos)
    {
        var x = Mathf.FloorToInt((worldPos.x - _worldMin.x) / _cellSize);
        var y = Mathf.FloorToInt((worldPos.z - _worldMin.y) / _cellSize);
        return new Vector2Int(x, y);
    }

    // Timmy "wrong cell" wander probability + forget-blacklist probability.
    // Hard-coded magnitudes (the F12 toggle controls whether they fire at
    // all; the magnitudes themselves aren't tuneable).
    private const float TimmyWrongCellProba = 0.20f;
    private const float TimmyForgetBlacklistProba = 0.05f;

    private static readonly Vector2Int[] _adjacentOffsets =
    {
        new(-1, -1), new(0, -1), new(1, -1),
        new(-1,  0),             new(1,  0),
        new(-1,  1), new(0,  1), new(1,  1),
    };

    private Vector2Int? PickRandomAdjacentValidCell(Vector2Int center)
    {
        // Walk the 8 neighbours in random order and return the first
        // in-bounds non-empty one. Skips empty cells because handing
        // Timmy a cell with nothing to do would mostly cause a stall.
        var indices = new int[_adjacentOffsets.Length];
        for (var i = 0; i < indices.Length; i++) indices[i] = i;
        for (var i = indices.Length - 1; i > 0; i--)
        {
            var j = Random.Range(0, i + 1);
            (indices[i], indices[j]) = (indices[j], indices[i]);
        }
        for (var i = 0; i < indices.Length; i++)
        {
            var off = _adjacentOffsets[indices[i]];
            var c = center + off;
            if (c.x < 0 || c.x >= _gridSize.x || c.y < 0 || c.y >= _gridSize.y) continue;
            if (!_cells[c.x, c.y].HasWaypoints) continue;
            return c;
        }
        return null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Vector2Int WorldToCell(Vector2 worldPos)
    {
        var x = Mathf.FloorToInt((worldPos.x - _worldMin.x) / _cellSize);
        var y = Mathf.FloorToInt((worldPos.y - _worldMin.y) / _cellSize);
        return new Vector2Int(x, y);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector3 CellToWorld(Vector2Int cell)
    {
        var x = _worldMin.x + (cell.x + 0.5f) * _cellSize;
        var z = _worldMin.y + (cell.y + 0.5f) * _cellSize;
        return new Vector3(x, 0, z);
    }

    /// <summary>
    /// Horizontal (XZ) squared distance between two world positions. Use
    /// this whenever you're comparing leader/agent against a main anchor
    /// — anchors come from <see cref="CellToWorld"/> or custom zones with
    /// <c>Y=0</c>, so a vanilla <c>Vector3.sqrMagnitude</c> inflates the
    /// result by the vertical offset. Cells are conceptually 2D so
    /// horizontal-only is the right semantic.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float XzDistanceSqr(Vector3 a, Vector3 b)
    {
        var dx = a.x - b.x;
        var dz = a.z - b.z;
        return dx * dx + dz * dz;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsValidCell(Vector2Int cell)
        => cell.x >= 0 && cell.x < _gridSize.x && cell.y >= 0 && cell.y < _gridSize.y;

    private Waypoint RequestFar(Entity entity)
    {
        // Walk the round-robin queue past cells whose waypoints are all
        // filtered for this entity (e.g. dead-end cells that only contain
        // an ineligible exfil). Capped at queue length so the squad
        // doesn't burn a full pass if literally every cell is unusable.
        var attempts = _validCellQueue.Count;
        Waypoint waypoint = null;
        Vector2Int pick = default;
        while (attempts-- > 0)
        {
            pick = _validCellQueue.Dequeue();
            _validCellQueue.Enqueue(pick);
            waypoint = AssignWaypoint(entity, pick);
            if (waypoint != null) break;
        }
        Log.Debug($"Requesting {waypoint} in far cell {pick}");
        return waypoint;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Waypoint AssignWaypoint(Entity entity, Vector2Int coords)
    {
        // Timmy extra: 20% chance to wander into a random adjacent cell
        // instead of the requested one. Simulates a noob bot getting
        // confused about which way they meant to go. PMC-only.
        if (entity is Squad sqWander
            && sqWander.Archetype == PersonalityArchetype.Timmy
            && (Plugin.SainTimmyExtrasEnabled?.Value ?? false)
            && Random.value < TimmyWrongCellProba)
        {
            var alt = PickRandomAdjacentValidCell(coords);
            if (alt.HasValue)
            {
                Log.Info($"{sqWander} Timmy wrong-cell pick: intended {coords} → wandered to adjacent {alt.Value}");
                coords = alt.Value;
            }
        }
        ref var cell = ref _cells[coords.x, coords.y];
        cell.Congestion += 1;
        PropagateForce(coords, 1f);
        _assignments[entity] = coords;
        Log.Debug($"Assigning waypoint in {coords}");
        var pick = PickFromCell(cell, entity, coords);
        if (pick != null && entity is Squad squad && IsSquadPmc(squad))
        {
            // The squad has left its previous loot cell behind — arm the
            // cooldown on it now so they don't get pulled back next
            // dispatch. While they remain in the same cell, loot picks
            // chain freely.
            //
            // EXCEPTION: while a Kills main is in roam phase, the squad is
            // supposed to oscillate around the anchor and may dip into the
            // same cell repeatedly. Arming a 10-min cooldown every time
            // they loot would lock them out of half their hunting ground.
            if (squad.LastLootCell.HasValue && squad.LastLootCell.Value != coords
                && !HasActiveKillsRoamMain(squad))
            {
                squad.LootCellCooldowns[squad.LastLootCell.Value] = Time.time + Plugin.PmcLootCellCooldownSeconds.Value;
                squad.LastLootCell = null;
            }
            if (IsLootCategory(pick.Category))
            {
                squad.LastLootCell = coords;
            }
            // If the picked waypoint sits behind one or more Locked doors,
            // roll for each door whether this squad is going to force it
            // open on arrival.
            RollForceUnlockForPick(squad, pick);
        }
        return pick;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RollForceUnlockForPick(Squad squad, Waypoint pick)
    {
        var doors = pick.LockedDoorsOnPath;
        if (doors == null || doors.Count == 0) return;

        var isMainAnchor = IsWaypointMainAnchorOfSquad(squad, pick);
        var intermediateRaw = squad.Personality != null
            ? squad.Personality.LockedDoorUnlockProba
            : Plugin.MainObjectivesUnlockProbabilityIntermediate.Value;
        var intermediateProba = Mathf.Clamp01(intermediateRaw);

        for (var i = 0; i < doors.Count; i++)
        {
            var door = doors[i];
            if (door == null) continue;
            // Door may have been unlocked since the LockedDoorsOnPath list
            // was built (e.g. by another squad's successful roll —
            // door.Unlock() flips world state for everyone). Skip silently:
            // no roll needed, HandleDoors takes the normal Open path.
            if (door.DoorState != EDoorState.Locked) continue;
            var doorId = door.GetInstanceID();
            if (squad.ForceUnlockDoorIds.Contains(doorId)) continue; // already rolled & granted

            // Already failed this door for this squad — don't re-roll.
            if (squad.FailedDoorUnlockIds.Contains(doorId))
            {
                squad.CompletedPoiIds.Add(pick.Id);
                Log.Info($"{squad} pick {pick} skipped — door {door.Id} previously failed for this squad, blacklisting waypoint");
                return;
            }

            var proba = isMainAnchor ? 1f : intermediateProba;
            if (proba <= 0f) continue;
            if (proba >= 1f || Random.value < proba)
            {
                squad.ForceUnlockDoorIds.Add(doorId);
                Log.Info($"{squad} granted force-unlock on door {door.Id} (instance {doorId}) for {pick} — {(isMainAnchor ? "MAIN anchor (100%)" : $"intermediate ({proba:F2})")}");
            }
            else
            {
                squad.FailedDoorUnlockIds.Add(doorId);
                squad.CompletedPoiIds.Add(pick.Id);
                Log.Info($"{squad} FAILED force-unlock roll on door {door.Id} ({proba:P0}) for {pick} — pick blacklisted, door marked failed (future picks behind this door filtered out)");
                return;
            }
        }
    }

    // Waypoint is filtered out if any door on its path is in the squad's
    // FailedDoorUnlockIds AND still in Locked state. A door that another
    // squad has since unlocked no longer counts.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasFailedDoorOnPath(Squad squad, Waypoint loc)
    {
        if (squad == null || squad.FailedDoorUnlockIds.Count == 0) return false;
        var doors = loc.LockedDoorsOnPath;
        if (doors == null || doors.Count == 0) return false;
        for (var i = 0; i < doors.Count; i++)
        {
            var door = doors[i];
            if (door == null) continue;
            if (door.DoorState != EDoorState.Locked) continue;
            if (squad.FailedDoorUnlockIds.Contains(door.GetInstanceID())) return true;
        }
        return false;
    }

    // Waypoint is the "main anchor" of the squad iff the squad has an
    // active (not Completed) main objective whose Position is very close
    // to the waypoint's. 5m tolerance for Quest (precise trigger), full
    // cell match for LootValue + Kills (loot anywhere in the cell counts).
    private const float MainAnchorTolerance = 5f;
    private static readonly float MainAnchorToleranceSqr = MainAnchorTolerance * MainAnchorTolerance;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsWaypointMainAnchorOfSquad(Squad squad, Waypoint loc)
    {
        var mains = squad.MainObjectives;
        if (mains == null || mains.Count == 0) return false;
        var locCell = WorldToCell(loc.Position);
        for (var i = 0; i < mains.Count; i++)
        {
            var m = mains[i];
            if (m == null || m.Completed) continue;
            // LootValue mains: any loot waypoint in the cell is an
            // "anchor" worth priority-picking. The 5m euclidean test
            // wasn't enough because m.Position = cell-center; waypoints
            // at the edge can be 30m+ from center and would lose the
            // priority pick.
            if (m.Type == MainObjectiveType.LootValue)
            {
                if (locCell == m.CellCoords
                    && (loc.Category == WaypointCategory.ContainerLoot
                        || loc.Category == WaypointCategory.LooseLoot
                        || loc.Category == WaypointCategory.Corpse))
                {
                    return true;
                }
                continue;
            }
            // Kills mains: roam phase triggers on cell entry. Priority
            // pick is restricted to LOOT waypoints — the bot biases toward
            // "actionable" targets in the cell. Synthetic patrol points
            // are intentionally NOT priority-picked: the priority path
            // bypasses RecentlyVisitedPoiCooldowns, so without this
            // restriction the same Synthetic gets re-picked every wait
            // tick. Synthetics still get picked via the reservoir-sample
            // path which respects the cooldown.
            if (m.Type == MainObjectiveType.Kills)
            {
                if (locCell == m.CellCoords
                    && (loc.Category == WaypointCategory.ContainerLoot
                        || loc.Category == WaypointCategory.LooseLoot
                        || loc.Category == WaypointCategory.Corpse))
                {
                    return true;
                }
                continue;
            }
            // Quest: m.Position is the precise trigger nav-point — 5m
            // gate keeps us pointed at the actual trigger, not a random
            // waypoint that happens to be in the same cell.
            if (XzDistanceSqr(m.Position, loc.Position) <= MainAnchorToleranceSqr) return true;
        }
        return false;
    }

    // Picks a Waypoint from the cell respecting per-squad filters
    // (blacklist, unreachable waypoint, detour cap, exfil eligibility,
    // PMC loot-cell cooldown). If everything is filtered, falls back to
    // a re-pick with only the *hard* constraints. Returns null only when
    // even the hard-constraint pass finds nothing.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Waypoint PickFromCell(in Cell cell, Entity entity, Vector2Int coords)
    {
        // Drain unreachable-waypoint removals queued by the previous
        // PickFromCell. We can't RemoveWaypoint mid-iteration (it mutates
        // cell.Waypoints), so detection enqueues and the next call cleans up.
        while (_pendingRemoval.Count > 0)
            RemoveWaypoint(_pendingRemoval.Dequeue());

        if (entity is Squad squad)
        {
            var hasBlacklist = squad.CompletedPoiIds.Count > 0;
            // Timmy extra: 5% chance to "forget" the blacklist for one
            // pick — re-considers waypoints the squad already cleared.
            if (hasBlacklist
                && squad.Archetype == PersonalityArchetype.Timmy
                && (Plugin.SainTimmyExtrasEnabled?.Value ?? false)
                && Random.value < TimmyForgetBlacklistProba)
            {
                hasBlacklist = false;
                Log.Debug($"{squad} Timmy forget-blacklist: ignoring CompletedPoiIds for this pick");
            }
            bool? squadIsPmc = null;
            var role = squad.Leader?.Bot?.Profile?.Info?.Settings?.Role;
            if (role.HasValue) squadIsPmc = role.Value.IsPMC();

            // Fresh kill in this cell lifts the loot-cooldown entirely (the
            // body AND nearby static loot both become valid again).
            var lootCooldownActive = IsLootCooldownActive(entity, coords)
                                     && !CellHasRuntimeLootPoi(cell);
            var corpseGate = CorpseRequiresSightOrSquadKillForSquad(squad);
            var waypoints = cell.Waypoints;

            // Strong bias toward "the body I dropped" — if this cell
            // contains a runtime corpse this squad is credited with the
            // kill on, pick it first.
            for (var i = 0; i < waypoints.Count; i++)
            {
                var loc = waypoints[i];
                if (loc.Category != WaypointCategory.Corpse) continue;
                if (!IsRuntimeWaypoint(loc)) continue;
                if (!WasCorpseKilledBySquad(loc.Id, squad.Id)) continue;
                if (hasBlacklist && squad.CompletedPoiIds.Contains(loc.Id)) continue;
                if (_claims.ContainsKey(loc.Id)) continue;
                if (HasFailedDoorOnPath(squad, loc)) continue;
                Log.Debug($"PickFromCell: {squad} priority-picked own kill {loc}");
                return loc;
            }

            // Main-anchor priority pick. If a waypoint in this cell is the
            // anchor of one of the squad's pending Main objectives, pick
            // THAT before falling back to the random reservoir sample.
            for (var i = 0; i < waypoints.Count; i++)
            {
                var loc = waypoints[i];
                if (!IsWaypointMainAnchorOfSquad(squad, loc)) continue;
                // Apply the standard hard filters before priority-picking.
                if (loc.Category == WaypointCategory.Quest && !SquadOwnsQuest(squad, loc)) continue;
                if (hasBlacklist && squad.CompletedPoiIds.Contains(loc.Id)) continue;
                if (_claims.ContainsKey(loc.Id)) continue;
                if (loc.LockedDoorsOnPath != null && loc.LockedDoorsOnPath.Count > 0
                    && squadIsPmc != true) continue;
                if (HasFailedDoorOnPath(squad, loc)) continue;
                if (RequiresReachabilityCheck(loc.Category) && !IsWaypointReachable(loc, squad)) continue;
                if (!SquadCanUseWaypoint(squad, squadIsPmc, loc)) continue;
                Log.Debug($"PickFromCell: {squad} priority-picked Main anchor {loc} (within 5m of an active Main)");
                return loc;
            }

            Waypoint pick = null;
            var candidates = 0;
            var skippedBlacklist = 0;
            var skippedExfil = 0;
            var skippedUnreachable = 0;
            var skippedTooFar = 0;
            var skippedLootCooldown = 0;
            var skippedCorpseHidden = 0;
            var skippedRecentVisit = 0;
            var skippedQuestNotMine = 0;
            var nowForVisitCheck = Time.time;
            for (var i = 0; i < waypoints.Count; i++)
            {
                var loc = waypoints[i];
                // Quest waypoints are reserved for main objectives.
                if (loc.Category == WaypointCategory.Quest
                    && !SquadOwnsQuest(squad, loc))
                {
                    skippedQuestNotMine++;
                    continue;
                }
                if (hasBlacklist && squad.CompletedPoiIds.Contains(loc.Id))
                {
                    skippedBlacklist++;
                    continue;
                }
                if (squad.RecentlyVisitedPoiCooldowns.TryGetValue(loc.Id, out var visitExpiry))
                {
                    if (nowForVisitCheck < visitExpiry)
                    {
                        skippedRecentVisit++;
                        continue;
                    }
                    squad.RecentlyVisitedPoiCooldowns.Remove(loc.Id);
                }
                if (lootCooldownActive && IsLootCategory(loc.Category))
                {
                    skippedLootCooldown++;
                    continue;
                }
                if (corpseGate && loc.Category == WaypointCategory.Corpse
                    && !WasCorpseKilledBySquad(loc.Id, squad.Id)
                    && !HasLineOfSightToCorpse(squad, loc))
                {
                    skippedCorpseHidden++;
                    continue;
                }
                if (RequiresReachabilityCheck(loc.Category) && !IsWaypointReachable(loc, squad))
                {
                    skippedUnreachable++;
                    continue;
                }
                if (loc.LockedDoorsOnPath != null && loc.LockedDoorsOnPath.Count > 0
                    && squadIsPmc != true)
                {
                    skippedUnreachable++;
                    continue;
                }
                if (HasFailedDoorOnPath(squad, loc))
                {
                    skippedUnreachable++;
                    continue;
                }
                if (!WithinLootDetourRange(loc, squad))
                {
                    skippedTooFar++;
                    continue;
                }
                if (!SquadCanUseWaypoint(squad, squadIsPmc, loc))
                {
                    skippedExfil++;
                    continue;
                }
                candidates++;
                if (Random.Range(0, candidates) == 0)
                    pick = loc;
            }
            if (pick != null)
            {
                if (skippedBlacklist + skippedExfil + skippedUnreachable + skippedTooFar + skippedLootCooldown + skippedCorpseHidden + skippedRecentVisit + skippedQuestNotMine > 0)
                {
                    Log.Debug($"PickFromCell: {squad} got {pick} after skipping {skippedBlacklist} blacklisted + {skippedExfil} ineligible exfil + {skippedUnreachable} unreachable + {skippedTooFar} too far + {skippedLootCooldown} loot-cooldown + {skippedCorpseHidden} corpse-hidden + {skippedRecentVisit} recent-visit + {skippedQuestNotMine} quest-not-mine");
                }
                return pick;
            }
            if (skippedBlacklist + skippedExfil + skippedUnreachable + skippedTooFar + skippedLootCooldown + skippedCorpseHidden + skippedRecentVisit + skippedQuestNotMine > 0)
            {
                Log.Debug($"PickFromCell: {squad} no eligible waypoint in cell ({skippedBlacklist} blacklisted, {skippedExfil} ineligible exfil, {skippedUnreachable} unreachable, {skippedTooFar} too far, {skippedLootCooldown} loot-cooldown, {skippedCorpseHidden} corpse-hidden, {skippedRecentVisit} recent-visit, {skippedQuestNotMine} quest-not-mine), falling back to hard-constraint pick");
            }

            // Fallback: re-pick relaxing ONLY the detour-distance cap.
            // Every other filter stays in effect to avoid sending bots to
            // genuinely unreachable / immersion-breaking targets.
            var fallbackCandidates = 0;
            Waypoint fallbackPick = null;
            for (var i = 0; i < waypoints.Count; i++)
            {
                var loc = waypoints[i];
                if (loc.Category == WaypointCategory.Quest
                    && !SquadOwnsQuest(squad, loc)) continue;
                if (hasBlacklist && squad.CompletedPoiIds.Contains(loc.Id)) continue;
                if (squad.RecentlyVisitedPoiCooldowns.TryGetValue(loc.Id, out var fallbackVisitExpiry)
                    && nowForVisitCheck < fallbackVisitExpiry) continue;
                if (lootCooldownActive && IsLootCategory(loc.Category)) continue;
                if (corpseGate && loc.Category == WaypointCategory.Corpse
                    && !WasCorpseKilledBySquad(loc.Id, squad.Id)
                    && !HasLineOfSightToCorpse(squad, loc)) continue;
                if (IsSquadKnownUnreachable(squad, loc.Id)) continue;
                if (!SquadCanUseWaypoint(squad, squadIsPmc, loc)) continue;
                fallbackCandidates++;
                if (Random.Range(0, fallbackCandidates) == 0)
                    fallbackPick = loc;
            }
            if (fallbackPick != null) return fallbackPick;
            return null;
        }
        return cell.Waypoints[Random.Range(0, cell.Waypoints.Count)];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool RequiresReachabilityCheck(WaypointCategory category)
    {
        // Every category where a bot has to physically reach the waypoint.
        // Exfil is excluded — exfils sit at well-known navmesh-anchored
        // points and have their own status/faction/entry filtering.
        // Synthetic is included: PopulateCell over-generates synthetic
        // candidates without a BFS pathing gate, so we rely on the per-
        // squad reachability cache to prune the ones that can't actually
        // be pathed to from the requesting bot's island.
        switch (category)
        {
            case WaypointCategory.LooseLoot:
            case WaypointCategory.ContainerLoot:
            case WaypointCategory.Corpse:
            case WaypointCategory.Quest:
            case WaypointCategory.Synthetic:
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Path-completeness check between two arbitrary world positions
    /// using the navmesh. Used by main-objective generation at squad
    /// creation to skip Quest anchors that aren't reachable from the
    /// squad's spawn position (bot can't ever reach the trigger, ends up
    /// roaming the map forever).
    /// </summary>
    public bool IsReachableFromPosition(Vector3 from, Vector3 to)
    {
        var path = new NavMeshPath();
        return NavMesh.CalculatePath(from, to, NavMesh.AllAreas, path)
               && path.status == NavMeshPathStatus.PathComplete;
    }

    private bool IsWaypointReachable(Waypoint loc, Squad squad)
    {
        if (_pathReachable.Contains(loc.Id)) return true;

        var squadId = squad?.Id ?? -1;
        HashSet<int> unreachableForSquad = null;
        if (squadId >= 0
            && _squadUnreachable.TryGetValue(squadId, out unreachableForSquad)
            && unreachableForSquad.Contains(loc.Id))
        {
            return false;
        }

        var leaderBot = squad?.Leader?.Bot;
        if (leaderBot == null) return true; // can't verify yet — let the caller proceed

        var path = new NavMeshPath();
        var reachable = NavMesh.CalculatePath(leaderBot.Position, loc.Position, NavMesh.AllAreas, path)
                        && path.status == NavMeshPathStatus.PathComplete;
        if (reachable)
        {
            _pathReachable.Add(loc.Id);
            return true;
        }

        // PathPartial / PathInvalid: the natural route is blocked. Before
        // giving up, check whether the waypoint is sitting behind one (or
        // several) Locked doors. Detection is squad-agnostic; per-squad
        // filtering happens later in PickFromCell.
        if (loc.LockedDoorsOnPath == null && CategoryAllowsLockedDoorBypass(loc.Category))
        {
            var nearbyLocked = CollectNearbyLockedDoors(loc.Position, LockedDoorDetectionRadius);
            if (nearbyLocked != null && nearbyLocked.Count > 0)
            {
                loc.LockedDoorsOnPath = nearbyLocked;
                _pathReachable.Add(loc.Id);
                Log.Debug($"{loc.Category} {loc} unreachable by direct path (status={path.status}) but {nearbyLocked.Count} Locked door(s) nearby — keeping as PMC force-unlock candidate (detected from {squad?.Leader})");
                return true;
            }
        }

        // No global removal: mark for THIS squad only. The old code queued
        // the waypoint for global removal here, which meant a single squad
        // with a bad spawn leader could purge waypoints that were perfectly
        // reachable from every other squad.
        if (squadId >= 0)
        {
            if (unreachableForSquad == null)
            {
                unreachableForSquad = new HashSet<int>();
                _squadUnreachable[squadId] = unreachableForSquad;
            }
            unreachableForSquad.Add(loc.Id);
        }
        Log.Debug($"{loc.Category} {loc} unreachable from {squad?.Leader} pos {leaderBot.Position} (path status={path.status}) — per-squad cache, not globally purged");
        return false;
    }

    // How wide to look for Locked doors around an unreachable waypoint.
    // Bigger than the 3m loot-arrival radius because a marked-room loot
    // pile can sit several metres inside the room, away from the door.
    // Empirically 12m covers any reasonable Tarkov room.
    private const float LockedDoorDetectionRadius = 12f;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsSquadKnownUnreachable(Squad squad, int locId)
    {
        if (squad == null) return false;
        return _squadUnreachable.TryGetValue(squad.Id, out var set) && set.Contains(locId);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool CategoryAllowsLockedDoorBypass(WaypointCategory category)
    {
        // Lock-bypass currently only applies to loot/quest categories —
        // corpses and synthetics behind locked doors are corner cases not
        // worth widening the surface for. Squad-faction filtering happens
        // separately in PickFromCell.
        return category == WaypointCategory.ContainerLoot
            || category == WaypointCategory.LooseLoot
            || category == WaypointCategory.Quest;
    }

    /// <summary>
    /// Returns every Locked door within <paramref name="radius"/> of
    /// <paramref name="position"/>. Scene scan is cached after the first
    /// call (door instance set is fixed at raid start, locked state can
    /// change at runtime but the *candidate* list is bounded by what was
    /// Locked at raid start).
    /// </summary>
    private List<Door> CollectNearbyLockedDoors(Vector3 position, float radius)
    {
        if (_rawLockedDoors == null)
        {
            _rawLockedDoors = new List<Door>();
            foreach (var d in UnityEngine.Object.FindObjectsOfType<Door>())
            {
                if (d != null && d.DoorState == EDoorState.Locked)
                    _rawLockedDoors.Add(d);
            }
            Log.Debug($"WaypointSystem: cached {_rawLockedDoors.Count} initially-Locked doors for unlock candidate scans");
        }
        if (_rawLockedDoors.Count == 0) return null;
        List<Door> hits = null;
        var radiusSqr = radius * radius;
        for (var i = 0; i < _rawLockedDoors.Count; i++)
        {
            var d = _rawLockedDoors[i];
            if (d == null) continue;
            // Re-check current state — door may have been unlocked since.
            if (d.DoorState != EDoorState.Locked) continue;
            if ((d.transform.position - position).sqrMagnitude > radiusSqr) continue;
            hits ??= new List<Door>(2);
            hits.Add(d);
        }
        return hits;
    }

    /// <summary>
    /// Caps how far a squad will detour for a lootable waypoint. Without
    /// this gate the dispatcher can send a bot 300m+ across the map for a
    /// single magazine, which is both immersion-breaking and a frequent
    /// stuck/teleport trigger. Distances come from F12 config (default
    /// ~80m). Non-loot categories pass through.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool WithinLootDetourRange(Waypoint loc, Squad squad)
    {
        float maxDist;
        switch (loc.Category)
        {
            case WaypointCategory.ContainerLoot:
                maxDist = LootConfig.DetectContainerDistance?.Value ?? float.MaxValue;
                break;
            case WaypointCategory.LooseLoot:
                maxDist = LootConfig.DetectItemDistance?.Value ?? float.MaxValue;
                break;
            case WaypointCategory.Corpse:
                maxDist = LootConfig.DetectCorpseDistance?.Value ?? float.MaxValue;
                break;
            default:
                return true; // Quest/Synthetic/Exfil aren't loot — no detour cap
        }

        var leaderBot = squad?.Leader?.Bot;
        if (leaderBot == null) return true;

        var distSqr = (leaderBot.Position - loc.Position).sqrMagnitude;
        return distSqr <= maxDist * maxDist;
    }

    /// <summary>
    /// Combined availability + co-op + faction check for Exfil waypoints.
    /// 1. Filters exfils whose live BSG status is unusable (NotPresent / Hidden / etc).
    /// 2. Filters co-op exfils — bots can't coordinate cross-faction in SPT.
    /// 3. PMC squads only get PMC-accessible exfils, scav squads only scav.
    /// 4. Reserve is blanket-blocked (every exfil there is conditional).
    /// Non-Exfil waypoints always pass.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool SquadCanUseWaypoint(Squad squad, bool? squadIsPmc, Waypoint loc)
    {
        if (loc.Category != WaypointCategory.Exfil) return true;
        if (loc.Target is not ExfiltrationPoint exfil) return true;

        // Faction-level extract gate. Scavs / bloodhounds / raiders / bosses
        // / Goons normally don't extract in Tarkov — they despawn, stay on
        // the map, or leave on a script.
        var role = squad?.Leader?.Bot?.Profile?.Info?.Settings?.Role;
        if (!role.HasValue) return false;
        if (!(LootConfig.ExtractAllowedFor?.Value ?? ExtractFaction.All).IsBotEnabled(role.Value)) return false;

        // Reserve blanket-block: every exfil on this map is conditional
        // (D-2 power+key, Hermetic Door power+key, Train timed, Sewer
        // Manhole, Cliff Descent w/ Paracord+Red Rebel...). Bots can't
        // satisfy any of them.
        if (string.Equals(_mapId, "RezervBase", StringComparison.OrdinalIgnoreCase))
            return false;

        // Live availability check.
        if (exfil.Status == EExfiltrationStatus.NotPresent
            || exfil.Status == EExfiltrationStatus.Hidden
            || exfil.Status == EExfiltrationStatus.AwaitsManualActivation)
            return false;

        // UncompleteRequirements is BSG's initial status for SharedTimer
        // exfils (V-Ex / BTR — legitimate, the extract routine has a
        // dedicated wait+countdown flow for them) AND for some requirement-
        // gated exfils. Allow it only when the type is SharedTimer.
        if (exfil.Status == EExfiltrationStatus.UncompleteRequirements
            && exfil.Settings.ExfiltrationType != EExfiltrationType.SharedTimer)
            return false;

        // Skip co-op extracts and other requirements bots can't satisfy.
        if (HasBotUnreachableRequirement(exfil))
            return false;

        // Mirror BSG's player-side extract filter: an exfil only matches an
        // agent whose spawn EntryPoint is in the exfil's EligibleEntryPoints.
        if (!MatchesBotSpawnEntry(squad, exfil))
            return false;

        // Strict on unknown faction: rejecting here costs at worst a brief
        // window where new squads can't be assigned exfils — they fall back
        // to loot/quest/synthetic and pick up exfils on the next dispatch
        // once Role is available.
        if (!squadIsPmc.HasValue) return false;

        // BSG hierarchy: SharedExfiltrationPoint : ScavExfiltrationPoint : ExfiltrationPoint
        if (exfil is SharedExfiltrationPoint) return true;
        if (exfil is ScavExfiltrationPoint) return !squadIsPmc.Value;
        return squadIsPmc.Value;
    }

    /// <summary>
    /// True if the exfil carries any <see cref="ERequirementState"/> that
    /// a bot can't satisfy autonomously (item handover, world-event
    /// switch, keycard, train, co-op). Backpack-state requirements
    /// (Empty / NotEmpty / EmptyOrSize) are tolerated — trivial gates a
    /// bot might satisfy by accident.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasBotUnreachableRequirement(ExfiltrationPoint exfil)
    {
        var reqs = exfil.Requirements;
        if (reqs == null) return false;
        for (var i = 0; i < reqs.Length; i++)
        {
            if (reqs[i] == null) continue;
            switch (reqs[i].Requirement)
            {
                case ERequirementState.ScavCooperation:
                case ERequirementState.WorldEvent:
                case ERequirementState.TransferItem:
                case ERequirementState.SecretTransferItem:
                case ERequirementState.HasItem:
                case ERequirementState.WearsItem:
                case ERequirementState.Reference:
                case ERequirementState.Train:
                case ERequirementState.SkillLevel:
                case ERequirementState.Timer:
                    return true;
            }
        }
        return false;
    }

    // Same gates as SquadCanUseWaypoint but skips MatchesBotSpawnEntry.
    // Used by FindNearestEligibleExfil's Pass 2 fallback when the full
    // spawn-side filter returned no exfil — better to extract on the
    // wrong side than stay stuck forever.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool SquadCanUseWaypointIgnoringEntry(Squad squad, bool? squadIsPmc, Waypoint loc)
    {
        if (loc.Category != WaypointCategory.Exfil) return true;
        if (loc.Target is not ExfiltrationPoint exfil) return true;
        var role = squad?.Leader?.Bot?.Profile?.Info?.Settings?.Role;
        if (!role.HasValue) return false;
        if (!(LootConfig.ExtractAllowedFor?.Value ?? ExtractFaction.All).IsBotEnabled(role.Value)) return false;
        if (string.Equals(_mapId, "RezervBase", StringComparison.OrdinalIgnoreCase)) return false;
        if (exfil.Status == EExfiltrationStatus.NotPresent
            || exfil.Status == EExfiltrationStatus.Hidden
            || exfil.Status == EExfiltrationStatus.AwaitsManualActivation) return false;
        if (exfil.Status == EExfiltrationStatus.UncompleteRequirements
            && exfil.Settings.ExfiltrationType != EExfiltrationType.SharedTimer) return false;
        if (HasBotUnreachableRequirement(exfil)) return false;
        if (!squadIsPmc.HasValue) return false;
        if (exfil is SharedExfiltrationPoint) return true;
        if (exfil is ScavExfiltrationPoint) return !squadIsPmc.Value;
        return squadIsPmc.Value;
    }

    private static bool MatchesBotSpawnEntry(Squad squad, ExfiltrationPoint exfil)
    {
        var leader = squad?.Leader?.Bot;
        if (leader == null) return true;

        // Scavs aren't filtered by spawn entry in BSG.
        if (leader.Profile?.Info?.Side == EPlayerSide.Savage) return true;

        var eligible = exfil.EligibleEntryPoints;
        var exfilUnrestricted = eligible == null || eligible.Length == 0;
        if (exfilUnrestricted) return true;

        var entry = leader.Profile?.Info?.EntryPoint;
        if (string.IsNullOrEmpty(entry))
        {
            // PMC bots spawned by mods (Legion / UNTAR / RUAF) and special-
            // spawn vanilla bots (Labs-Guard-XX) often have an empty
            // Profile.Info.EntryPoint. Derive the closest SpawnPointMarker's
            // Infiltration from the squad's captured spawn position.
            entry = ResolveDerivedEntryPoint(squad);
            if (string.IsNullOrEmpty(entry))
            {
                // No marker resolved — reject restricted exfils so a bot
                // with truly no spawn data doesn't walk into the wrong-
                // side exfil.
                return false;
            }
        }

        var entryLower = entry.ToLowerInvariant();
        for (var i = 0; i < eligible.Length; i++)
        {
            if (eligible[i] == entryLower) return true;
        }
        return false;
    }

    private static string ResolveDerivedEntryPoint(Squad squad)
    {
        if (squad == null) return null;
        if (squad.DerivedEntryPoint != null) return squad.DerivedEntryPoint;

        var spawnPos = squad.SpawnPosition;
        if (spawnPos == Vector3.zero)
        {
            squad.DerivedEntryPoint = string.Empty;
            return string.Empty;
        }

        EFT.Game.Spawning.SpawnPointMarker bestMarker = null;
        var bestDistSqr = float.MaxValue;
        var markers = UnityEngine.Object.FindObjectsOfType<EFT.Game.Spawning.SpawnPointMarker>();
        for (var i = 0; i < markers.Length; i++)
        {
            var m = markers[i];
            if (m == null || m.SpawnPoint == null) continue;
            var infiltration = m.SpawnPoint.Infiltration;
            if (string.IsNullOrEmpty(infiltration)) continue;
            var distSqr = (m.Position - spawnPos).sqrMagnitude;
            if (distSqr < bestDistSqr)
            {
                bestDistSqr = distSqr;
                bestMarker = m;
            }
        }

        var derived = bestMarker?.SpawnPoint?.Infiltration ?? string.Empty;
        squad.DerivedEntryPoint = derived;
        Log.Info($"{squad} derived EntryPoint='{derived}' from spawn pos {spawnPos} (closest SpawnPointMarker {Mathf.Sqrt(bestDistSqr):F1}m away)");
        return derived;
    }

    // ── Main-objective anchor selection helpers ──────────────────────
    //
    // When generating a squad's main objectives, the anchor for a Kills
    // main comes from a positive-Force zone in the current map's Config;
    // the anchor for a LootValue main comes from a top-quartile cell by
    // total Container + LooseLoot value. Both helpers are called from the
    // main objective builder.

    /// <summary>
    /// World positions of every map zone with a positive Force (max > 0).
    /// Builtin zones resolve through BSG's BotSpawner.AllBotZones by name;
    /// custom zones come from the explicit Position in Config. Returns
    /// an empty list when the map has no PvP zones configured.
    /// </summary>
    public List<Vector3> GetPositiveForceZoneAnchors()
    {
        var result = new List<Vector3>();
        var botZones = _botsController?.BotSpawner?.AllBotZones;
        if (botZones != null)
        {
            for (var i = 0; i < botZones.Length; i++)
            {
                var botZone = botZones[i];
                if (botZone == null) continue;
                if (!_zoneConfig.Value.BuiltinZones.TryGetValue(botZone.name, out var builtin)) continue;
                if (builtin.Force.Max <= 0f) continue;
                result.Add(botZone.CenterOfSpawnPoints);
            }
        }
        for (var i = 0; i < _zoneConfig.Value.CustomZones.Count; i++)
        {
            var customZone = _zoneConfig.Value.CustomZones[i];
            if (customZone.Force.Max <= 0f) continue;
            // CustomZone.Position is (x, z) in world coords; y unused
            // (NavMesh-sample at consumption time if needed). Force
            // attraction is purely 2D anyway.
            result.Add(new Vector3(customZone.Position.x, 0f, customZone.Position.y));
        }
        return result;
    }

    private List<Vector2Int> _topLootCellsCache;

    /// <summary>
    /// Up to <c>Plugin.MainObjectivesTopLootCellsMaxCount</c> cells ranked
    /// by total rouble value of their ContainerLoot + LooseLoot waypoints
    /// (real handbook prices via <see cref="SumCellLootValue"/>). Lazy on
    /// first call; cached for the raid. No mutual exclusion: multiple
    /// squads can roll the same cell, which concentrates PvP at hotspots.
    /// </summary>
    public List<Vector2Int> GetTopLootCells()
    {
        if (_topLootCellsCache != null) return _topLootCellsCache;

        var cellValues = new List<KeyValuePair<Vector2Int, float>>();
        for (var x = 0; x < _gridSize.x; x++)
        {
            for (var y = 0; y < _gridSize.y; y++)
            {
                ref var cell = ref _cells[x, y];
                if (!cell.HasWaypoints) continue;
                var coords = new Vector2Int(x, y);
                var v = SumCellLootValue(coords);
                if (v <= 0f)
                {
                    // Appraiser not ready yet — fall back to item-count.
                    for (var i = 0; i < cell.Waypoints.Count; i++)
                    {
                        var loc = cell.Waypoints[i];
                        if (loc.Category != WaypointCategory.ContainerLoot
                            && loc.Category != WaypointCategory.LooseLoot) continue;
                        v += EstimateWaypointValue(loc);
                    }
                }
                if (v > 0f) cellValues.Add(new KeyValuePair<Vector2Int, float>(coords, v));
            }
        }
        cellValues.Sort((a, b) => b.Value.CompareTo(a.Value));
        var keep = Math.Min(Plugin.MainObjectivesTopLootCellsMaxCount.Value, cellValues.Count);
        _topLootCellsCache = new List<Vector2Int>(keep);
        for (var i = 0; i < keep; i++) _topLootCellsCache.Add(cellValues[i].Key);
        var summary = new System.Text.StringBuilder();
        for (var i = 0; i < keep; i++)
        {
            if (i > 0) summary.Append(", ");
            summary.Append($"{cellValues[i].Key}=₽{cellValues[i].Value:F0}");
        }
        Log.Info($"GetTopLootCells: ranked {cellValues.Count} loot-bearing cells, kept top {keep} — [{summary}]");
        return _topLootCellsCache;
    }

    /// <summary>
    /// Sum of <see cref="ItemValuator"/> handbook prices for every item
    /// in every Container + LooseLoot waypoint of the cell. Returns 0 if
    /// the valuator isn't initialised yet (raid still loading) or the
    /// cell has no loot waypoints.
    /// </summary>
    public float SumCellLootValue(Vector2Int cell)
    {
        if (!IsValidCell(cell)) return 0f;
        if (LootConfig.ItemValuator == null) return 0f;
        ref var cellRef = ref _cells[cell.x, cell.y];
        if (!cellRef.HasWaypoints) return 0f;
        var sum = 0f;
        for (var i = 0; i < cellRef.Waypoints.Count; i++)
        {
            var loc = cellRef.Waypoints[i];
            if (loc.Category != WaypointCategory.ContainerLoot
                && loc.Category != WaypointCategory.LooseLoot) continue;
            sum += SumWaypointRoubleValue(loc);
        }
        return sum;
    }

    private static float SumWaypointRoubleValue(Waypoint loc)
    {
        if (loc.Target == null) return 0f;
        try
        {
            if (loc.Target is LootItem li && li.Item != null)
            {
                return LootConfig.ItemValuator.GetItemPrice(li.Item, null);
            }
            if (loc.Target is LootableContainer container
                && container.ItemOwner?.RootItem is SearchableItemItemClass searchable)
            {
                var sum = 0f;
                var grids = searchable.Grids;
                if (grids != null)
                {
                    for (var g = 0; g < grids.Length; g++)
                    {
                        var grid = grids[g];
                        if (grid?.Items == null) continue;
                        foreach (var item in grid.Items)
                        {
                            if (item == null) continue;
                            sum += LootConfig.ItemValuator.GetItemPrice(item, null);
                        }
                    }
                }
                return sum;
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"SumWaypointRoubleValue: valuator threw on {loc} — {ex.Message}");
        }
        return 0f;
    }

    // Cell-ranking proxy used by GetTopLootCells — keeps the cheap item-
    // count heuristic so we don't run the valuator N times per cell during
    // the top-quartile sort when prices aren't ready.
    private static float EstimateWaypointValue(Waypoint loc)
    {
        if (loc.Target == null) return 0f;
        if (loc.Target is LootItem li && li.Item != null) return 1f;
        if (loc.Target is LootableContainer container
            && container.ItemOwner?.RootItem is SearchableItemItemClass searchable)
        {
            var count = 0;
            var grids = searchable.Grids;
            if (grids != null)
            {
                for (var g = 0; g < grids.Length; g++)
                {
                    var grid = grids[g];
                    if (grid?.Items == null) continue;
                    foreach (var item in grid.Items)
                    {
                        if (item != null) count++;
                    }
                }
            }
            return count;
        }
        return 0f;
    }

    private void PropagateForce(Vector2Int sourceCoords, float forceMul, int range = 3)
    {
        const float baseForce = 0.5f;
        var maxForce = forceMul * baseForce;

        for (var dx = -range; dx <= range; dx++)
        {
            for (var dy = -range; dy <= range; dy++)
            {
                if (dx == 0 && dy == 0) continue;

                var targetCoords = new Vector2Int(sourceCoords.x + dx, sourceCoords.y + dy);

                if (!IsValidCell(targetCoords)) continue;

                var direction = new Vector2(dx, dy);
                var distanceNorm = direction.sqrMagnitude;

                var force = direction.normalized * maxForce / distanceNorm;

                _advectionField[targetCoords.x, targetCoords.y] += force;
            }
        }
    }

    private bool PopulateCell(Cell cell, Vector2Int cellCoords, Vector3 centerPoint)
    {
        var pointsFound = 0;

        // 5×5 grid (25 candidates) spread across the FULL cell. cellSize/4
        // spacing matches the density of inner-quarter sampling while
        // covering the full cell area (corners and edges get coverage).
        const float resolution = 5;
        var spacing = _cellSize / (resolution - 1);
        var halfSize = _cellSize / 2f;
        // Sample radius scaled to spacing: each candidate "owns" a patch
        // of roughly spacing × spacing.
        var sampleRadius = spacing;
        var maxHitDistanceSqr = spacing * spacing;
        // Spatial exclusion radius around existing builtin waypoints
        // (ContainerLoot, LooseLoot, Quest, Exfil). Synthetic candidates
        // that fall closer than this to any builtin are skipped —
        // otherwise the cell ends up with so many synthetic patrol points
        // that PickFromCell's reservoir sample drowns out the real loot/
        // quest target.
        const float poiExclusionRadius = 20f;
        var poiExclusionRadiusSqr = poiExclusionRadius * poiExclusionRadius;

        for (var z = 0; z < resolution; z++)
        {
            for (var x = 0; x < resolution; x++)
            {
                var xOffset = x * spacing - halfSize;
                var zOffset = z * spacing - halfSize;

                var candidatePoint = new Vector3(centerPoint.x + xOffset, centerPoint.y, centerPoint.z + zOffset);

                if (!NavMesh.SamplePosition(candidatePoint, out var hit, sampleRadius, NavMesh.AllAreas))
                    continue;

                if (WorldToCell(hit.position) != cellCoords)
                    continue;

                // Reject hits that snapped far from the candidate — those
                // are the navmesh-extends-past-map-edge artefacts.
                if ((hit.position - candidatePoint).sqrMagnitude > maxHitDistanceSqr)
                    continue;

                if (IsTooCloseToBuiltinPoi(cell, hit.position, poiExclusionRadiusSqr))
                    continue;

                RegisterWaypointInCell(cellCoords, _waypointGatherer.CreateSyntheticWaypoint(hit.position));
                pointsFound++;
            }
        }

        Log.Debug($"Cell {cellCoords}: found a total of {pointsFound} synthetic points");

        return pointsFound > 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsTooCloseToBuiltinPoi(Cell cell, Vector3 hit, float maxDistSqr)
    {
        var locs = cell.Waypoints;
        for (var i = 0; i < locs.Count; i++)
        {
            var existing = locs[i];
            switch (existing.Category)
            {
                case WaypointCategory.ContainerLoot:
                case WaypointCategory.LooseLoot:
                case WaypointCategory.Quest:
                case WaypointCategory.Exfil:
                    if ((existing.Position - hit).sqrMagnitude < maxDistSqr) return true;
                    break;
            }
        }
        return false;
    }

    public readonly struct Zone(Vector2Int coords, float radius, float force, float decay)
    {
        public readonly Vector2Int Coords = coords;
        public readonly float Radius = radius;
        public readonly float Force = force;
        public readonly float Decay = decay;

        public override string ToString()
            => $"Zone(position: {Coords}, radius: {Radius}, force: {Force}, decay: {Decay})";
    }
}
