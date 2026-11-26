using System;
using System.Collections.Generic;

namespace Orbit.Looting;

// Simulate legal moves before changing the inventory. Cells in separate compartments
// never combine, and a relocation must be executable with the other items still present.
internal sealed class BackpackLayout
{
    internal readonly struct Rect
    {
        public readonly int Grid, X, Y, W, H;
        public readonly bool Turned;
        public Rect(int grid, int x, int y, int w, int h, bool turned = false)
        { Grid = grid; X = x; Y = y; W = w; H = h; Turned = turned; }
        public bool Hits(Rect other) => Grid == other.Grid && X < other.X + other.W && X + W > other.X
            && Y < other.Y + other.H && Y + H > other.Y;
    }

    internal sealed class Cargo
    {
        public Rect Original;
        public bool Movable;
        public double DropCost;
        public bool[] Allowed;
    }

    internal sealed class Plan
    {
        public Rect Target;
        public double Loss;
        public readonly List<(int Index, Rect To)> Moves = new();
        public readonly List<int> Drops = new();
    }

    private readonly (int W, int H)[] grids;
    private readonly List<Cargo> cargo;
    private readonly Rect[] positions;
    private readonly int[] initialFree, free;
    private readonly List<int> blockers = new();
    internal BackpackLayout((int, int)[] grids, List<Cargo> cargo)
    {
        this.grids = grids;
        this.cargo = cargo;
        positions = new Rect[cargo.Count];
        initialFree = Array.ConvertAll(grids, grid => grid.Item1 * grid.Item2);
        foreach (var item in cargo) initialFree[item.Original.Grid] -= item.Original.W * item.Original.H;
        free = new int[grids.Length];
    }

    internal IEnumerable<Rect> Positions(int g, int w, int h)
    {
        for (int turn = 0; turn < (w == h ? 1 : 2); turn++)
        {
            int width = turn == 0 ? w : h, height = turn == 0 ? h : w;
            for (int y = 0; y <= grids[g].H - height; y++)
                for (int x = 0; x <= grids[g].W - width; x++)
                    yield return new Rect(g, x, y, width, height, turn != 0);
        }
    }

    internal Plan Consider(Rect target, double incoming, Plan best)
    {
        blockers.Clear();
        Array.Copy(initialFree, free, free.Length);
        for (int i = 0; i < cargo.Count; i++)
        {
            positions[i] = cargo[i].Original;
            if (!positions[i].Hits(target)) continue;
            if (!cargo[i].Movable) return best;
            blockers.Add(i);
        }
        // Give irreplaceable and expensive objects first choice of the available space.
        blockers.Sort((a, b) => cargo[b].DropCost.CompareTo(cargo[a].DropCost));
        var plan = new Plan { Target = target };
        foreach (int i in blockers)
        {
            if (TryRelocate(i, target, out var to))
            {
                free[positions[i].Grid] += to.W * to.H;
                free[to.Grid] -= to.W * to.H;
                positions[i] = to;
                plan.Moves.Add((i, to));
            }
            else
            {
                plan.Loss += cargo[i].DropCost;
                if (plan.Loss >= incoming || (best != null && plan.Loss > best.Loss)) return best;
                plan.Drops.Add(i);
                // Leave its cells occupied. That lets us execute every move before the first drop.
            }
        }
        if (best != null && plan.Loss == best.Loss &&
            plan.Moves.Count + plan.Drops.Count >= best.Moves.Count + best.Drops.Count) return best;
        return plan;
    }

    private bool TryRelocate(int index, Rect target, out Rect to)
    {
        var item = cargo[index];
        var from = item.Original;
        for (int g = 0; g < grids.Length; g++)
        {
            if (!item.Allowed[g] || free[g] == 0 || (from.Grid != g && free[g] < from.W * from.H)) continue;
            foreach (var candidate in Positions(g, from.Turned ? from.H : from.W, from.Turned ? from.W : from.H))
            {
                if (candidate.Hits(target)) continue;
                bool free = true;
                for (int j = 0; j < positions.Length; j++)
                    if (j != index && positions[j].Hits(candidate)) { free = false; break; }
                if (free) { to = candidate; return true; }
            }
        }
        to = default;
        return false;
    }
}
