using Orbit.Entities;
using Orbit.Helpers;
using Orbit.Navigation;
using Orbit.Systems;
using UnityEngine;

namespace Orbit.Tasks;

internal static class LootMainEntry
{
    internal static void Begin(Squad squad, MainObjective main, WaypointSystem waypoints)
    {
        // Bypass the neighbourhood cooldown once, on actual entry. Clearing cached verdicts
        // performs no path query: the existing frame queue rechecks candidates as needed.
        RefreshLocalReachability(squad, main, waypoints);
        var current = squad.Objective.Location;
        var pursuingCellLoot = current != null
            && current.Category is WaypointCategory.LooseLoot or WaypointCategory.ContainerLoot or WaypointCategory.Corpse
            && waypoints.WorldToCell(current.Position) == main.CellCoords;
        squad.Objective.RepickRequested = !pursuingCellLoot;
        Log.Info($"LOOT MAIN ENTRY: {squad} cell={main.CellCoords} preservePoi={pursuingCellLoot} localValidation={main.LootValueLocalValidation}");
    }

    internal static void RefreshLocalReachability(Squad squad, MainObjective main, WaypointSystem waypoints)
    {
        if (main.LootValueLocalValidation || squad.Leader == null
            || waypoints.WorldToCell(squad.Leader.Position) != main.CellCoords) return;
        waypoints.RefreshLootEntry(squad, main.CellCoords);
        squad.RecentlyRefreshedCells[main.CellCoords] = Time.time;
        main.LootValueLocalValidation = true;
        Log.Info($"LOOT MAIN RECHECK: {squad} cell={main.CellCoords} local path cache refreshed; candidates use frame budget");
    }
}
