using System.Collections.Generic;
using EFT;
using EFT.Interactive;
using Orbit.Entities;
using Orbit.Helpers;
using Orbit.Navigation;
using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Systems;

public partial class WaypointSystem
{
    private MultiStepCatalog _operations;
    private readonly NavMeshPath _operationPath = new();
    private float _nextOperationPlan;
    private int _operationWorkFrame = -1;

    internal bool TryOperationWork()
    {
        if (_operationWorkFrame == Time.frameCount) return false;
        _operationWorkFrame = Time.frameCount;
        return true;
    }

    private void InitializeOperations()
    {
        var exits = new List<Waypoint>();
        foreach (var cell in _cells)
            foreach (var point in cell.Waypoints)
                if (point.Category == WaypointCategory.Exfil) exits.Add(point);
        _operations = new MultiStepCatalog(_mapId, exits);
    }

    internal MainObjective RollOperationMain(Squad squad)
    {
        var definition = PickOperation(squad, false);
        if (definition == null) return null;
        var plan = new OperationPlan(definition, UnityEngine.Random.value < OperationStyle(squad).DisableAlarmChance);
        var main = new MainObjective { Type = MainObjectiveType.MultiStep, Operation = plan,
            Position = plan.Current.Position, CellCoords = WorldToCell(plan.Current.Position) };
        plan.Main = main;
        return main;
    }

    internal static Orbit.Settings.MultiStepStyle OperationStyle(Squad squad)
        => ServerConfig.MultiStep.Style(squad.Personality?.Archetype.ToString(), squad.Leader?.BotCategory == "PlayerScav");

    private OperationDefinition PickOperation(Squad squad, bool extraction)
    {
        if (_operations == null || !ServerConfig.MultiStep.Allows(squad.Leader?.BotCategory)) return null;
        var sum = 0f;
        OperationDefinition chosen = null;
        foreach (var definition in _operations.Operations)
        {
            if (definition.Extraction != extraction || !definition.AllowsCategory(squad.Leader?.BotCategory)) continue;
            if (extraction && squad.FailedOperationExits.Contains(definition.Id)) continue;
            var used = false;
            if (!extraction && squad.MainObjectives != null)
                foreach (var main in squad.MainObjectives)
                    if (main.Operation?.Definition == definition) { used = true; break; }
            if (used) continue;
            if (extraction && !OperationExitEligible(squad, definition.Steps[definition.Steps.Count - 1].Exit)) continue;
            var weight = ServerConfig.MultiStep.Weight(definition.Id);
            if (weight <= 0) continue;
            sum += weight;
            if (UnityEngine.Random.value * sum < weight) chosen = definition;
        }
        return chosen;
    }

    private bool OperationExitEligible(Squad squad, Waypoint target)
    {
        if (target?.Target is not ExfiltrationPoint exit || exit.Settings == null
            || exit.Status is EExfiltrationStatus.NotPresent or EExfiltrationStatus.Hidden) return false;
        var bot = squad.Leader.Bot;
        if (bot.Profile.WillBeAPlayerScav() || !bot.Profile.Info.Settings.Role.IsPMC()) return false;
        return ServerConfig.Loot.ExtractAllowedFor.IsBotEnabled(bot.Profile.Info.Settings.Role)
               && MatchesBotSpawnEntry(squad, exit);
    }

    internal bool TickOperation(Squad squad, bool combat)
    {
        // Shared world prerequisites can change while another operation owns
        // the squad. Keep pending targets current for selection and telemetry.
        if (squad.MainObjectives != null)
            foreach (var main in squad.MainObjectives)
                main.Operation?.RefreshPending(this);
        if (squad.Operation != null)
        {
            if (squad.Operation.Tick(squad, this, combat)) return true;
            if (squad.Operation.Active) return false;
            squad.Operation = null;
        }
        if (combat || squad.SainResolutionPending || squad.Camp.Active || squad.CorpseEscort.Active
            || Time.time < _nextOperationPlan || !ServerConfig.MultiStep.Allows(squad.Leader?.BotCategory)) return false;
        if (squad.ExtractRequested)
        {
            if (!squad.OperationExtractRolled)
            {
                squad.OperationExtractRolled = true;
                squad.OperationExtractCommitted = UnityEngine.Random.value < OperationStyle(squad).ExtractionChance;
            }
            if (!squad.OperationExtractCommitted) return false;
            var definition = PickOperation(squad, true);
            if (definition != null)
            {
                squad.Operation = new OperationPlan(definition, false);
                var marker = new MainObjective { Type = MainObjectiveType.MultiStep, Operation = squad.Operation,
                    Position = squad.Operation.Current.Position, CellCoords = WorldToCell(squad.Operation.Current.Position) };
                squad.Operation.Main = marker;
                squad.MainObjectives ??= new List<MainObjective>();
                squad.MainObjectives.Add(marker);
            }
            else squad.OperationExtractCommitted = false;
        }
        else if (squad.MainObjectives != null)
        {
            MainObjective nearest = null;
            var distance = float.MaxValue;
            foreach (var main in squad.MainObjectives)
            {
                if (main.Completed) continue;
                var d = (main.Position - squad.Leader.Position).sqrMagnitude;
                if (d < distance) { nearest = main; distance = d; }
            }
            if (nearest?.Operation != null) squad.Operation = nearest.Operation;
        }
        if (squad.Operation == null) return false;
        _nextOperationPlan = Time.time + .5f;
        squad.Operation.Begin(squad);
        return squad.Operation.Tick(squad, this, combat);
    }

    internal bool TryOperationPoint(Agent actor, OperationStep step, OperationRouteSearch search,
        out Vector3 point, out bool final)
    {
        var target = step.Position;
        if (step.Kind == OperationStepKind.Access && step.Object is KeycardDoor card
            && card.DoorState == EDoorState.Locked && card.Proxies?.Length > 0)
            target = card.Proxies[0].transform.position; // 11SR reader is upstairs, far from its door.
        else if (step.Kind is OperationStepKind.Access or OperationStepKind.Switch)
            target = step.Object.GetInteractionParameters(actor.Position).InteractionPosition;
        return search.Find(actor.Position, target, out point, out final);
    }

    internal Waypoint OperationLoot(Agent agent, Vector3 center, HashSet<int> attempted, out bool exhausted)
    {
        exhausted = true;
        var coords = WorldToCell(center);
        var paths = 0;
        for (var x = Mathf.Max(0, coords.x - 1); x <= Mathf.Min(_gridSize.x - 1, coords.x + 1); x++)
        for (var y = Mathf.Max(0, coords.y - 1); y <= Mathf.Min(_gridSize.y - 1, coords.y + 1); y++)
            foreach (var point in _cells[x, y].Waypoints)
            {
                if (point.Category is not (WaypointCategory.ContainerLoot or WaypointCategory.LooseLoot)
                    || (point.Position - center).sqrMagnitude > 225f || Mathf.Abs(point.Position.y - center.y) > 2.5f
                    || attempted.Contains(point.Id) || agent.Squad.CompletedPoiIds.Contains(point.Id)
                    || IsClaimedByOther(point.Id, agent.Id)
                    || !Orbit.Tasks.Actions.GotoObjectiveAction.IsLootableForAgent(agent, point)) continue;
                if (++paths > 3) { exhausted = false; return null; }
                if (NavMesh.CalculatePath(agent.Position, point.Position, NavMesh.AllAreas, _operationPath)
                    && _operationPath.status == NavMeshPathStatus.PathComplete && TryClaim(point.Id, agent.Id)) return point;
                attempted.Add(point.Id);
            }
        return null;
    }
}
