using System;
using System.Collections.Generic;
using System.Linq;
using EFT;
using EFT.Interactive;
using Orbit.Helpers;
using UnityEngine;
using UnityEngine.AI;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace Orbit.Navigation;

/// <summary>
/// Boot-time scrape of every interesting world object on the map into the
/// initial waypoint set: quest triggers, lootable containers, loose loot
/// items, exfiltration points. Each candidate is sampled against the
/// navmesh and dropped if the bot can't physically stand near it.
/// Corpses are added at runtime (see CorpseRegistrationPatch), so this
/// gatherer doesn't touch them.
/// </summary>
public class WaypointGatherer(float cellSize, BotsController botsController)
{
    private static int _idCounter;

    public List<Waypoint> CollectBuiltinWaypoints()
    {
        var collection = new List<Waypoint>();

        Log.Debug("Collecting quests POIs");

        _idCounter = 0;

        var beforeQuest = collection.Count;
        foreach (var trigger in Object.FindObjectsOfType<TriggerWithId>())
        {
            if (trigger.transform == null)
                continue;

            // trigger.transform.position is often in mid-air or off-mesh
            // (the anchor point of a tall trigger volume), which makes the
            // 2 m navmesh sample fail. Prefer the collider's bounds.center,
            // biased toward the floor for tall volumes (the floor inside is
            // where a bot would actually stand), and widen the sample
            // radius for big volumes so long quest zones (Customs office
            // building, Streets sectors…) stay usable.
            var position = trigger.transform.position;
            var maxNavDist = 2f;
            var triggerCollider = trigger.GetComponent<Collider>();
            if (triggerCollider != null)
            {
                var bounds = triggerCollider.bounds;
                position = bounds.center;
                // Tall volume → snap toward the floor.
                if (bounds.extents.y > 1.5f)
                {
                    position.y = bounds.min.y + 0.75f;
                }
                // Big volume → wider sample.
                if (bounds.size.sqrMagnitude > 100f) maxNavDist = 10f;
            }
            ValidateAndAddWaypoint(collection, WaypointCategory.Quest, trigger.name, position, maxNavDist);
        }
        Log.Debug($"Quest POIs collected: {collection.Count - beforeQuest}");

        var beforeContainer = collection.Count;
        foreach (var container in Object.FindObjectsOfType<LootableContainer>())
        {
            if (container.transform == null || !container.enabled || container.Template == null)
                continue;

            ValidateAndAddWaypoint(collection, WaypointCategory.ContainerLoot, container.name, container.transform.position, target: container);
        }
        Log.Debug($"ContainerLoot POIs collected: {collection.Count - beforeContainer}");

        var beforeLoose = collection.Count;
        var lootItemsSeen = 0;
        var lootItemsSkippedQuest = 0;
        foreach (var lootItem in Object.FindObjectsOfType<LootItem>())
        {
            if (lootItem.transform == null || lootItem.Item == null)
                continue;
            lootItemsSeen++;
            if (lootItem.Item.QuestItem)
            {
                lootItemsSkippedQuest++;
                continue;
            }

            ValidateAndAddWaypoint(collection, WaypointCategory.LooseLoot, lootItem.name, lootItem.transform.position, target: lootItem);
        }
        Log.Debug($"LooseLoot POIs collected: {collection.Count - beforeLoose} (LootItem objects scanned: {lootItemsSeen}, skipped quest: {lootItemsSkippedQuest})");

        Log.Debug("Collecting exfil POIs");

        // Collect every ExfiltrationPoint regardless of faction — we want both
        // PMC and Scav extracts available in the POI grid. PickFromCell does
        // the faction filtering at assignment time.
        var uniqueExfils = new HashSet<Exfil>();

        foreach (var point in LocationScene.GetAllObjects<ExfiltrationPoint>())
        {
            uniqueExfils.Add(new Exfil(point));
        }

        Log.Debug($"Found {uniqueExfils.Count} exfils");

        foreach (var exfil in uniqueExfils)
        {
            var access = exfil.Point is SharedExfiltrationPoint
                ? "shared"
                : exfil.Point is ScavExfiltrationPoint
                    ? "scav-only"
                    : "pmc-only";
            var coop = HasScavCoopRequirement(exfil.Point) ? " co-op" : "";
            var entries = exfil.Point.EligibleEntryPoints != null && exfil.Point.EligibleEntryPoints.Length > 0
                ? string.Join("/", exfil.Point.EligibleEntryPoints)
                : "<any>";
            Log.Debug($"Trying to add exfil {exfil.Point.name} [{access}{coop}] status={exfil.Point.Status} entries={entries}");
            ValidateAndAddWaypoint(collection, WaypointCategory.Exfil, exfil.Point.name, exfil.Point.transform.position, 5f, target: exfil.Point);
        }

        Log.Debug($"Collected {collection.Count} points of interest");

        Shuffle(collection);

        return collection;
    }

