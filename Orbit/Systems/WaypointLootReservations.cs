using System.Collections.Generic;
using EFT.Interactive;
using Orbit.Navigation;
using UnityEngine.AI;

namespace Orbit.Systems;

public partial class WaypointSystem
{
    // Per-waypoint reservation: while a bot is actively looting a waypoint, other bots (same squad or
    // different) cannot start a second loot on it. Cleared by ReleaseClaim or automatically on
    // RemoveWaypoint.
    private readonly Dictionary<int, int> _claims = new();

    private sealed class LandedAirdrop
    {
        internal LootableContainer Container;
        internal Waypoint Point;
        internal bool Retired;
        internal bool LootInProgress;
    }

    private readonly List<LandedAirdrop> _landedAirdrops = new();

    internal void RegisterLandedAirdrop(LootableContainer container)
    {
        if (container == null) return;
        PruneDestroyedAirdrops();
        foreach (var drop in _landedAirdrops) if (drop.Container == container) return;
        _landedAirdrops.Add(new LandedAirdrop { Container = container });
        RegisterAmbushAirdrop(container);
        Log.Info($"AIRDROP LOOT: landed crate registered at {container.transform.position}");
    }

    private Waypoint AirdropPoint(LootableContainer container)
    {
        if (container == null) return null;
        foreach (var drop in _landedAirdrops)
        {
            if (drop.Container != container || drop.Retired) continue;
            if (drop.Point != null) return drop.Point;
            if (!NavMesh.SamplePosition(container.transform.position, out var hit, 2f, NavMesh.AllAreas)) return null;
            // Keep the crate out of the general grid: each bot must discover it or choose its ambush.
            drop.Point = new Waypoint(NewRuntimeWaypointId(), WaypointCategory.ContainerLoot,
                "Airdrop", hit.position, 1f, new(), new(), container) { IsAirdrop = true };
            return drop.Point;
        }
        return null;
    }

    private LandedAirdrop FindLandedAirdrop(int waypointId)
    {
        foreach (var drop in _landedAirdrops)
            if (drop.Point?.Id == waypointId) return drop;
        return null;
    }

    private bool RetireLandedAirdrop(int waypointId)
    {
        var drop = FindLandedAirdrop(waypointId);
        if (drop == null || drop.Retired) return false;
        drop.Retired = true;
        _claims.Remove(waypointId);
        return true;
    }

    private void PruneDestroyedAirdrops()
    {
        for (int i = _landedAirdrops.Count - 1; i >= 0; i--)
        {
            var drop = _landedAirdrops[i];
            if (drop.Container != null) continue;
            if (drop.Point != null) _claims.Remove(drop.Point.Id);
            _landedAirdrops.RemoveAt(i);
        }
    }

    internal bool BeginAirdropLoot(int waypointId, int agentId)
    {
        var drop = FindLandedAirdrop(waypointId);
        if (drop == null || !TryClaim(waypointId, agentId)) return false;
        drop.LootInProgress = true;
        return true;
    }

    internal void EndLootSession(Waypoint point, int agentId)
    {
        if (point.IsAirdrop && _claims.TryGetValue(point.Id, out var owner) && owner == agentId)
        {
            var drop = FindLandedAirdrop(point.Id);
            if (drop != null) drop.LootInProgress = false;
        }
        ReleaseClaim(point.Id, agentId);
    }

    internal void ReleaseAirdropClaims(int agentId)
    {
        foreach (var drop in _landedAirdrops)
            if (drop.Point != null) EndLootSession(drop.Point, agentId);
    }

    /// <summary>
    /// Reserve an existing waypoint for an agent. Reclaiming one's own reservation is idempotent.
    /// </summary>
    public bool TryClaim(int waypointId, int agentId)
    {
        LandedAirdrop airdrop = null;
        if (!_waypointCells.ContainsKey(waypointId))
        {
            airdrop = FindLandedAirdrop(waypointId);
            if (airdrop == null || airdrop.Retired || airdrop.Container == null)
            {
                Log.Debug($"LOOT RECOVERY: denied claim on removed waypoint {waypointId} for agent {agentId}");
                return false;
            }
        }
        if (_claims.TryGetValue(waypointId, out var holder))
        {
            var ok = holder == agentId;
            Log.Debug($"TryClaim: loc={waypointId} agent={agentId} already held by {holder} → {(ok ? "self-reclaim" : "DENIED")}");
            return ok;
        }
        _claims[waypointId] = agentId;
        if (airdrop != null)
            Log.Info($"AIRDROP LOOT: reserved point={waypointId} agent={agentId}");
        Log.Debug($"TryClaim: loc={waypointId} agent={agentId} GRANTED");
        return true;
    }

    public void ReleaseClaim(int waypointId, int agentId)
    {
        if (_claims.TryGetValue(waypointId, out var holder) && holder == agentId)
        {
            // A camp or objective can end one tick before the inventory action cancels.
            if (_landedAirdrops.Count != 0 && !_waypointCells.ContainsKey(waypointId)
                && FindLandedAirdrop(waypointId)?.LootInProgress == true) return;
            _claims.Remove(waypointId);
            Log.Debug($"ReleaseClaim: loc={waypointId} agent={agentId} released");
        }
    }

    public bool IsClaimed(int waypointId) => _claims.ContainsKey(waypointId);

    internal bool IsClaimedByOther(int waypointId, int agentId)
        => _claims.TryGetValue(waypointId, out var holder) && holder != agentId;
}
