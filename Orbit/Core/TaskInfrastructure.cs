using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Orbit.Entities;
using Orbit.Helpers;
using Orbit.Tasks;

namespace Orbit.Core;

// ╔══════════════════════════════════════════════════════════════════╗
// ║ Task plumbing — definition lookup, the BaseTaskManager scoring    ║
// ║ loop, and the two managers (per-agent action, per-squad strategy).║
// ║ Lives in one file because the inheritance chain reads together as ║
// ║ a single unit.                                                     ║
// ╚══════════════════════════════════════════════════════════════════╝

/// <summary>
/// Type-keyed bag of task definitions used at boot to assemble the
/// action / strategy / component registries before they're frozen into
/// arrays. The Type key is the concrete class, which prevents the same
/// task from being registered twice by accident.
/// </summary>
public class DefinitionRegistry<T>
{
    private readonly Dictionary<Type, T> _defs = new();

    public Dictionary<Type, T>.ValueCollection Values => _defs.Values;

    public void Add<TI>(TI instance) where TI : T
    {
        _defs.Add(typeof(TI), instance);
    }

    public void Remove<TI>() where TI : T
    {
        _defs.Remove(typeof(TI));
    }
}

/// <summary>
/// Base scoring/picking loop shared by ActionManager (per-Agent) and
/// StrategyManager (per-Squad). Each tick: every task computes its score
/// per entity, then per entity we pick the highest-scoring task (with
/// hysteresis bias toward the current pick to avoid thrash), then every
/// active task runs Update.
/// </summary>
public class BaseTaskManager<TEntity>(Task<TEntity>[] tasks) where TEntity : Entity
{
    public readonly Task<TEntity>[] Tasks = tasks;

    public void RemoveEntity(TEntity entity)
    {
        Log.Debug($"Removing {entity} from {this}");
        entity.TaskAssignment.Task?.Deactivate(entity);
        entity.TaskAssignment = new TaskAssignment();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected void UpdateScores()
    {
        for (var i = 0; i < Tasks.Length; i++)
            Tasks[i].UpdateScore(i);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected void UpdateTasks()
    {
        for (var i = 0; i < Tasks.Length; i++)
            Tasks[i].Update();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected void PickTask(TEntity entity)
    {
        var assignment = entity.TaskAssignment;

        var highestScore = 0f;
        var nextTaskOrdinal = 0;

        // Seed from the current task plus its hysteresis bonus so a tied
        // score doesn't cause every-tick switching.
        if (assignment.Task != null)
        {
            nextTaskOrdinal = assignment.Ordinal;
            highestScore = entity.TaskScores[assignment.Ordinal] + assignment.Task.Hysteresis;
        }

        Task<TEntity> nextTask = null;

        for (var j = 0; j < Tasks.Length; j++)
        {
            var task = Tasks[j];
            var score = entity.TaskScores[j];

            if (score <= highestScore) continue;

            highestScore = score;
            nextTaskOrdinal = j;
            nextTask = task;
        }

        // No switch needed when nothing scored higher than the current pick.
        if (nextTask == null) return;

        Log.Debug($"{entity} changing task from {assignment.Task} to {nextTask} with utility {highestScore}");

        assignment.Task?.Deactivate(entity);
        nextTask.Activate(entity);

        entity.TaskAssignment = new TaskAssignment(nextTask, nextTaskOrdinal);
    }

    public override string ToString() => GetType().Name;
}

/// <summary>Per-Agent action manager. Runs every frame.</summary>
public class ActionManager(AgentData dataset, Task<Agent>[] tasks) : BaseTaskManager<Agent>(tasks)
{
    public void Update()
    {
        UpdateScores();
        PickTasks();
        UpdateTasks();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PickTasks()
    {
        var agents = dataset.Entities.Values;

        for (var i = 0; i < agents.Count; i++)
        {
            var agent = agents[i];
            var assignment = agent.TaskAssignment;

            if (!agent.IsActive)
            {
                if (assignment.Task != null)
                {
                    assignment.Task.Deactivate(agent);
                    agent.TaskAssignment = new TaskAssignment();
                }
                continue;
            }

            PickTask(agent);
        }
    }
}

/// <summary>
/// Per-Squad strategy manager. Throttled to twice a second — strategies
/// don't need frame-rate granularity (they re-pick objectives on the
/// order of seconds anyway) and the per-squad work is heavier than per-
/// agent action scoring.
/// </summary>
public class StrategyManager(SquadData dataset, Task<Squad>[] tasks) : BaseTaskManager<Squad>(tasks)
{
    private readonly TimePacing _pacing = new(0.5f);

    public void Update()
    {
        if (_pacing.Blocked()) return;

        UpdateScores();
        PickTasks();
        UpdateTasks();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PickTasks()
    {
        var squads = dataset.Entities.Values;

        for (var i = 0; i < squads.Count; i++)
        {
            var squad = squads[i];
            var assignment = squad.TaskAssignment;

            if (squad.Size == 0)
            {
                if (assignment.Task != null)
                {
                    assignment.Task.Deactivate(squad);
                    squad.TaskAssignment = new TaskAssignment();
                }
                continue;
            }

            PickTask(squad);
        }
    }
}