    public Waypoint CreateSyntheticWaypoint(Vector3 position)
    {
        var radius = Mathf.Clamp(cellSize / 2f, 10f, 25f);
        var radiusSqr = radius * radius;
        var name = $"Synthetic_{_idCounter}";
        const WaypointCategory category = WaypointCategory.Synthetic;

        var coverData = CollectBuiltinCoverData(position, radius);

        if (coverData.CoverPoints.Count < 16)
        {
            var extraPoints = 16 - coverData.CoverPoints.Count;
            CollectSyntheticCoverData(coverData.CoverPoints, position, radius, extraPoints);
        }

        if (coverData.CoverPoints.Count == 0)
        {
            Log.Debug($"Waypoint {category}:{name} has 0 cover points in proximity");
        }

        var waypoint = new Waypoint(_idCounter, category, name, position, radiusSqr, coverData.Doors, coverData.CoverPoints, target: null);
        _idCounter++;
        return waypoint;
    }

    private Waypoint CreateBuiltinWaypoint(WaypointCategory category, string name, Vector3 position, MonoBehaviour target)
    {
        // Arrival radii for "real" targets. 1m euclidean — matches BSG's
        // native interaction range. Wall-through false-arrivals are
        // prevented by the 3D Physics.Raycast (vs.
        // LayerMaskClass.HighPolyWithTerrainMask) gate in
        // GotoObjectiveAction, same raycast BSG uses for cover/vision.
        // BSG's BotMover nav-snap drift (1.5-2.5m off Waypoint.Position)
        // is absorbed by GotoObjectiveAction's nav-snap rescue path
        // (Stopped + ≤4m + raycast clear → accept arrival).
        //
        // Synthetic (patrol filler) keeps its wider radius — it's not a
        // specific target, just a navmesh point in a cell. Exfil radius
        // also stays — EFT's own extract zone gates the actual transition.
        var radius = category switch
        {
            WaypointCategory.ContainerLoot => 1f,
            WaypointCategory.LooseLoot => 1f,
            WaypointCategory.Corpse => 1f,
            WaypointCategory.Quest => 1f,
            WaypointCategory.Synthetic => Mathf.Clamp(cellSize / 2f, 10f, 15f),
            WaypointCategory.Exfil => Mathf.Clamp(cellSize / 2f, 10f, 15f),
            _ => 10f
        };

        var coverData = CollectBuiltinCoverData(position, radius);

        if (coverData.CoverPoints.Count < 16)
        {
            var extraPoints = 16 - coverData.CoverPoints.Count;
            CollectSyntheticCoverData(coverData.CoverPoints, position, radius, extraPoints);
        }

        if (coverData.CoverPoints.Count == 0)
        {
            Log.Debug($"Waypoint {category}:{name} has 0 cover points in proximity");
        }

        var radiusSqr = radius * radius;
        var waypoint = new Waypoint(_idCounter, category, name, position, radiusSqr, coverData.Doors, coverData.CoverPoints, target);
        _idCounter++;
        return waypoint;
    }

    private void ValidateAndAddWaypoint(List<Waypoint> collection, WaypointCategory category, string name, Vector3 position, float maxDistance = 2f, MonoBehaviour target = null)
    {
        if (NavMesh.SamplePosition(position, out var navTarget, maxDistance, NavMesh.AllAreas))
        {
            var objective = CreateBuiltinWaypoint(category, name, navTarget.position, target);
            collection.Add(objective);
        }
        else
        {
            Log.Debug($"Skipping Waypoint({category}, {name}, {position}), too far from navmesh");
        }
    }

    private static void Shuffle<T>(List<T> items)
    {
        // Fisher-Yates in-place.
        for (var i = 0; i < items.Count; i++)
        {
            var randomIndex = Random.Range(i, items.Count);
            (items[i], items[randomIndex]) = (items[randomIndex], items[i]);
        }
    }

    private static bool HasScavCoopRequirement(ExfiltrationPoint exfil)
    {
        var reqs = exfil.Requirements;
        if (reqs == null) return false;
        for (var i = 0; i < reqs.Length; i++)
        {
            if (reqs[i] != null && reqs[i].Requirement == ERequirementState.ScavCooperation)
                return true;
        }
        return false;
    }

