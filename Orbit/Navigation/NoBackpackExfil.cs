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
    private sealed class DropState { internal int Attempts; internal bool Pending; internal float StartedAt; }
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
        if (Drops.TryGetValue(agent, out var pending) && pending.Pending)
            return Time.time - pending.StartedAt < 10f ? Preparation.Waiting : Preparation.Rejected;
        var bag = Backpack(agent);
        if (bag == null) return Preparation.Ready;
        if (!agent.IsActive) return Preparation.Waiting;
        // Drop at the entrance, using the native inventory operation so the bag and its contents remain lootable.
        var trigger = exit.GetComponent<Collider>();
        var entrance = trigger != null ? trigger.ClosestPoint(agent.Position) : location.Position;
        if ((agent.Position - entrance).sqrMagnitude > 16f) return Preparation.Waiting;
        var state = Drops.GetValue(agent, static _ => new DropState());
        if (state.Attempts >= 3) return Preparation.Rejected;
        if (state.Attempts > 0 && Time.time - state.StartedAt < 3f) return Preparation.Waiting;
        state.Attempts++;
        state.StartedAt = Time.time;
        state.Pending = true;
        try
        {
            Log.Info($"{agent} NO-BACKPACK: dropping bag at {exit.name}, emergency={agent.SoloExtractIsEmergency}");
            agent.Bot.GetPlayer.InventoryController.ThrowItem(bag, downDirection: false, callback: _ =>
            {
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
                && (!Drops.TryGetValue(agent, out var state) || !state.Pending)
                && RequirementsMet(agent, exit));

    private static bool RequirementsMet(Agent agent, ExfiltrationPoint exit)
    {
        foreach (var requirement in exit.Requirements)
            if (requirement != null && !requirement.Met(agent.Bot.GetPlayer, exit)) return false;
        return true;
    }
}
