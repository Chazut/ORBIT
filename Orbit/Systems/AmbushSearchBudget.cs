using System.Collections.Generic;

namespace Orbit.Systems;

internal sealed class AmbushSearchBudget(int remaining = 24)
{
    internal int Remaining = remaining;
    internal readonly Dictionary<string, int> Rejected = new();
    internal string Rejections => Rejected.Count == 0 ? "no cover candidates" : string.Join(",", Rejected);
    internal bool Reject(string reason)
    { Rejected.TryGetValue(reason, out var count); Rejected[reason] = count + 1; return false; }
}
