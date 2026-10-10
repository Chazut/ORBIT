using System;
using System.Collections.Generic;
using EFT;
using EFT.Interactive;
using Orbit.Navigation;
using UnityEngine;

namespace Orbit.Systems;

internal enum OperationStepKind { Switch, Access, Wait, Loot, Regroup, Extract }

internal sealed class OperationStep
{
    internal string Label;
    internal OperationStepKind Kind;
    internal WorldInteractiveObject Object;
    internal WorldInteractiveObject WaitAt;
    internal Waypoint Exit;
    internal bool OptionalAlarm;
    internal bool Underground;
    internal bool RetryNavigation;
    internal bool BestEffort;
    internal Vector3 Entry;
    internal Vector3? SwitchApproach;
    private Func<Vector3, bool> _interactionCheck;
    private OperationSwitchReach.Result _lastReach;
    private OperationSwitchReach.Result _candidateRejection;
    private Vector3 _rejectedFeet;
    internal Func<Vector3, bool> InteractionCheck => _interactionCheck ??= CanStand;
    internal string ReachDiagnostics => _lastReach.Diagnostics;
    internal string CandidateDiagnostics => $"feet={_rejectedFeet} {_candidateRejection.Diagnostics}";
    private bool CanStand(Vector3 feet)
    {
        var reach = OperationSwitchReach.Evaluate(feet, Object);
        if (!reach.Reachable) { _candidateRejection = reach; _rejectedFeet = feet; }
        return reach.Reachable;
    }
    internal bool CanInteract(Vector3 feet)
    {
        _lastReach = OperationSwitchReach.Evaluate(feet, Object);
        return _lastReach.Reachable;
    }
    internal Vector3 Position => WaitAt?.transform.position ?? Exit?.ExfilInteriorPosition ?? Exit?.Position
        ?? (Kind == OperationStepKind.Access && Object is KeycardDoor card && card.DoorState == EDoorState.Locked
            && card.Proxies?.Length > 0 ? card.Proxies[0].transform.position : Object.transform.position);
}

internal sealed class OperationDefinition
{
    internal string Id, Name, MapKey;
    internal bool Extraction;
    internal bool RequiresCredential;
    internal bool AllowsCategory(string category) => category != "PlayerScav" || !RequiresCredential;
    internal Switch Power;
    internal readonly List<OperationStep> Steps = new();
}

/// <summary>Operations bound to scene IDs, with optional ground approaches for confined switches.</summary>
internal sealed class MultiStepCatalog
{
    internal readonly List<OperationDefinition> Operations = new();
    private readonly Dictionary<string, WorldInteractiveObject> _objects = new(StringComparer.Ordinal);
    private readonly List<Waypoint> _exits;
    private readonly string _mapKey;
    internal const string Mall = "Shopping_Mall_DesignStuff_";

    internal MultiStepCatalog(string map, List<Waypoint> exits, string mapKey = null)
    {
        _exits = exits;
        _mapKey = mapKey ?? map;
        MultiStepAccess.Reset();
        map = map.ToLowerInvariant();
        if (map != "interchange" && map != "rezervbase" && map != "bigmap") return;
        foreach (var obj in UnityEngine.Object.FindObjectsOfType<WorldInteractiveObject>(true))
            if (!string.IsNullOrEmpty(obj.Id)) _objects[obj.Id] = obj;
        if (map == "interchange")
        {
            Add("kiba", "KIBA", false, Mall + "00055",
                Step("Mall power", Mall + "00055"), Step("Disable mall alarm", Mall + "00058", alarm: true),
                Step("Unlock outer door", Mall + "00050"), Step("Unlock inner door", Mall + "00049"), Loot(Mall + "00049"));
            Add("ultra", "ULTRA Medical", false, Mall + "00055",
                Step("Mall power", Mall + "00055"), Step("Unlock medical room", Mall + "00052"), Loot(Mall + "00052"));
            Add("object21ws", "Object 21WS", false, Mall + "00055",
                Step("Mall power", Mall + "00055"), Step("21WS card reader", Mall + "00063"), Loot(Mall + "00063"));
            Add("saferoom-loot", "Saferoom loot", false, Mall + "00055",
                Step("Mall power", Mall + "00055"), Step("Disable mall alarm", Mall + "00058", alarm: true),
                Step("Move urinal", Mall + "00064"),
                Step("11SR card reader", Mall + "00051"), Loot(Mall + "00051"));
            Add("object14", "Object 14", false, Mall + "00055",
                Step("Mall power", Mall + "00055"), Step("Disable mall alarm", Mall + "00058", alarm: true),
                Step("Move urinal", Mall + "00064"),
                Step("11SR card reader", Mall + "00051"), Step("Object 14 lever", Mall + "00061"),
                Wait("Wait for Object 14 door", Mall + "00048", Mall + "00048"), Loot(Mall + "00048"));
            Add("saferoom-extract", "Saferoom extraction", true, Mall + "00055",
                Step("Mall power", Mall + "00055"), Step("Disable mall alarm", Mall + "00058", alarm: true),
                Step("Move urinal", Mall + "00064"),
                Step("11SR card reader", Mall + "00051"), Loot(Mall + "00051"),
                ExitStep("Regroup inside Saferoom", "Saferoom Exfil", OperationStepKind.Regroup),
                Step("Close Saferoom", Mall + "00060", bestEffort: true) ?? new OperationStep
                {
                    Label = "Close Saferoom", Kind = OperationStepKind.Switch, BestEffort = true,
                    WaitAt = _objects.TryGetValue(Mall + "00051", out var saferoomDoor) ? saferoomDoor : null
                },
                ExitStep("Extract", "Saferoom Exfil", OperationStepKind.Extract));
        }
        else if (map == "rezervbase")
        {
            Add("d2", "D-2", true, "autoId_00000_D2_LEVER",
                Step("Bunker power", "autoId_00000_D2_LEVER"),
                Step("D-2 gate button", "00453", approach: new Vector3(-116.3f, -18.4f, 169.3f)),
                Wait("Wait for D-2 gate", "00454", "00453"),
                ExitStep("Wait for gate and extract", "EXFIL_Bunker_D2", OperationStepKind.Extract));
            Add("hermetic", "Bunker Hermetic Door", true, "autoId_00632_EXFIL",
                Step("Hermetic lever", "autoId_00632_EXFIL", approach: new Vector3(-60.083f, -7.042f, 76.826f)),
                ExitStep("Reach active exit", "EXFIL_Bunker", OperationStepKind.Extract));
        }
        else
        {
            Add("zb013", "ZB-013", true, "custom_DesignStuff_00034",
                Step("Warehouse power", "custom_DesignStuff_00034", approach: new Vector3(352.395f, 1.231f, -39.127f)),
                Step("Unlock Factory door", "door_Custom_Construction_Factory_00000"),
                ExitStep("Extract", "EXFIL_ZB013", OperationStepKind.Extract));
        }
        Log.Info($"MULTISTEP: catalog map={map} operations={Operations.Count}");
    }

