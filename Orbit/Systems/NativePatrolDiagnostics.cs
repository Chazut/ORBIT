using System;
using System.Collections.Generic;
using System.Reflection;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace Orbit.Systems;

/// <summary>Read-only patrol snapshots for native bots that have used Ghost movement this raid.</summary>
public static class NativePatrolDiagnostics
{
    private sealed class Observation
    {
        public Vector3 LastPosition;
        public float StillSince;
        public float NextReport;
        public bool ReportedError;
        public float NextArrivalReport, NextChoiceReport;
        public int ArrivalChecks, Arrivals, Choices, SameChoices, WayChanges;
        public object LastChoiceWay;
        public float NextFollowerArrivalReport;
        public int FollowerChecks, FollowerArrivals;
    }

    private const float StationarySeconds = 30f;
    private const float ReportInterval = 60f;
    private static readonly Dictionary<BotOwner, Observation> Observations = new();
    private static readonly FieldInfo ComeTime = AccessTools.Field(typeof(PatrollingData), "_comeTime");
    private static readonly FieldInfo ReserveChosenTime = AccessTools.Field(typeof(PatrollingData), "_reservChoosedTime");
    private static readonly FieldInfo NextChangeWay = AccessTools.Field(typeof(PatrolPointChooserBasic), "_nextChangeWay");
    private static Type _huntType;
    private static readonly Dictionary<string, FieldInfo> HuntFields = new();

    public static void Clear() => Observations.Clear();

    public static void Forget(BotOwner bot)
    {
        if (!ReferenceEquals(bot, null)) Observations.Remove(bot);
    }

    public static void BeforeSleep(BotOwner bot)
    {
        if (!Observations.TryGetValue(bot, out var observation))
        {
            observation = new Observation { LastPosition = bot.Position, StillSince = Time.time };
            Observations.Add(bot, observation);
        }
        UpdatePosition(bot, observation);
        Snapshot(bot, observation, "before-sleep", ghost: false, fight: false);
    }

    public static void AfterWake(BotOwner bot)
    {
        if (!Observations.TryGetValue(bot, out var observation)) return;
        UpdatePosition(bot, observation);
        Snapshot(bot, observation, "after-wake", ghost: false, fight: false);
    }

    // Called by the existing vanilla poll, including while the body stays awake near other bots.
    public static void Observe(BotOwner bot, bool ghost, bool fight)
    {
        if (!Observations.TryGetValue(bot, out var observation)) return;
        if (bot == null || bot.IsDead) { Forget(bot); return; }
        UpdatePosition(bot, observation);
        if (Time.time - observation.StillSince < StationarySeconds || Time.time < observation.NextReport) return;
        Snapshot(bot, observation, "stationary", ghost, fight);
    }

    private static void UpdatePosition(BotOwner bot, Observation observation)
    {
        // Ignore tiny position corrections and breathing. No movement or brain method is called here.
        if ((bot.Position - observation.LastPosition).sqrMagnitude <= 1f) return;
        observation.LastPosition = bot.Position;
        observation.StillSince = Time.time;
    }

    private static bool TrackGlukhar(BotOwner bot, out Observation observation)
    {
        observation = null;
        return bot != null && bot.Profile?.Info?.Settings?.Role == WildSpawnType.bossGluhar
            && Observations.TryGetValue(bot, out observation);
    }

    internal static void ArrivalChecked(BotOwner bot, bool arrived)
    {
        if (!TrackGlukhar(bot, out var observation)) return;
        observation.ArrivalChecks++;
        if (arrived) observation.Arrivals++;
        if (Time.time < observation.NextArrivalReport) return;
        observation.NextArrivalReport = Time.time + ReportInterval;
        try
        {
            var patrol = bot.PatrollingData;
            var move = bot.Settings.FileSettings.Move;
            var offset = patrol.CurTargetPoint - bot.Position;
            var distance3D = offset.magnitude;
            if (Mathf.Abs(offset.y) <= move.Y_APPROXIMATION) offset.y = 0f;
            Log.Info($"NATIVE PATROL ARRIVAL: {bot.Profile.Nickname} [{bot.ProfileId}] ghost={!bot.gameObject.activeSelf} arrived={arrived}"
                + $" checks={observation.ArrivalChecks} arrivals={observation.Arrivals} distance={offset.magnitude:F3}m distance3D={distance3D:F3}m reach={move.REACH_DIST:F3}m"
                + $" patrol={patrol.Status} point={patrol.CurPatrolPoint?.TargetPoint?.Id} target={patrol.CurTargetPoint} way={patrol.Way?.name}");
            observation.ArrivalChecks = observation.Arrivals = 0;
        }
        catch (Exception e) { DiagnosticError(bot, observation, e); }
    }

