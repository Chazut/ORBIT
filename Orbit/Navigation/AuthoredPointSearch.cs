using System;
using System.Collections.Generic;
using EFT;
using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Navigation;

// Runs only after ordinary destination probes fail. Each Step uses at most three local
// samples or one path query. Alternate storeys compete only if both can actually be reached.
internal sealed class AuthoredPointSearch
{
    private static readonly int[] Heights = { 0, 4, -4, 8, -8, 12, -12, 16, -16 };
    private const float SampleRadius = 2.35f;
    private const int Columns = 9;
    private readonly Vector3 _seed;
    private readonly float _radius;
    private readonly Func<Vector3, bool> _scope;
    private readonly List<Vector3> _candidates = new();
    private readonly NavMeshPath _path = new();
    private int _probe, _next, _terrainColumn;
    private bool _terrainFallback;
    private bool _sorted, _found;
    internal Vector3 Result { get; private set; }
    internal string Outcome { get; private set; }
    internal int Samples => _probe + _terrainColumn;
    internal int Paths { get; private set; }
    internal int RejectedScope { get; private set; }
    internal int BlockedSpace { get; private set; }
    internal int IncompletePath { get; private set; }
    internal int ShortPath { get; private set; }
    // A walkable spot on the requested floor can be disconnected from a distant origin.
    // It is evidence for trying an approach, never a corrected destination by itself.
    internal bool HasDisconnectedSurface => Outcome == "no-reachable-surface" && IncompletePath > 0;

    internal AuthoredPointSearch(Vector3 seed, float radius, Func<Vector3, bool> scope)
    { _seed = seed; _radius = Mathf.Min(radius, 6f); _scope = scope; }

    internal bool Reachable(Vector3 origin, Vector3 candidate)
    {
        if (_scope != null && !_scope(candidate)) { RejectedScope++; return false; }
        // Room to stand on the sampled surface, including low overhangs and mesh edges inside props.
        if (Physics.CheckCapsule(candidate + Vector3.up * .45f, candidate + Vector3.up * 1.4f,
            .25f, LayersMaskController.HighPolyWithTerrainMask, QueryTriggerInteraction.Ignore))
        { BlockedSpace++; return false; }
        Paths++;
        if (!NavMesh.CalculatePath(origin, candidate, NavMesh.AllAreas, _path)
            || _path.status != NavMeshPathStatus.PathComplete) { IncompletePath++; return false; }
        var corners = _path.corners;
        if (corners == null || corners.Length == 0 || (corners[0] - origin).sqrMagnitude > 2.25f
            || (corners[corners.Length - 1] - candidate).sqrMagnitude > .75f * .75f)
        { ShortPath++; return false; }
        return true;
    }

    internal bool Step(Vector3 origin)
    {
        if (Outcome != null) return true;
        if (_probe < Columns * Heights.Length)
        {
            for (int i = 0; i < 3 && _probe < Columns * Heights.Length; i++, _probe++)
            {
                int column = _probe / Heights.Length;
                var sample = Column(column) + Vector3.up * Heights[_probe % Heights.Length];
                if (!NavMesh.SamplePosition(sample, out var hit, SampleRadius, NavMesh.AllAreas)
                    || (hit.position - sample).sqrMagnitude > SampleRadius * SampleRadius) continue;
                if (Mathf.Abs(hit.position.y - _seed.y) <= 18f) AddCandidate(hit.position);
            }
            return false;
        }
        // Very bad outdoor heights can be tens of metres off. Accept only the first opaque
        // surface when it is actual terrain, then verify a nearby walkable surface at its height.
        if (!_sorted && _terrainColumn == 0) _terrainFallback = _candidates.Count == 0;
        if (_terrainFallback && _terrainColumn < Columns)
        {
            for (int i = 0; i < 2 && _terrainColumn < Columns; i++)
            {
                var column = Column(_terrainColumn++);
                if (!Physics.Raycast(column + Vector3.up * 64f, Vector3.down, out var ground, 128f,
                    LayersMaskController.HighPolyWithTerrainMask, QueryTriggerInteraction.Ignore)
                    || ground.collider is not TerrainCollider) continue;
                if (NavMesh.SamplePosition(ground.point, out var hit, 1.25f, NavMesh.AllAreas)
                    && (hit.position - ground.point).sqrMagnitude <= 1.25f * 1.25f)
                    AddCandidate(hit.position);
                break;
            }
            return false;
        }
        if (!_sorted)
        {
            _candidates.Sort((a, b) => Score(a).CompareTo(Score(b)));
            _sorted = true;
        }
        while (_next < _candidates.Count)
        {
            var candidate = _candidates[_next++];
            if (_found)
            {
                if (Score(candidate) > Score(Result) + 1.5f) break;
                if (Mathf.Abs(candidate.y - Result.y) <= 2.5f) continue;
            }
            if (Reachable(origin, candidate))
            {
                if (_found) { Outcome = "ambiguous-floors"; return true; }
                Result = candidate;
                _found = true;
            }
            return false;
        }
        Outcome = _found ? "corrected" : _candidates.Count == 0 ? "no-compatible-surface" : "no-reachable-surface";
        return true;
    }

    private Vector3 Column(int column)
    {
        if (column == 0) return _seed;
        float angle = (column - 1) * Mathf.PI / 4f;
        float offset = _radius * .72f;
        return _seed + new Vector3(Mathf.Cos(angle) * offset, 0f, Mathf.Sin(angle) * offset);
    }

    private void AddCandidate(Vector3 position)
    {
        var delta = position - _seed;
        if (delta.x * delta.x + delta.z * delta.z > _radius * _radius || _scope != null && !_scope(position))
        { RejectedScope++; return; }
        foreach (var point in _candidates)
            if ((point - position).sqrMagnitude < .0625f) return;
        _candidates.Add(position);
    }

    private float Score(Vector3 point) => Vector3.Distance(_seed, point);
}