    private OperationStep Step(string label, string id, bool alarm = false, Vector3? approach = null, bool bestEffort = false)
        => _objects.TryGetValue(id, out var obj) ? new OperationStep
        {
            Label = label, Object = obj, Kind = obj is Switch ? OperationStepKind.Switch : OperationStepKind.Access,
            OptionalAlarm = alarm,
            BestEffort = bestEffort,
            RetryNavigation = id == Mall + "00064" || id == Mall + "00058" || id == Mall + "00051",
            // A map variant may relocate this object. Keep the generic search in that case.
            SwitchApproach = obj is Switch && (RecordedRouteLibrary.Interaction(_mapKey, id) ?? approach) is { } point
                && (point - obj.transform.position).sqrMagnitude <= 16f
                && Mathf.Abs(point.y - obj.transform.position.y) <= 2f ? point : null
        } : null;

    private OperationStep Loot(string id) => _objects.TryGetValue(id, out var obj)
        ? new OperationStep { Label = "Loot area", Object = obj, Kind = OperationStepKind.Loot } : null;

    private OperationStep Wait(string label, string id, string near)
        => _objects.TryGetValue(id, out var obj) && _objects.TryGetValue(near, out var anchor)
            ? new OperationStep { Label = label, Object = obj, WaitAt = anchor, Kind = OperationStepKind.Wait } : null;

    private OperationStep ExitStep(string label, string name, OperationStepKind kind)
    {
        foreach (var point in _exits)
            if (point.Target is ExfiltrationPoint exit && (exit.name == name || exit.Settings?.Name == name))
                return new OperationStep { Label = label, Exit = point, Kind = kind };
        return null;
    }

    private void Add(string id, string name, bool extraction, string power, params OperationStep[] steps)
    {
        if (!_objects.TryGetValue(power, out var source) || source is not Switch sw) return;
        foreach (var step in steps)
            if (step == null) { Log.Warning($"MULTISTEP: unavailable operation={id}: required scene object missing"); return; }
        var definition = new OperationDefinition { Id = id, Name = name, MapKey = _mapKey, Extraction = extraction, Power = sw,
            RequiresCredential = id is "kiba" or "ultra" or "object21ws" or "saferoom-loot"
                or "object14" or "saferoom-extract" or "zb013" };
        definition.Steps.AddRange(steps);
        if (id == "d2")
            for (var i = 1; i < steps.Length; i++)
            { steps[i].Underground = true; steps[i].Entry = sw.transform.position; }
        Operations.Add(definition);
        foreach (var step in steps)
        {
            if (step.Object is Door door) MultiStepAccess.Protect(door, sw);
            if (step.Exit?.Target is ExfiltrationPoint exit) MultiStepAccess.Protect(exit);
        }
    }
}

internal static class MultiStepAccess
{
    private static readonly Dictionary<Door, Switch> Protected = new();
    private static readonly HashSet<ExfiltrationPoint> Exits = new();
    internal static void Reset() { Protected.Clear(); Exits.Clear(); }
    internal static void Protect(Door door, Switch power) => Protected[door] = power;
    internal static void Protect(ExfiltrationPoint exit) => Exits.Add(exit);
    internal static bool IsConditional(ExfiltrationPoint exit) => Exits.Contains(exit);
    internal static bool ExitActive(ExfiltrationPoint exit, Orbit.Entities.Agent agent = null)
        => exit != null && (exit.Status is EExfiltrationStatus.RegularMode or EExfiltrationStatus.Countdown
            || agent?.Squad?.Operation?.CanExtractOpenSaferoom(agent, exit) == true);
    // Generic key bypass must never skip a powered room's actual access sequence.
    internal static bool CanForceUnlock(Door door) => !Protected.ContainsKey(door);
    internal static bool HasPower(Door door) => !Protected.TryGetValue(door, out var power) || power != null && power.DoorState == EDoorState.Open;
}
