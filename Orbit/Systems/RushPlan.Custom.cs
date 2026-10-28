using System.Collections.Generic;
using Orbit.Entities;
using Orbit.Navigation;
using Orbit.Settings;
using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Systems;

internal sealed partial class RushPlan
{
    private bool _customArrived, _customCampDone;
    private float _customCampHeld, _customLootHeld;
    private readonly HashSet<Agent> _customSlots = new();
    private readonly Dictionary<Agent, CoverPoint> _customCampPositions = new();
    private readonly List<Vector3> _customCampOccupied = new();
    private IEnumerator<CoverPoint?> _customCampSearch;
    private Agent _customCampMember;

    private bool TickCustom(Squad squad, WaypointSystem w, RushPoint site, float delta)
    {
        var camping = !_customCampDone && site.CustomMode is "Camp" or "CampThenLoot";
        if (camping)
        {
            if (!PrepareCustomCamp(squad, w, site, delta)) return !Main.Completed;
            if (_customCampHeld < site.CampSeconds) return true;
            _customCampDone = true;
            if (site.CustomMode == "Camp") return Visit(squad, w);
            delta = 0;
        }
        if (Status != "looting area") SetStatus(squad, "looting area");
        _customLootHeld += delta;
        HoldCustomMembers(squad, w, site);
        if (_loot != null)
        {
            // A time limit cannot interrupt an inventory transfer already in progress.
            if (CorpseEscort.InFlight(_actor)) return true;
            if (_customLootHeld < site.LootSeconds && _actor.Objective.Location == _loot
                && _actor.Objective.Status is not (ObjectiveStatus.Finished or ObjectiveStatus.Failed)) return true;
            _attempted.Add(_loot.Id); w.ReleaseClaim(_loot.Id, _actor.Id); _loot = null;
        }
        if (_customLootHeld >= site.LootSeconds) return Visit(squad, w);
        if (Time.time < _nextWork || !w.TryOperationWork(this)) return true;
        _nextWork = Time.time + .1f;
        _loot = w.OperationLoot(_actor, Position(site), _attempted, out var exhausted, site.LootRadius);
        if (_loot != null) SetOrder(_actor, _loot, w);
        else if (exhausted)
        {
            _roomLooters.Add(_actor);
            foreach (var member in squad.Members)
                if (member.IsActive && !member.Bot.IsDead && !member.SoloExtractRequested
                    && !CorpseEscort.InFlight(member) && !_roomLooters.Contains(member))
                {
                    _customSlots.Remove(_actor); _customSlots.Remove(member);
                    _actor = member; _attempted.Clear(); return true;
                }
            return Visit(squad, w);
        }
        return true;
    }

    private bool PrepareCustomCamp(Squad squad, WaypointSystem w, RushPoint site, float delta)
    {
        var arrived = true;
        for (var i = -1; i < squad.Members.Count; i++)
        {
            var member = i < 0 ? _actor : squad.Members[i];
            if (i >= 0 && member == _actor || !member.IsActive || member.Bot.IsDead || member.SoloExtractRequested
                || CorpseEscort.InFlight(member)) continue;
            if (!_customCampPositions.TryGetValue(member, out var cover))
            {
                _elapsed += delta;
                if (Status != "finding camp cover") SetStatus(squad, "finding camp cover");
                if (!w.TryOperationWork(this)) return false;
                if (_customCampMember != member)
                {
                    _customCampSearch?.Dispose(); _customCampMember = member;
                    _customCampOccupied.Clear();
                    foreach (var pair in _customCampPositions)
                        if (squad.Members.Contains(pair.Key) && !pair.Key.Bot.IsDead && !pair.Key.SoloExtractRequested)
                            _customCampOccupied.Add(pair.Value.Position);
                    _customCampSearch = w.SearchCustomCamp(member, site, _customCampOccupied).GetEnumerator();
                }
                if (!_customCampSearch.MoveNext())
                { Skip(squad, w, "no reachable camp position"); return false; }
                if (_customCampSearch.Current.HasValue)
                {
                    cover = _customCampSearch.Current.Value;
                    _customCampSearch.Dispose(); _customCampSearch = null; _customCampMember = null;
                    _customCampPositions.Add(member, cover); _customSlots.Add(member);
                    SetOrder(member, Point(w, cover.Position, "Camp " + site.Name), w);
                    member.Guard.CoverPoint = cover;
                    if (member == _actor) squad.Objective.Location = _orders[member];
                }
                w.ContinueOperationWork(this);
                return false;
            }
            if (member.Objective.Status == ObjectiveStatus.Failed)
            {
                if (++_retries >= 3) { Skip(squad, w, "camp position unreachable"); return false; }
                Release(squad, w); _route.Reset(); _customArrived = false;
                SetStatus(squad, "returning to camp"); return false;
            }
            if (!_orders.TryGetValue(member, out var order) || member.Objective.Location != order
                || member.Objective.Status == ObjectiveStatus.Finished && (member.Position - cover.Position).sqrMagnitude > 4f)
            {
                SetOrder(member, Point(w, cover.Position, "Camp " + site.Name), w);
                member.Guard.CoverPoint = cover;
                if (member == _actor) squad.Objective.Location = _orders[member];
            }
            arrived &= member.Objective.Status == ObjectiveStatus.Finished && (member.Position - cover.Position).sqrMagnitude <= 4f;
        }
        if (!arrived)
        {
            _elapsed += delta;
            if (Status != "approaching camp cover") SetStatus(squad, "approaching camp cover");
            return false;
        }
        if (Status == "camping") _customCampHeld += delta;
        else SetStatus(squad, "camping");
        return true;
    }

    private void ClearCustomCamp()
    {
        _customCampSearch?.Dispose(); _customCampSearch = null; _customCampMember = null;
        _customCampPositions.Clear(); _customCampOccupied.Clear();
    }

    private void HoldCustomMembers(Squad squad, WaypointSystem w, RushPoint site)
    {
        // One follower placement per shared work slot, retained until combat or a detour.
        for (var i = 0; i < squad.Members.Count; i++)
        {
            var member = squad.Members[i];
            if (member == _actor || !member.IsActive || member.Bot.IsDead || member.SoloExtractRequested
                || CorpseEscort.InFlight(member) || _customSlots.Contains(member)) continue;
            if (!w.TryOperationWork(this)) return;
            var angle = i * 2.399963f;
            var offset = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * Mathf.Min(site.Radius, 3f + i);
            if (NavMesh.SamplePosition(_anchor + offset, out var hit, 1f, NavMesh.AllAreas)
                && Mathf.Abs(hit.position.y - _anchor.y) <= .65f
                && (hit.position - _anchor).sqrMagnitude <= site.Radius * site.Radius
                && NavMesh.CalculatePath(member.Position, hit.position, NavMesh.AllAreas, _path)
                && _path.status == NavMeshPathStatus.PathComplete)
            {
                SetOrder(member, Point(w, hit.position, "Hold " + site.Name), w);
                member.Guard.CoverPoint = new CoverPoint(hit.position, offset.normalized, CoverCategory.None, CoverLevel.Stay);
            }
            _customSlots.Add(member);
            return;
        }
    }
}
