using System.Collections.Generic;
using System.Diagnostics;
using Orbit.Entities;
using Orbit.Navigation;
using Orbit.Sain;

namespace Orbit.Systems;

public partial class WaypointSystem
{
    private readonly Dictionary<Squad, MainGeneration> _mainGeneration = new();

    internal bool MainObjectivesPending(Squad squad) => _mainGeneration.ContainsKey(squad);

    internal void CancelMainGeneration(Squad squad)
    {
        if (!_mainGeneration.TryGetValue(squad, out var work)) return;
        CancelOperationWork(work);
        _mainGeneration.Remove(squad);
    }

    internal void QueueMainObjectives(Squad squad)
    {
        CancelMainGeneration(squad);
        var work = new MainGeneration(this, squad);
        _mainGeneration.Add(squad, work);
        ContinueOperationWork(work);
    }

    // Quest candidates can share a trigger position. Check it once, and spread NavMesh
    // queries across frames instead of repeating a full pool scan for each failed roll.
    private sealed class MainGeneration : IObjectiveWork
    {
        private readonly WaypointSystem _owner;
        private readonly Squad _squad;
        private readonly HashSet<int> _reachable = new();
        private readonly Dictionary<UnityEngine.Vector3, bool> _positions = new();
        private List<Waypoint> _pool;
        private int _cursor;
        private bool _checkQuests;

        internal MainGeneration(WaypointSystem owner, Squad squad) { _owner = owner; _squad = squad; }

        public void ResumeObjectiveWork()
        {
            if (!_owner._mainGeneration.TryGetValue(_squad, out var current) || current != this) return;
            if (_squad.Leader?.Bot == null || _squad.Leader.Bot.IsDead || _squad.ExtractRequested)
            { _owner.CancelMainGeneration(_squad); return; }
            if (_pool == null)
            {
                _checkQuests = MainObjectiveBuilder.NeedsQuestReachability(_squad);
                _pool = _checkQuests
                    ? MainObjectiveBuilder.BuildQuestPool(_owner) : new List<Waypoint>();
            }
            if (_cursor < _pool.Count)
            {
                var timer = Stopwatch.StartNew();
                var queries = 0;
                while (_cursor < _pool.Count && queries < 2 && timer.Elapsed.TotalMilliseconds < 2)
                {
                    var point = _pool[_cursor++];
                    if (!_positions.TryGetValue(point.Position, out var reachable))
                    {
                        reachable = _owner.IsReachableFromPosition(_squad.SpawnPosition, point.Position);
                        _positions.Add(point.Position, reachable);
                        queries++;
                    }
                    if (reachable) _reachable.Add(point.Id);
                }
                _owner.ContinueOperationWork(this);
                return;
            }
            _owner.CancelMainGeneration(_squad);
            MainObjectiveBuilder.Generate(_squad, _owner, _checkQuests ? _reachable : null);
            Log.Info($"MAIN GENERATION: {_squad} quests={_pool.Count} positions={_positions.Count} reachable={_reachable.Count}");
        }
    }
}
