using System.Collections.Generic;
using Orbit.Entities;
using Orbit.Helpers;

namespace Orbit.Tasks;

/// <summary>
/// One half of a Task↔Entity binding. <see cref="Ordinal"/> is the index
/// of the task in the entity's score array, so an entity can re-find its
/// active task in O(1).
/// </summary>
public readonly struct TaskAssignment(BaseTask task, int ordinal)
{
    public readonly BaseTask Task = task;
    public readonly int Ordinal = ordinal;
}

/// <summary>
/// Untyped base — used by the runtime when an entity needs to talk about
/// "whatever task it's running" without knowing the concrete entity type.
/// </summary>
public abstract class BaseTask(float hysteresis)
{
    public readonly float Hysteresis = hysteresis;

    public abstract void Deactivate(Entity entity);

    public override string ToString() => GetType().Name;
}

/// <summary>
/// Typed task that operates on a specific entity subtype (Agent or Squad).
/// Subclasses provide <see cref="UpdateScore"/> (scoring per entity each
/// tick) and <see cref="Update"/> (the actual work for currently-active
/// entities).
/// </summary>
public abstract class Task<T>(float hysteresis) : BaseTask(hysteresis) where T : Entity
{
    protected readonly List<T> ActiveEntities = new(16);
    private readonly HashSet<Entity> _entitySet = [];

    public abstract void UpdateScore(int ordinal);
    public abstract void Update();

    public virtual void Activate(T entity)
    {
        Log.Debug($"{entity} activating {this}");

        if (!_entitySet.Add(entity))
            return;

        ActiveEntities.Add(entity);
    }

    public override void Deactivate(Entity entity)
    {
        Log.Debug($"{entity} deactivating {this}");

        if (!_entitySet.Remove(entity))
            return;

        for (var i = 0; i < ActiveEntities.Count; i++)
        {
            var candidate = ActiveEntities[i];
            if (candidate.Id != entity.Id) continue;

            Deactivate(candidate);
            ActiveEntities.SwapRemoveAt(i);
            return;
        }
    }

    protected virtual void Deactivate(T entity)
    {
    }
}