    internal static void PointChosen(BotOwner bot, PatrolPointContainer chosen)
    {
        if (!TrackGlukhar(bot, out var observation)) return;
        try
        {
            var patrol = bot.PatrollingData;
            var current = patrol.CurPatrolPoint?.TargetPoint;
            var next = chosen?.TargetPoint;
            var same = next != null && ReferenceEquals(current, next);
            observation.Choices++;
            if (same) observation.SameChoices++;
            if (observation.LastChoiceWay != null && !ReferenceEquals(observation.LastChoiceWay, patrol.Way)) observation.WayChanges++;
            observation.LastChoiceWay = patrol.Way;
            if (Time.time < observation.NextChoiceReport) return;
            observation.NextChoiceReport = Time.time + ReportInterval;
            Log.Info($"NATIVE PATROL CHOICE: {bot.Profile.Nickname} [{bot.ProfileId}] ghost={!bot.gameObject.activeSelf} samePoint={same}"
                + $" choices={observation.Choices} sameChoices={observation.SameChoices} wayChanges={observation.WayChanges}"
                + $" current={current?.Id}:{current?.name} chosen={next?.Id}:{next?.name} way={patrol.Way?.name} patrol={patrol.Status}");
            observation.Choices = observation.SameChoices = observation.WayChanges = 0;
        }
        catch (Exception e) { DiagnosticError(bot, observation, e); }
    }

    internal static void FollowerArrivalChecked(BotOwner bot, Vector3? target, bool extraTarget, float distance, bool arrived)
    {
        if (bot == null || bot.BotFollower?.BossToFollow == null
            || !Observations.TryGetValue(bot, out var observation)) return;
        observation.FollowerChecks++;
        if (arrived) observation.FollowerArrivals++;
        if (Time.time < observation.NextFollowerArrivalReport) return;
        observation.NextFollowerArrivalReport = Time.time + ReportInterval;
        try
        {
            var leader = bot.BotFollower.BossToFollow;
            Log.Info($"NATIVE FOLLOWER ARRIVAL: {bot.Profile.Nickname} [{bot.ProfileId}] ghost={!bot.gameObject.activeSelf} arrived={arrived}"
                + $" checks={observation.FollowerChecks} arrivals={observation.FollowerArrivals} target={target} extraTarget={extraTarget} distance={distance:F3}m"
                + $" reach={(extraTarget ? 1f : bot.Settings.FileSettings.Move.REACH_DIST):F3}m leader={leader.Player()?.Profile?.Nickname}"
                + $" leaderDistance={Vector3.Distance(bot.Position, leader.Position):F1}m path={bot.Mover.ActualPathController.HavePath}");
            observation.FollowerChecks = observation.FollowerArrivals = 0;
        }
        catch (Exception e) { DiagnosticError(bot, observation, e); }
    }

    private static void DiagnosticError(BotOwner bot, Observation observation, Exception e)
    {
        if (observation.ReportedError) return;
        observation.ReportedError = true;
        Log.Warning($"NATIVE PATROL: snapshot unavailable [{bot.ProfileId}] ({e.GetType().Name}: {e.Message})");
    }

