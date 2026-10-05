using System;
using System.Collections.Generic;
using EFT;
using EFT.EnvironmentEffect;
using EFT.Weather;
using Orbit.Helpers;
using Orbit.Sain;
using UnityEngine;

namespace Orbit.Systems;

// One instance per DormancySystem/raid. Sampling is independent of bot sleep and of
// player cameras; the BotGame curve also works on an authoritative headless host.
internal sealed class GhostVisibility
{
    private sealed class Observer
    {
        internal int Generation = -1;
        internal float Baseline, Time = 1, Weather = 1, Minimum, Acquisition = 1;
        internal Vector3 Position;
        internal bool Inside, Managed;
    }
    private readonly GhostWeatherBridge _bridge = new();
    private readonly Dictionary<BotOwner, Observer> _observers = new();
    private readonly List<BotOwner> _removed = new();
    private GhostWeatherBridge.Settings _settings;
    private float _nextSample, _nextPrune, _time = 1, _fog, _rain, _clouds, _managedWeather = 1;
    private bool _available, _managed, _reportedFailure;
    private int _generation;

    internal GhostVisibilityModel.Result Read(BotOwner observer, BotOwner target, float rawReach, float darkness, bool nightCapable)
    {
        if (observer == null) return new(0, 0);
        Refresh(observer);
        var data = Get(observer);
        var targetInside = target != null && Get(target).Inside;
        var coefficient = data.Managed || !data.Inside ? data.Weather : 1f;
        var gain = !data.Managed && data.Inside && targetInside ? 1f : data.Acquisition;
        return GhostVisibilityModel.Calculate(rawReach, darkness, nightCapable, data.Baseline,
            data.Time, coefficient, data.Minimum, gain);
    }

    private void Refresh(BotOwner bot)
    {
        if (Time.time < _nextSample) return;
        _nextSample = Time.time + 2f; _generation++;
        using var timing = TransitionPerformance.Measure(TransitionPhase.GhostWeather);
        _available = false; _fog = _rain = 0; _clouds = -1;
        try
        {
            // Effective game weather, including changes published by weather mods.
            var curve = WeatherController.Instance?.WeatherCurve ?? bot.BotsController?.BotGame?.WeatherCurve;
            if (curve != null && GhostWeatherBridge.Finite(curve.Fog) && GhostWeatherBridge.Finite(curve.Rain) && GhostWeatherBridge.Finite(curve.Cloudiness))
            { _available = true; _fog = curve.Fog; _rain = Mathf.Clamp01(curve.Rain); _clouds = curve.Cloudiness; }
            _managed = _bridge.TrySettings(out _settings, out _time);
            _managedWeather = _available && _managed ? GhostWeatherBridge.Coefficient(_settings, _fog, _rain, _clouds) : 1f;
            if (!GhostWeatherBridge.Finite(_managedWeather)) _managed = false;
        }
        catch (Exception e) { Report(e); _managed = false; }
        if (Time.time >= _nextPrune)
        {
            _nextPrune = Time.time + 30f; _removed.Clear();
            foreach (var pair in _observers) if (pair.Key == null || pair.Key.IsDead) _removed.Add(pair.Key);
            foreach (var dead in _removed) _observers.Remove(dead);
            if (PerformanceJournal.Enabled)
            {
                var detail = FormattableString.Invariant($"available={_available} fog={_fog:F4} rain={_rain:F2} clouds={_clouds:F2} managed={_managed} coefficient={_managedWeather:F3} observers={_observers.Count}");
                Log.Always("GHOST WEATHER: " + detail);
                PerformanceJournal.Event("ghost-weather", detail: detail);
            }
        }
    }

    private Observer Get(BotOwner bot)
    {
        if (!_observers.TryGetValue(bot, out var data)) _observers[bot] = data = new Observer();
        var position = bot.GetPlayer?.Position ?? bot.Position;
        if (data.Generation == _generation && (position - data.Position).sqrMagnitude < 16f) return data;
        using var timing = TransitionPerformance.Measure(TransitionPhase.GhostWeather);
        data.Generation = _generation; data.Position = position;
        data.Managed = false; data.Weather = data.Time = data.Acquisition = 1; data.Minimum = 0; data.Baseline = 0;
        try
        {
            // Trigger lookup uses the Ghost's current transform, not stale AIData.IsInside.
            // Indoor maps remain deterministic even without the scene environment manager.
            var map = bot.GetPlayer?.Location;
            var environment = EnvironmentManager.Instance;
            data.Inside = environment != null ? environment.GetEnvironmentByPos(position) == EnvironmentType.Indoor
                : map is "factory4_day" or "factory4_night" or "laboratory" or "labyrinth";
            data.Baseline = bot.Settings?.Current?.CurrentVisibleDistance ?? 0;
            if (!GhostWeatherBridge.Finite(data.Baseline) || data.Baseline < 0) data.Baseline = 0;
            data.Managed = _managed && _bridge.Applies(bot);
            if (data.Managed)
            {
                data.Time = Mathf.Clamp01(_time);
                data.Weather = Mathf.Clamp(_managedWeather, Mathf.Clamp01(_settings.MinimumCoefficient), 1f);
                data.Minimum = Mathf.Max(0, _settings.MinimumDistance);
                data.Acquisition = 1f / (2f - _managedWeather);
            }
            else if (bot.Settings?.FileSettings?.Look is { } look)
            {
                var curve = bot.BotsController?.BotGame?.WeatherCurve;
                var fogValue = curve != null && GhostWeatherBridge.Finite(curve.Fog) ? curve.Fog : _fog;
                var rain = curve != null && GhostWeatherBridge.Finite(curve.Rain) ? Mathf.Clamp01(curve.Rain) : _rain;
                var fog = Mathf.InverseLerp(.004f, .01f, fogValue);
                data.Weather = Mathf.Lerp(1, Mathf.Clamp01(look.RAIN_DEBUFF_MAXVISIBILITY_MULTIPLYER), rain)
                    * Mathf.Lerp(1, Mathf.Clamp01(look.FOG_DEBUFF_MAXVISIBILITY_MULTIPLYER), fog);
                data.Acquisition = Mathf.Lerp(1, Mathf.Clamp01(look.RAIN_DEBUFF_SEENCOEFF_MULTIPLYER), rain)
                    * Mathf.Lerp(1, Mathf.Clamp01(look.FOG_DEBUFF_SEENCOEFF_MULTIPLYER), fog);
                data.Minimum = Mathf.Max(0, look.MINIMUM_VISIBLE_DIST);
                var timeCurve = bot.LookSensor?._visionCurve;
                if (timeCurve != null && bot.GameDateTime != null)
                    data.Time = Mathf.Clamp01(timeCurve.Evaluate((float)bot.GameDateTime.Calculate().TimeOfDay.TotalHours));
            }
            if (!GhostWeatherBridge.Finite(data.Weather) || !GhostWeatherBridge.Finite(data.Time)
                || !GhostWeatherBridge.Finite(data.Minimum) || !GhostWeatherBridge.Finite(data.Acquisition))
                throw new InvalidOperationException("Non-finite vision settings");
        }
        catch (Exception e)
        {
            // Discard partial or invalid results so a broken optional setting cannot poison
            // comparisons for the rest of this sample. Retry on the next generation.
            data.Managed = false; data.Weather = data.Time = data.Acquisition = 1; data.Minimum = 0;
            Report(e);
        }
        return data;
    }

    private void Report(Exception error)
    {
        if (!_reportedFailure) Log.Warning($"GHOST WEATHER: weather or observer state unavailable; retaining clear-weather fallback ({error.GetType().Name})");
        _reportedFailure = true;
    }
}
