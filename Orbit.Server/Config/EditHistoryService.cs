using Orbit.Server.Zones;
using SPTarkov.DI.Annotations;

namespace Orbit.Server.Config;

/// <summary>
/// Undo / redo for everything the web UI edits: the server config and the zone editor's working copies.
///
/// The pages mutate those objects directly through their bindings, so nothing announces an edit. Same answer
/// as the unsaved-changes button: the layout polls (<see cref="Track"/>, twice a second) and the history
/// works on serialized snapshots. A change is committed as ONE step once the state has stopped moving for a
/// poll, so a slider drag, a zone drag or a typed value is a single undo, not one per intermediate value.
/// Save, Discard all, Reset and pack imports are ordinary state changes and go through the same path.
/// </summary>
[Injectable(InjectionType.Singleton)]
public class EditHistoryService(ConfigService configs, ZoneStoreService zones)
{
    private const int MaxSteps = 100;

    private sealed class Snapshot
    {
        public string Config = "";
        public Dictionary<string, string> Zones = new();

        public bool SameAs(Snapshot other)
        {
            if (Config != other.Config || Zones.Count != other.Zones.Count) return false;
            foreach (var kv in Zones)
                if (!other.Zones.TryGetValue(kv.Key, out var json) || json != kv.Value) return false;
            return true;
        }
    }

    private readonly object _lock = new();
    private readonly List<Snapshot> _undo = new();
    private readonly List<Snapshot> _redo = new();
    private Snapshot? _committed; // the state the next undo returns FROM
    private Snapshot? _lastSeen;  // the state at the previous poll
    private bool _moving;

    public bool CanUndo { get { lock (_lock) return _undo.Count > 0 || _moving; } }
    public bool CanRedo { get { lock (_lock) return _redo.Count > 0 && !_moving; } }
    public int UndoCount { get { lock (_lock) return _undo.Count + (_moving ? 1 : 0); } }
    public int RedoCount { get { lock (_lock) return _moving ? 0 : _redo.Count; } }

    /// <summary>Preset switches start a separate editing history. Undo must not restore another preset.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _undo.Clear();
            _redo.Clear();
            _committed = _lastSeen = Capture(out _);
            _moving = false;
        }
    }

    /// <summary>Polled by the layout. Returns true when the undo / redo availability may have changed.</summary>
    public bool Track()
    {
        lock (_lock) return TrackLocked(settle: false);
    }

    public bool Undo()
    {
        lock (_lock)
        {
            TrackLocked(settle: true); // an edit still in flight becomes the step being undone
            if (_undo.Count == 0 || _committed == null) return false;
            var target = _undo[^1];
            _undo.RemoveAt(_undo.Count - 1);
            _redo.Add(_committed);
            Restore(target);
            return true;
        }
    }

    public bool Redo()
    {
        lock (_lock)
        {
            TrackLocked(settle: true); // a fresh edit clears the redo stack, as in any editor
            if (_redo.Count == 0 || _committed == null) return false;
            var target = _redo[^1];
            _redo.RemoveAt(_redo.Count - 1);
            _undo.Add(_committed);
            Restore(target);
            return true;
        }
    }

    private bool TrackLocked(bool settle)
    {
        var current = Capture(out var savedBaselines);
        if (_committed == null || _lastSeen == null)
        {
            _committed = _lastSeen = current;
            return false;
        }

        // A map the history has never seen joins the committed state with its SAVED content: opening a map is
        // not an edit, but a map that enters the working set already modified (pack import) is one.
        foreach (var kv in savedBaselines)
        {
            if (_committed.Zones.ContainsKey(kv.Key)) continue;
            _committed.Zones[kv.Key] = kv.Value;
            if (!_lastSeen.Zones.ContainsKey(kv.Key)) _lastSeen.Zones[kv.Key] = kv.Value;
        }

        if (!current.SameAs(_lastSeen))
        {
            _lastSeen = current;
            var wasMoving = _moving;
            _moving = !current.SameAs(_committed);
            if (!settle) return wasMoving != _moving;
        }
        if (!_moving) return false;

        // Stable since the previous poll (or forced by an undo / redo): commit the step.
        _moving = false;
        if (current.SameAs(_committed)) return true;
        _undo.Add(_committed);
        if (_undo.Count > MaxSteps) _undo.RemoveAt(0);
        _redo.Clear();
        _committed = current;
        return true;
    }

    private Snapshot Capture(out Dictionary<string, string> savedBaselines)
    {
        var snapshot = new Snapshot { Config = configs.ToJson() };
        savedBaselines = new Dictionary<string, string>();
        foreach (var kv in zones.SnapshotWorking())
        {
            snapshot.Zones[kv.Key] = kv.Value.Current;
            savedBaselines[kv.Key] = kv.Value.Saved;
        }
        return snapshot;
    }

    private void Restore(Snapshot target)
    {
        configs.RestoreJson(target.Config);
        zones.RestoreWorking(target.Zones);
        // Maps opened after the target was taken were untouched back then: they keep their current content,
        // and the committed state must say so or the next poll would read them as a new edit.
        var now = Capture(out _);
        _committed = now;
        _lastSeen = now;
        _moving = false;
    }
}
