using System;
using EFT;
using UnityEngine;

namespace Orbit.Systems;

public sealed partial class NativeGhostSystem
{
    // Scoped to one continuous sleep. Only our own attrition and regeneration may lower/raise
    // this baseline. An unexplained loss permanently revokes the exception until the next sleep.
    private sealed class SimulatedWounds
    {
        public bool Known, Invalid;
        public float ExpectedHp;
        public float NextRefresh, NextReport;

        public bool Observe(float hp)
        {
            if (!Finite(hp) || hp <= 0f || Known && hp < ExpectedHp - 1f) Invalid = true;
            return Known && !Invalid;
        }

        public void RecordWound(float before, float after, float maximum)
        {
            if (Invalid) return;
            if (Known) Observe(before);
            else if (!Finite(maximum) || maximum <= 0f || before < maximum - 1f) Invalid = true;
            if (Invalid || !Finite(before) || !Finite(after) || after <= 0f || after >= before) return;
            Known = true;
            ExpectedHp = after;
        }

        public void RecordHealing(float before, float after)
        {
            if (!Observe(before) || !Finite(after) || after < before) return;
            ExpectedHp = after;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }

    internal void RecordSimulatedWound(BotOwner bot, float before, float after)
    {
        if (!Sleepers.TryGetValue(bot, out var state) || !RetainsNativeState(bot)) return;
        state.Wounds.RecordWound(before, after,
            bot.GetPlayer.HealthController.GetBodyPartHealth(EBodyPart.Common, true).Maximum);
    }

    internal void RecordSimulatedHealing(BotOwner bot, float before, float after)
    {
        if (Sleepers.TryGetValue(bot, out var state)) state.Wounds.RecordHealing(before, after);
    }

    // Some native layers call FirstAid from GetDecision, before the returned action is guarded.
    internal static bool RetainSimulatedFirstAid(BotOwner bot)
        => bot != null && Sleepers.TryGetValue(bot, out var state) && RetainSimulatedHealing(state);

    private static readonly EBodyPart[] HealingParts =
        { EBodyPart.Head, EBodyPart.Chest, EBodyPart.Stomach, EBodyPart.LeftArm, EBodyPart.RightArm, EBodyPart.LeftLeg, EBodyPart.RightLeg };

    private static bool RetainSimulatedHealing(Sleeper state)
    {
        var bot = state.Bot;
        if (!state.Wounds.Known || state.Wounds.Invalid || state.WakeReason != null
            || !RetainsNativeState(bot) || bot.Memory == null || bot.Memory.GoalEnemy != null || bot.Memory.IsUnderFire
            || state.HumanDistanceSqr == null || state.HumanWakeDistanceSqr <= 0f || NeedsBody(bot)) return false;
        try
        {
            if (!(state.HumanDistanceSqr(bot.Position) > state.HumanWakeDistanceSqr)) return false;
            var health = bot.GetPlayer.HealthController;
            var hp = health.GetBodyPartHealth(EBodyPart.Common, true).Current;
            if (!state.Wounds.Observe(hp)) return false;
            var medicine = bot.Medecine;
            if (medicine?.FirstAid == null || medicine.SurgicalKit == null || medicine.Stimulators == null
                || medicine.FirstAid.Using || medicine.SurgicalKit.Using || medicine.Stimulators.Using
                || medicine.SurgicalKit.HaveWork) return false;
            for (var i = 0; i < HealingParts.Length; i++)
                if (health.GetBodyPartHealth(HealingParts[i]).Current <= 0f) return false;

            if (Time.time >= state.Wounds.NextRefresh)
            {
                state.Wounds.NextRefresh = Time.time + 1f;
                // Refresh the engine's damaged-part/bleeding flags after gradual Ghost healing.
                // This starts no item operation and lets the native layer finish its heal decision.
                medicine.FirstAid.CheckParts();
            }
            if (medicine.FirstAid.IsBleeding || NeedsBody(bot)) return false;

            // Never start the physical heal node or invoke a medicine animation on an inactive body.
            // DormancySystem remains the only owner of the existing delay, rate and HP baselines.
            if (state.Hearing == null)
            {
                CancelMoveOrder(bot.Mover);
                bot.Mover.ActualPathController.Stop();
                bot.Mover.IsMoving = false;
                state.Decision = "heal";
            }
            if (Time.time >= state.Wounds.NextReport)
            {
                state.Wounds.NextReport = Time.time + 30f;
                Log.Info($"NATIVE GHOST HEAL: {bot.Profile.Nickname} simulated wound care retained hp={hp:F1} expected={state.Wounds.ExpectedHp:F1}");
            }
            return true;
        }
        catch (Exception e)
        {
            RequestWake(state, $"simulated wound care failed: {e.GetType().Name}");
            return false;
        }
    }
}
