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

    private bool TickCustom(Squad squad, WaypointSystem w, RushPoint site, float delta)
    {
        var camping = !_customCampDone && site.CustomMode is "Camp" or "CampThenLoot";
        if (camping)
        {
            if ((_actor.Position - _anchor).sqrMagnitude > 9f || _actor.Objective.Status == ObjectiveStatus.Failed)
            {
                if (_actor.Objective.Status == ObjectiveStatus.Failed && ++_retries >= 3)
                    return Skip(squad, w, "camp position unreachable");
                // Resume the ordinary staged approach, retaining time already spent camping.
                Release(squad, w); _route.Reset(); _customArrived = false;
                SetStatus(squad, "returning to camp"); return true;
            }
            _actor.Guard.CoverPoint = new CoverPoint(_anchor, new Vector3(0, 0, 1), CoverCategory.None, CoverLevel.Stay);
            if (Status != "camping") SetStatus(squad, "camping");
            if (_actor.Objective.Status == ObjectiveStatus.Finished) _customCampHeld += delta;
            if (_customCampHeld < site.CampSeconds) { HoldCustomMembers(squad, w, site); return true; }
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
