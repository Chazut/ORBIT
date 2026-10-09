using System;
using System.Runtime.CompilerServices;
using EFT.Interactive;
using EFT.InventoryLogic;
using Orbit.Entities;
using Orbit.Helpers;
using UnityEngine;

namespace Orbit.Navigation;

internal static class NoBackpackExfil
{
    internal enum Preparation { Waiting, Ready, Rejected }
    private sealed class DropState
    {
        internal ExfiltrationPoint Exit;
        internal Item Bag;
        internal int Attempts;
        internal bool Pending, Confirmed;
        internal float StartedAt, RecoverySince = -1f, LastTick = -1f;
        internal Vector3 LastPosition;
    }
    private static readonly ConditionalWeakTable<Agent, DropState> Drops = new();

    internal static bool RequiresDrop(ExfiltrationPoint exit)
    {
        if (exit?.Requirements == null) return false;
        foreach (var requirement in exit.Requirements)
            if (IsBackpackRequirement(requirement)) return true;
        return false;
    }

    internal static bool IsBackpackRequirement(ExfiltrationRequirement requirement)
        => requirement != null && requirement.RequiredSlot == EquipmentSlot.Backpack
            && requirement.Requirement is ERequirementState.Empty or ERequirementState.EmptyOrSize;

    internal static bool CanPlan(Agent agent)
    {
        if (!ServerConfig.Loot.AllowNoBackpackExfils || agent?.Bot?.GetPlayer?.InventoryController == null) return false;
        var bag = Backpack(agent);
        return bag == null || agent.SoloExtractIsEmergency || IsEmpty(bag);
    }

    internal static bool CanPlan(Squad squad, Agent solo = null)
    {
        if (solo != null) return CanPlan(solo);
        if (squad == null || !ServerConfig.Loot.AllowNoBackpackExfils) return false;
        foreach (var member in squad.Members)
            if (!member.SoloExtractRequested && !CanPlan(member)) return false;
        return true;
    }

    private static Item Backpack(Agent agent)
        => agent.Bot?.GetPlayer?.InventoryController?.Inventory?.Equipment?.GetSlot(EquipmentSlot.Backpack)?.ContainedItem;

    internal static bool IsEmpty(Item bag)
    {
        if (bag is not CompoundItem compound || compound.Grids == null) return false;
        foreach (var grid in compound.Grids)
            if (grid?.Items != null)
                foreach (var item in grid.Items) if (item != null) return false;
        return true;
    }

    internal static Preparation Prepare(Agent agent, Waypoint location)
    {
        if (location?.Target is not ExfiltrationPoint exit || !RequiresDrop(exit)) return Preparation.Ready;
        if (!CanPlan(agent) || ExfilArrival.IsUnavailable(exit)) return Preparation.Rejected;
        var bag = Backpack(agent);
        Drops.TryGetValue(agent, out var state);
        if (state != null && (state.Exit != exit || (bag != null && (state.Confirmed || !ReferenceEquals(state.Bag, bag)))))
        {
            Drops.Remove(agent);
            state = null;
        }
        // A native throw can remove the bag without delivering its callback. Inventory is authoritative.
        if (bag == null)
        {
            if (state != null && !state.Confirmed)
            {
                Log.Info($"{agent} NO-BACKPACK: drop confirmed from inventory at {exit.name}, callbackPending={state.Pending}");
                state.Confirmed = true;
                state.Pending = false;
            }
            return Preparation.Ready;
        }
        if (state?.Pending == true)
        {
            if (Time.time - state.StartedAt < 10f) return Preparation.Waiting;
            Log.Warning($"{agent} NO-BACKPACK: drop timed out at {exit.name}, bag still equipped");
            return Preparation.Rejected;
        }
        if (!agent.IsActive) return Preparation.Waiting;
        // Drop at the entrance, using the native inventory operation so the bag and its contents remain lootable.
        var trigger = exit.GetComponent<Collider>();
        var entrance = trigger != null ? trigger.ClosestPoint(agent.Position) : location.Position;
        if ((agent.Position - entrance).sqrMagnitude > 16f) return Preparation.Waiting;
        if (state == null)
        {
            state = new DropState { Exit = exit, Bag = bag };
            Drops.Add(agent, state);
        }
        if (state.Attempts >= 3) return Preparation.Rejected;
        if (state.Attempts > 0 && Time.time - state.StartedAt < 3f) return Preparation.Waiting;
        state.Attempts++;
        state.StartedAt = Time.time;
        state.Pending = true;
        var attempt = state.Attempts;
        try
        {
            Log.Info($"{agent} NO-BACKPACK: dropping bag at {exit.name}, emergency={agent.SoloExtractIsEmergency}");
            agent.Bot.GetPlayer.InventoryController.ThrowItem(bag, downDirection: false, callback: _ =>
            {
                if (!Drops.TryGetValue(agent, out var current) || !ReferenceEquals(current, state)
                    || current.Attempts != attempt) return;
                state.Pending = false;
                Log.Info($"{agent} NO-BACKPACK: drop callback, slotEmpty={Backpack(agent) == null}");
            });
        }
        catch (Exception error)
        {
            state.Pending = false;
            Log.Warning($"{agent} NO-BACKPACK: drop failed: {error.Message}");
        }
        return Preparation.Waiting; // Confirm the real slot state on the next update, never despawn on request.
    }

