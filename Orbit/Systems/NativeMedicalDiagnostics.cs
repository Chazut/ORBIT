using System;
using System.Collections.Generic;
using System.Globalization;
using EFT;
using EFT.InventoryLogic;
using UnityEngine;

namespace Orbit.Systems;

/// <summary>Read-only medical observations for Sanitar and his guards, including before their first Ghost.</summary>
internal static class NativeMedicalDiagnostics
{
    private struct UseObservation
    {
        public bool? Value;
        public float Since;

        public bool Update(bool? value)
        {
            if (Value == value) return false;
            Value = value;
            Since = Time.time;
            return true;
        }

        // This is time continuously observed at polls, not the start time of an engine operation.
        public float ObservedFor => Value == true ? Time.time - Since : 0f;
    }

    private sealed class Observation
    {
        public UseObservation Medicine, FirstAid, Surgery, Stimulator;
        public object Hands, Operation;
        public float HandsSince, OperationSince, NextReport, LastReport, NextErrorReport;
        public BotStandByType? StandBy;
        public EBotState State;
        public BotLogicDecision? Decision;
        public bool Ghost, BodyActive, Initialized, Dirty;
        public string LastRefusal;
        public float RefusedAt;
    }

    private const float ChangeInterval = 5f;
    private const float StableInterval = 30f;
    private static readonly Dictionary<BotOwner, Observation> Observations = new();

    public static void Clear() => Observations.Clear();
    public static void Forget(BotOwner bot)
    {
        if (!ReferenceEquals(bot, null)) Observations.Remove(bot);
    }

    private static bool IsMedicalRole(BotOwner bot)
        => bot?.Profile?.Info?.Settings?.Role is WildSpawnType.bossSanitar or WildSpawnType.followerSanitar;

    private static Observation Get(BotOwner bot)
    {
        if (!Observations.TryGetValue(bot, out var observation))
            Observations.Add(bot, observation = new Observation());
        return observation;
    }

    // Keep the actual per-bot refusal even when the general refusal log is throttled.
    // Do not infer a guard's refusal from its leader: group evaluation can stop at the leader.
    public static void Refused(BotOwner bot, string reason)
    {
        if (!IsMedicalRole(bot) || bot.IsDead) return;
        var observation = Get(bot);
        observation.Dirty |= observation.LastRefusal != reason;
        observation.LastRefusal = reason;
        observation.RefusedAt = Time.time;
    }

    public static void Observe(BotOwner bot, bool ghost)
    {
        if (!IsMedicalRole(bot)) return;
        if (bot.IsDead) { Forget(bot); return; }
        var observation = Get(bot);
        try
        {
            var medicine = bot.Medecine;
            observation.Dirty |= observation.Medicine.Update(medicine?.Using);
            observation.Dirty |= observation.FirstAid.Update(medicine?.FirstAid?.Using);
            observation.Dirty |= observation.Surgery.Update(medicine?.SurgicalKit?.Using);
            observation.Dirty |= observation.Stimulator.Update(medicine?.Stimulators?.Using);
            var hands = bot.GetPlayer?.HandsController;
            var operation = (hands as Player.ItemHandsController)?.CurrentHandsOperation;
            if (!observation.Initialized || !ReferenceEquals(observation.Hands, hands))
            {
                observation.HandsSince = Time.time;
                observation.OperationSince = Time.time;
                observation.Dirty = true;
            }
            if (!observation.Initialized || !ReferenceEquals(observation.Operation, operation))
            {
                observation.OperationSince = Time.time;
                observation.Dirty = true;
            }
            observation.Hands = hands;
            observation.Operation = operation;
            observation.Dirty |= !observation.Initialized || observation.Ghost != ghost
                || observation.StandBy != bot.StandBy?.StandByType || observation.State != bot.BotState
                || observation.BodyActive != bot.gameObject.activeSelf || observation.Decision != bot.Brain?.LastDecision;
            observation.Ghost = ghost;
            observation.StandBy = bot.StandBy?.StandByType;
            observation.State = bot.BotState;
            observation.BodyActive = bot.gameObject.activeSelf;
            observation.Decision = bot.Brain?.LastDecision;
            observation.Initialized = true;
            if (Time.time < observation.NextReport
                || (!observation.Dirty && Time.time - observation.LastReport < StableInterval)) return;
            observation.NextReport = Time.time + ChangeInterval;
            observation.LastReport = Time.time;
            observation.Dirty = false;

            Log.Info($"NATIVE MEDICAL: {bot.Profile?.Nickname} [{bot.ProfileId}] role={bot.Profile?.Info?.Settings?.Role}"
                + $" ghost={ghost} state={bot.BotState} bodyActive={bot.gameObject.activeSelf}"
                + $" leader={bot.BotFollower?.BossToFollow?.Player()?.ProfileId ?? "none"} group={bot.BotsGroup?.MembersCount ?? 1}"
                + $" using={Flag(observation.Medicine.Value)} usingObservedFor={Number(observation.Medicine.ObservedFor)}s"
                + $" firstAid={Flag(observation.FirstAid.Value)} firstAidObservedFor={Number(observation.FirstAid.ObservedFor)}s"
                + $" surgery={Flag(observation.Surgery.Value)} surgeryObservedFor={Number(observation.Surgery.ObservedFor)}s"
                + $" stimulator={Flag(observation.Stimulator.Value)} stimulatorObservedFor={Number(observation.Stimulator.ObservedFor)}s"
                + $" firstAidItem={ItemLabel(medicine?.FirstAid?.CurUsingMeds)} surgeryItem={ItemLabel(medicine?.SurgicalKit?.CurUsingMeds)}"
                + $" hands={hands?.GetType().FullName ?? "none"} handsObservedFor={Number(Time.time - observation.HandsSince)}s"
                + $" handsItem={ItemLabel(hands?.Item)} handsDestroyed={Flag(hands?.Destroyed)}"
                + $" operation={operation?.GetType().FullName ?? "none"} operationState={operation?.State.ToString() ?? "none"}"
                + $" operationObservedFor={Number(Time.time - observation.OperationSince)}s"
                + $" standby={bot.StandBy?.StandByType.ToString() ?? "unknown"} canStandby={Flag(bot.StandBy?.CanDoStandBy)}"
                + $" decision={bot.Brain?.LastDecision} enemy={bot.Memory?.GoalEnemy != null} underFire={bot.Memory?.IsUnderFire}"
                + $" lastRefusal={observation.LastRefusal ?? "none"} refusalAge={Number(observation.LastRefusal == null ? -1f : Time.time - observation.RefusedAt)}s"
                + $" position={bot.Position} " + NativeGhostPatrol.Snapshot(bot));
        }
        catch (Exception e)
        {
            if (Time.time < observation.NextErrorReport) return;
            observation.NextErrorReport = Time.time + StableInterval;
            observation.NextReport = Time.time + StableInterval;
            Log.Info($"NATIVE MEDICAL: [{bot.ProfileId}] snapshot-error={e.GetType().Name}");
        }
    }

    private static string Flag(bool? value) => value.HasValue ? value.Value ? "True" : "False" : "unknown";
    private static string Number(float value) => value.ToString("F1", CultureInfo.InvariantCulture);
    private static string ItemLabel(Item item) => item == null ? "none" : $"{item.TemplateId}/{item.Id}";
}