    private CoverData CollectBuiltinCoverData(Vector3 position, float radius)
    {
        // BSG's CoversData voxel grid is actually 10x5x10 but we round it.
        const float voxelSize = 10f;
        var innerRadius = 0.75f * radius;
        var voxelSearchRange = Mathf.CeilToInt(2f * radius / voxelSize);
        var voxelIndex = botsController.CoversData.GetIndexes(position);

        var neighborVoxels = botsController.CoversData.GetVoxelesExtended(
            voxelIndex.x, voxelIndex.y, voxelIndex.z, voxelSearchRange, true
        );

        var doors = new HashSet<Door>();
        var coverPoints = new HashSet<CoverPoint>();

        for (var i = 0; i < neighborVoxels.Count; i++)
        {
            var voxel = neighborVoxels[i];

            for (var j = 0; j < voxel.DoorLinks.Count; j++)
            {
                var doorLink = voxel.DoorLinks[j];

                if ((doorLink.Door.transform.position - position).magnitude > radius)
                    continue;

                doors.Add(doorLink.Door);
            }

            for (var j = 0; j < voxel.Points.Count; j++)
            {
                var groupPoint = voxel.Points[j];

                if ((groupPoint.Position - position).magnitude > innerRadius)
                    continue;

                if (!IsReachable(position, groupPoint.Position, innerRadius))
                    continue;

                var coverPoint = new CoverPoint(groupPoint.Position, groupPoint.WallDirection, groupPoint.CoverType, groupPoint.CoverLevel);
                coverPoints.Add(coverPoint);
            }
        }

        return new CoverData(doors.ToList(), coverPoints.ToList());
    }

    private static void CollectSyntheticCoverData(List<CoverPoint> coverPoints, Vector3 waypointPosition, float radius, int count)
    {
        var innerRadius = 0.75f * radius;
        var goldenAngle = Mathf.PI * (3f - Mathf.Sqrt(5f));
        // 0.5 × r × √(π/N) ~= 0.886 × r / √N
        var navmeshSampleEps = 0.886f * innerRadius / Mathf.Sqrt(count);

        // Sunflower seed pattern — uniform-ish coverage of the disk.
        for (var i = 0; i < count; i++)
        {
            var theta = i * goldenAngle;
            var r = innerRadius * Mathf.Sqrt((float)i / count);

            var x = waypointPosition.x + r * Mathf.Cos(theta);
            var z = waypointPosition.z + r * Mathf.Sin(theta);

            var candidatePosition = new Vector3(x, waypointPosition.y, z);

            if (!NavMesh.SamplePosition(candidatePosition, out var target, navmeshSampleEps, NavMesh.AllAreas))
                continue;

            if (!IsReachable(waypointPosition, target.position, innerRadius))
                continue;

            var coverPoint = new CoverPoint(target.position, Vector3.zero, CoverCategory.None, CoverLevel.Lay);
            coverPoints.Add(coverPoint);
        }

        // Fall back to the waypoint itself if nothing else seeded.
        if (coverPoints.Count == 0)
        {
            coverPoints.Add(new CoverPoint(waypointPosition, Vector3.zero, CoverCategory.None, CoverLevel.Lay));
        }
    }

    private static bool IsReachable(Vector3 origin, Vector3 target, float lengthLimit)
    {
        var path = new NavMeshPath();
        if (!NavMesh.CalculatePath(origin, target, NavMesh.AllAreas, path))
            return false;

        return path.status == NavMeshPathStatus.PathComplete && PathHelper.TotalLength(path.corners) <= lengthLimit;
    }

    private readonly struct CoverData(List<Door> doors, List<CoverPoint> coverPoints)
    {
        public readonly List<Door> Doors = doors;
        public readonly List<CoverPoint> CoverPoints = coverPoints;
    }

    private readonly struct Exfil(ExfiltrationPoint point) : IEquatable<Exfil>
    {
        public readonly ExfiltrationPoint Point = point;

        // ExfiltrationPoint.Id is empty in SPT 4.0 (BSG API change), so the
        // original MongoID-based equality would collapse every exfil on the
        // map into a single deduped entry. Dedupe on the component reference
        // instead — each ExfiltrationPoint is a unique MonoBehaviour.
        public bool Equals(Exfil other) => ReferenceEquals(Point, other.Point);

        public override bool Equals(object obj) => obj is Exfil other && Equals(other);

        public override int GetHashCode() => Point != null ? Point.GetInstanceID() : 0;
    }
}