    internal static bool CanFinish(Agent agent, Waypoint location)
        => location?.Target is not ExfiltrationPoint exit || !RequiresDrop(exit)
            || (CanPlan(agent) && !ExfilArrival.IsUnavailable(exit)
                && exit.GetComponent<Collider>() != null
                && ExfilArrival.IsInside(agent, location) && Backpack(agent) == null
                && RequirementsMet(agent, exit));

    internal static void PauseRecovery(Agent agent)
    {
        if (Drops.TryGetValue(agent, out var state)) state.RecoverySince = -1f;
    }

    internal static bool RetainAfterDrop(Agent agent, Waypoint location)
        => Drops.TryGetValue(agent, out var state) && RecoveryCandidate(agent, location, state);

    // Rescue only a confirmed drop at this very exit. A missing bag on spawn is not proof of arrival.
    internal static bool RecoveryDue(Agent agent, Waypoint location)
    {
        if (!Drops.TryGetValue(agent, out var state)) return false;
        if (!RecoveryCandidate(agent, location, state))
        { state.RecoverySince = -1f; return false; }
        var now = Time.time;
        if (state.RecoverySince < 0f || now - state.LastTick > Mathf.Max(1f, Time.deltaTime * 2f)
            || (agent.Position - state.LastPosition).sqrMagnitude > 1f)
        {
            state.RecoverySince = now;
            state.LastPosition = agent.Position;
        }
        state.LastTick = now;
        var wait = Mathf.Max(15f, state.Exit.Settings?.ExfiltrationTime ?? 0f);
        return now - state.RecoverySince >= wait && CanRecover(agent, location);
    }

    internal static bool CanRecover(Agent agent, Waypoint location)
    {
        if (!Drops.TryGetValue(agent, out var state) || !RecoveryCandidate(agent, location, state)) return false;
        if (ExfilArrival.IsInside(agent, location)) return true;
        var entrance = state.Exit.GetComponent<Collider>().ClosestPoint(agent.Position);
        var delta = entrance - agent.Position;
        // Only the final clear stretch at the trigger edge, never through a wall or from another floor.
        return delta.sqrMagnitude < 0.001f || !Physics.Raycast(agent.Position + Vector3.up * 0.5f,
            delta.normalized, delta.magnitude, LayersMaskController.HighPolyWithTerrainMask | (1 << LayersMaskController.DoorLayer));
    }

    private static bool RecoveryCandidate(Agent agent, Waypoint location, DropState state)
    {
        if (!agent.IsActive || location?.Target != state.Exit || !state.Confirmed || !CanPlan(agent)
            || Backpack(agent) != null || ExfilArrival.IsUnavailable(state.Exit)
            || ExfilArrival.IsSharedTimer(state.Exit)) return false;
        var trigger = state.Exit.GetComponent<Collider>();
        if (trigger == null || !trigger.enabled) return false;
        var delta = trigger.ClosestPoint(agent.Position) - agent.Position;
        if (delta.sqrMagnitude > 16f || Mathf.Abs(delta.y) > 1f) return false;
        // Only bypass stale backpack bookkeeping. Other requirements remain real requirements.
        foreach (var requirement in state.Exit.Requirements)
            if (requirement != null && !IsBackpackRequirement(requirement)
                && !requirement.Met(agent.Bot.GetPlayer, state.Exit)) return false;
        return true;
    }

    private static bool RequirementsMet(Agent agent, ExfiltrationPoint exit)
    {
        foreach (var requirement in exit.Requirements)
            if (requirement != null && !requirement.Met(agent.Bot.GetPlayer, exit)) return false;
        return true;
    }
}
