using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using EFT;

namespace Orbit.Entities;

/// <summary>
/// ECS-style storage for entities + their side components. Merges what
/// was a 6-file <c>Data/</c> + <c>Entities/Entity.cs</c> structure into
/// one cohesive unit. Three layers:
///   - <see cref="EntityArray{T}"/> owns the live entities, indexable by
///     stable id, swap-removable in O(1).
///   - <see cref="ComponentArray{T}"/> is a parallel side-array keyed on
///     the same id, used when a per-entity component shouldn't live as a
///     field on the entity itself (rare — most components do).
///   - <see cref="Dataset{T,TE}"/> couples one EntityArray with N
///     ComponentArrays so adding/removing an entity propagates correctly.
/// </summary>
public interface IComponentArray
{
    void Add(int id);
    void Remove(int id);
}

public class ComponentArray<T>(int capacity = 16) : IComponentArray where T : class, new()
{
    private readonly List<T> _data = new(capacity);

    public T this[int id]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _data[id];
    }

    public void Add(int id)
    {
        T component = new();

        if (id >= _data.Count)
        {
            // Add empty slots up to and excluding the one represented by id,
            // then the new component.
            for (var i = _data.Count; i < id; i++)
                _data.Add(null);
            _data.Add(component);
        }
        else
        {
            _data[id] = component;
        }
    }

    public void Remove(int id)
    {
        if (id == _data.Count - 1)
            _data.RemoveAt(id);
        else
            _data[id] = null;
    }

    public override string ToString() => $"ComponentArray<{typeof(T).Name}>()";
}

/// <summary>
/// Id-indexed entity list with free-id recycling. Removal swaps the last
/// entry into the gap and pops the tail, so iteration order isn't stable
/// but lookup-by-id and removal are both O(1).
/// </summary>
public class EntityArray<T>(int capacity = 16) where T : Entity
{
    public readonly List<T> Values = new(capacity);

    private readonly List<int?> _idSlots = new(capacity);
    private readonly Stack<int> _freeIds = new(capacity);

    public T this[int id]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            var index = _idSlots[id];
            return index != null ? Values[index.Value] : throw new KeyNotFoundException($"Key {id} not found");
        }
    }

    protected int Reserve()
    {
        var valueIndex = Values.Count;
        int id;

        if (_freeIds.Count > 0)
        {
            id = _freeIds.Pop();
            _idSlots[id] = valueIndex;
        }
        else
        {
            id = valueIndex;
            _idSlots.Add(id);
        }

        return id;
    }

    public bool Remove(T entity)
    {
        var slot = _idSlots[entity.Id];
        if (!slot.HasValue) return false;

        var valueIndex = slot.Value;
        var lastValueIndex = Values.Count - 1;

        Values[valueIndex] = Values[lastValueIndex];
        Values.RemoveAt(lastValueIndex);

        if (Values.Count == 0)
        {
            _freeIds.Clear();
            _idSlots.Clear();
            return true;
        }

        if (entity.Id == _idSlots.Count - 1)
        {
            _idSlots.RemoveAt(entity.Id);
        }
        else
        {
            _freeIds.Push(entity.Id);
            _idSlots[entity.Id] = null;
        }

        if (valueIndex == lastValueIndex) return true;

        var swapped = Values[valueIndex];
        _idSlots[swapped.Id] = valueIndex;

        return true;
    }
}

public class AgentArray(int capacity = 32) : EntityArray<Agent>(capacity)
{
    public Agent Add(BotOwner bot, int taskCount)
    {
        var id = Reserve();
        var agent = new Agent(id, bot, new float[taskCount]);
        Values.Add(agent);
        return agent;
    }
}

public class SquadArray(int capacity = 16) : EntityArray<Squad>(capacity)
{
    public Squad Add(int taskCount, int targetMembersCount)
    {
        var id = Reserve();
        var squad = new Squad(id, new float[taskCount], targetMembersCount)
        {
            // SAIN-style spread: same-faction squads in the same raid don't all
            // hit their extract threshold at the exact same rouble count.
            ExtractValueRandomization = UnityEngine.Random.Range(0.75f, 1.25f),
        };
        Values.Add(squad);
        return squad;
    }
}

/// <summary>
/// One entity array + N registered component arrays. RegisterComponent
/// wires a component array so AddEntity/RemoveEntity propagate to every
/// component. <see cref="GetComponentArray"/> looks one up by its T.
/// </summary>
public class Dataset<T, TE>(TE entities) where TE : EntityArray<T> where T : Entity
{
    public readonly TE Entities = entities;

    private readonly List<IComponentArray> _components = [];
    private readonly Dictionary<Type, IComponentArray> _componentsTypeMap = new();

    protected void AddEntityComponents(T entity)
    {
        for (var i = 0; i < _components.Count; i++)
            _components[i].Add(entity.Id);
    }

    public void RemoveEntity(T entity)
    {
        Entities.Remove(entity);
        for (var i = 0; i < _components.Count; i++)
            _components[i].Remove(entity.Id);
    }

    public void RegisterComponent(IComponentArray componentArray)
    {
        _componentsTypeMap.Add(componentArray.GetType(), componentArray);
        _components.Add(componentArray);
    }

    public ComponentArray<TC> GetComponentArray<TC>() where TC : class, new()
    {
        return (ComponentArray<TC>)_componentsTypeMap[typeof(ComponentArray<TC>)];
    }

    public override string ToString() => GetType().Name;
}

public class AgentData() : Dataset<Agent, AgentArray>(new AgentArray())
{
    public Agent AddEntity(BotOwner bot, int taskCount)
    {
        var agent = Entities.Add(bot, taskCount);
        AddEntityComponents(agent);
        return agent;
    }
}

public class SquadData() : Dataset<Squad, SquadArray>(new SquadArray())
{
    public Squad AddEntity(int taskCount, int targetMembersCount)
    {
        var squad = Entities.Add(taskCount, targetMembersCount);
        AddEntityComponents(squad);
        return squad;
    }
}