    private static void Snapshot(BotOwner bot, Observation observation, string reason, bool ghost, bool fight)
    {
        observation.NextReport = Time.time + ReportInterval;
        try
        {
            var patrol = bot.PatrollingData;
            var point = patrol?.PointControl == null ? null : patrol.CurPatrolPoint?.TargetPoint;
            var way = patrol?.PointControl == null ? null : patrol.Way;
            var mover = bot.Mover;
            var route = mover?.ActualPathController;
            var target = point == null ? "none" : $"{patrol.CurTargetPoint} distance={Vector3.Distance(bot.Position, patrol.CurTargetPoint):F1}m";
            var settings = bot.Settings?.FileSettings?.Patrol;
            var sinceCome = ReadFloat(ComeTime, patrol);
            var reserveAt = ReadFloat(ReserveChosenTime, patrol);
            var changeAt = ReadFloat(NextChangeWay, patrol?.PointChooser);
            var stayFor = sinceCome.HasValue ? $"{Time.time - sinceCome.Value:F1}s" : "unknown";
            var nextPointIn = sinceCome.HasValue && settings != null
                ? $"{sinceCome.Value + settings.GO_TO_NEXT_POINT_DELTA - Time.time:F1}s" : "unknown";
            var changeWayIn = changeAt.HasValue ? $"{changeAt.Value - Time.time:F1}s" : "unknown";
            var reserveNextIn = sinceCome.HasValue && reserveAt.HasValue && settings != null
                ? $"{Mathf.Max(sinceCome.Value, reserveAt.Value) + settings.GO_TO_NEXT_POINT_DELTA_RESERV_WAY - Time.time:F1}s" : "unknown";
            // Reading HavePath/CurPath is safe. CheckShouldMove would consume the route, so never use it.
            var path = route?.CurPath;
            Log.Info($"NATIVE PATROL: {bot.Profile?.Nickname} [{bot.ProfileId}] reason={reason} ghost={ghost} bodyActive={bot.gameObject.activeSelf} stillFor={Time.time - observation.StillSince:F1}s"
                + $" decision={bot.Brain?.LastDecision} patrol={patrol?.Status} target={target} point={point?.Id}:{point?.name} pointType={point?.PatrolPointType}"
                + $" way={way?.name}:{way?.PatrolType} action={point?.ActionData?.GetType().Name ?? "none"} chooser={patrol?.PointChooser?.GetType().Name ?? "none"}"
                + $" sinceStay={stayFor} bossNextIn={nextPointIn} reserveNextIn={reserveNextIn} changeWayIn={changeWayIn}"
                + $" path={route?.HavePath} corner={path?.CurIndex}/{path?.Length} moverTarget={mover?.TargetPoint} moving={mover?.IsMoving}"
                + $" paused={mover?.Pause} pauseLeft={mover?.RemainPause:F1}s speed={mover?.DestMoveSpeed:F2} standbyAllowed={bot.StandBy?.CanDoStandBy} enemy={bot.Memory?.GoalEnemy != null} fight={fight}"
                + " " + NativeGhostPatrol.Snapshot(bot) + HuntSnapshot(bot));
        }
        catch (Exception e)
        {
            if (observation.ReportedError) return;
            observation.ReportedError = true;
            Log.Warning($"NATIVE PATROL: snapshot unavailable [{bot.ProfileId}] ({e.GetType().Name}: {e.Message})");
        }
    }

    private static float? ReadFloat(FieldInfo field, object instance)
        => instance != null && field?.GetValue(instance) is float value ? value : null;

    private static string HuntSnapshot(BotOwner bot)
    {
        _huntType ??= AccessTools.TypeByName("MoreBotsAPI.Components.BotHuntManager");
        var hunt = _huntType == null ? null : bot.GetComponent(_huntType);
        if (hunt == null) return "";
        object Read(string name)
        {
            if (!HuntFields.TryGetValue(name, out var field))
                HuntFields[name] = field = AccessTools.Field(_huntType, name);
            return field?.GetValue(hunt) ?? "unknown";
        }
        var leader = bot.BotFollower?.BossToFollow;
        var distance = leader == null ? "none" : $"{Vector3.Distance(bot.Position, leader.Position):F1}m";
        return $" huntActive={Read("active")} regroup={Read("shouldRegroup")} regrouping={Read("isRegrouping")} regroupPoint={Read("regroupPoint")} regroupDirty={Read("regroupPointDirty")}"
            + $" boss={bot.Boss?.IamBoss} leader={leader?.Player()?.Profile?.Nickname ?? "none"} leaderDistance={distance}";
    }
}
