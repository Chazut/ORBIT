using System.Collections.Generic;

namespace Orbit.Fika;

/// <summary>Per-door receive order, including local interactions not yet acknowledged by the host.</summary>
internal sealed class DoorSyncOrder
{
    private readonly Dictionary<string, ulong> _revisions = new();
    private readonly Dictionary<string, ulong> _fences = new();
    private ulong _nextToken;

    internal void Clear()
    {
        _revisions.Clear();
        _fences.Clear();
        _nextToken = 0;
    }

    internal ulong Fence(string door)
    {
        var token = ++_nextToken;
        _fences[door] = token;
        return token;
    }

    internal bool Receive(string door, ulong revision)
    {
        if (revision == 0 || (_revisions.TryGetValue(door, out var previous) && revision <= previous))
            return false;
        _revisions[door] = revision;
        return !_fences.ContainsKey(door);
    }

    internal bool IsCurrent(string door, ulong revision)
        => !_fences.ContainsKey(door) && _revisions.TryGetValue(door, out var current) && current == revision;

    internal void Acknowledge(string door, ulong revision, ulong token)
    {
        Receive(door, revision);
        if (_fences.TryGetValue(door, out var current) && current == token) _fences.Remove(door);
    }
}
