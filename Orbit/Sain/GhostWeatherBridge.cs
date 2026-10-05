using System;
using System.Linq.Expressions;
using EFT;
using UnityEngine;

namespace Orbit.Sain;

// Optional, exact-type bindings compiled while the raid loads. No assembly-wide scans
// or per-pair reflection, and no calls to a sleeping bot's vision update.
internal sealed class GhostWeatherBridge
{
    internal sealed class Settings
    {
        public float Fog = 0, MinimumCoefficient = 0, MinimumDistance = 0;
        public float SprinkleThreshold = 0, Sprinkle = 0, LightThreshold = 0, Light = 0, NormalThreshold = 0, Normal = 0, HeavyThreshold = 0, Heavy = 0, Downpour = 0;
        public float ClearThreshold = 0, CloudyThreshold = 0, Cloudy = 0, Overcast = 0;
    }
    private readonly Func<BotOwner, bool> _excluded;
    private readonly Func<Settings> _settings;
    private readonly Func<float> _time;
    private bool _failureReported;

    internal GhostWeatherBridge()
    {
        try
        {
            var enable = Type.GetType("SAIN.SAINEnableClass, SAIN");
            var globals = Type.GetType("SAIN.Preset.Shared.GlobalSettings.GlobalSettingsClass, SAIN.Preset.Shared");
            var manager = Type.GetType("SAIN.Components.BotManagerComponent, SAIN");
            if (enable == null || globals == null || manager == null) return;
            var bot = Expression.Parameter(typeof(BotOwner));
            _excluded = Expression.Lambda<Func<BotOwner, bool>>(
                Expression.Call(enable.GetMethod("IsBotExcluded", new[] { typeof(BotOwner) }), bot), bot).Compile();
            var source = Expression.PropertyOrField(Expression.PropertyOrField(Expression.Field(null, globals.GetField("Instance")), "Look"), "Time");
            var fields = new[] {
                ("Fog", "FOG_MAXCOEF"), ("MinimumCoefficient", "MIN_COEF"), ("MinimumDistance", "MIN_DIST_METERS"),
                ("SprinkleThreshold", "RAIN_SRINKLE_THRESH"), ("Sprinkle", "RAIN_SRINKLE_COEF"),
                ("LightThreshold", "RAIN_LIGHT_THRESH"), ("Light", "RAIN_LIGHT_COEF"),
                ("NormalThreshold", "RAIN_NORMAL_THRESH"), ("Normal", "RAIN_NORMAL_COEF"),
                ("HeavyThreshold", "RAIN_HEAVY_THRESH"), ("Heavy", "RAIN_HEAVY_COEF"), ("Downpour", "RAIN_DOWNPOUR_COEF"),
                ("ClearThreshold", "NOCLOUDS_THRESH"), ("CloudyThreshold", "CLOUDY_THRESH"),
                ("Cloudy", "CLOUDY_COEF"), ("Overcast", "OVERCAST_COEF") };
            var bindings = new MemberBinding[fields.Length];
            for (var i = 0; i < fields.Length; i++)
                bindings[i] = Expression.Bind(typeof(Settings).GetField(fields[i].Item1), Expression.PropertyOrField(source, "VISION_WEATHER_" + fields[i].Item2));
            Expression value = Expression.MemberInit(Expression.New(typeof(Settings)), bindings);
            for (Expression current = source; current is MemberExpression member; current = member.Expression)
                if (!current.Type.IsValueType)
                    value = Expression.Condition(Expression.Equal(current, Expression.Constant(null, current.Type)), Expression.Constant(null, typeof(Settings)), value);
            _settings = Expression.Lambda<Func<Settings>>(value).Compile();
            var clock = Expression.PropertyOrField(Expression.Property(null, manager, "Instance"), "TimeVision");
            value = Expression.PropertyOrField(clock, "TimeVisionDistanceModifier");
            for (Expression current = clock; current is MemberExpression member; current = member.Expression)
                if (!current.Type.IsValueType)
                    value = Expression.Condition(Expression.Equal(current, Expression.Constant(null, current.Type)), Expression.Constant(1f), value);
            _time = Expression.Lambda<Func<float>>(value).Compile();
        }
        catch (Exception e) { Report(e); }
    }

    internal bool TrySettings(out Settings settings, out float time)
    {
        settings = null; time = 1f;
        if (_settings == null || _excluded == null || _time == null) return false;
        try { settings = _settings(); time = _time(); return settings != null && Finite(time)
            && Finite(settings.MinimumDistance) && Finite(settings.MinimumCoefficient); }
        catch (Exception e) { Report(e); return false; }
    }
    internal bool Applies(BotOwner bot)
    {
        if (_excluded == null) return false;
        try { return !_excluded(bot); }
        catch (Exception e) { Report(e); return false; }
    }
    private void Report(Exception error)
    {
        if (!_failureReported) Log.Warning($"GHOST WEATHER: optional vision binding unavailable; using native weather settings ({error.GetType().Name})");
        _failureReported = true;
    }
    internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    internal static float Coefficient(Settings s, float fog, float rain, float clouds)
    {
        var fogK = Mathf.Lerp(1f, Mathf.Clamp01(s.Fog), Mathf.Clamp01(fog / .018f));
        var rainValue = rain <= s.SprinkleThreshold ? s.Sprinkle : rain < s.LightThreshold ? s.Light
            : rain < s.NormalThreshold ? s.Normal : rain < s.HeavyThreshold ? s.Heavy : s.Downpour;
        var rainK = Mathf.Lerp(1f, Mathf.Clamp01(rainValue), rain);
        var cloudValue = Mathf.Clamp01((clouds + 1f) / 2f);
        var cloudK = cloudValue <= s.ClearThreshold ? 1f
            : Mathf.Lerp(1f, Mathf.Clamp01(cloudValue <= s.CloudyThreshold ? s.Cloudy : s.Overcast), cloudValue);
        return Mathf.Clamp(fogK * rainK * cloudK, .01f, 1f);
    }
}
