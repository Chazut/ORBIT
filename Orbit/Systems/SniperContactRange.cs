using System.Collections.Generic;
using EFT;
using Orbit.Entities;
using Orbit.Helpers;
using UnityEngine;

namespace Orbit.Systems;

// Only the awake contact handed off by a Ghost sniper receives this short, renewable allowance.
// Native/SAIN sight rays, field of view, visibility accumulation and shooting remain untouched.
internal sealed class SniperContactRange
{
    private static readonly Dictionary<BotOwner, SniperContactRange> Active = new();
    internal readonly Agent Observer;
    internal readonly SniperPlan Plan;
    internal readonly Player Target;
    internal readonly EnemyInfo Enemy;
    internal float Reach, Until, NextPoll, LastSeen, NextReport, LastReport;
    internal float NativeCheckAt = -1, InputRange, EffectiveRange;
    internal readonly float Started = Time.time;
    internal int LastState = -1;

    internal SniperContactRange(Agent observer, SniperPlan plan, Player target, EnemyInfo enemy, float reach)
    {
        Observer = observer; Plan = plan; Target = target; Enemy = enemy; Reach = reach;
        Until = Time.time + 2f; LastSeen = Time.time;
        Active[observer.Bot] = this;
    }

    internal void Remove()
    {
        if (Active.TryGetValue(Observer.Bot, out var value) && ReferenceEquals(value, this))
            Active.Remove(Observer.Bot);
    }

    internal bool IsValid => ServerConfig.Rush.Enabled && Time.time <= Until
        && Plan.ContactObserver == Observer && !Observer.IsDormant && !Observer.Bot.IsDead
        && Observer.Bot.BotState == EBotState.Active && Observer.Bot.gameObject.activeInHierarchy
        && ReferenceEquals(Observer.Bot.Memory.GoalEnemy, Enemy)
        && Target.HealthController is { IsAlive: true } && !DormancySystem.IsDormantProfile(Target.ProfileId)
        && Target.AIData?.BotOwner is { IsDead: false, BotState: EBotState.Active } other
        && Observer.Bot.BotsGroup is { } group && other.BotsGroup != null
        && other.gameObject.activeInHierarchy && other.BotsGroup != group
        && group.IsEnemy(Enemy.Person);

    internal static void Extend(EnemyInfo enemy, ref float additional)
    {
        if (enemy?.Owner == null || !Active.TryGetValue(enemy.Owner, out var contact)
            || !ReferenceEquals(contact.Enemy, enemy) || !contact.IsValid) return;
        if (enemy.Owner.LookSensor == null) return;
        var baseline = enemy.Owner.LookSensor.VisibleDist;
        var reach = contact.Reach;
        if (!(reach > 0f && reach <= 400f) || float.IsNaN(baseline) || float.IsInfinity(baseline)
            || float.IsNaN(additional) || float.IsInfinity(additional)) return;
        contact.NativeCheckAt = Time.time;
        contact.InputRange = baseline + additional;
        // Do not widen other enemies, or remove a larger legitimate SAIN bonus.
        if ((contact.Target.Position - contact.Observer.Position).sqrMagnitude <= reach * reach)
            additional = Mathf.Max(additional, reach - baseline);
        contact.EffectiveRange = baseline + additional;
    }
}
