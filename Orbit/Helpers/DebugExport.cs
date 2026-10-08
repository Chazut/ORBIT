using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;

namespace Orbit.Helpers;

internal static class DebugExport
{
    private static readonly DebugArchive Archive = new(BepInEx.Paths.BepInExRootPath, Plugin.ReleaseVersion);
    private static Task<string> _export;
    internal static bool Busy => _export != null;
    internal static string Status { get; private set; } = "Create a ZIP to attach to your bug report.";
    internal static string LastPath { get; private set; }
    internal static string Folder => Path.Combine(BepInEx.Paths.BepInExRootPath, "ORBIT", "debug");

    internal static void BeginRaid(string map) => Archive.BeginRaid(map, RaidReviewContext());
    internal static void RegisterDiagnostic(string path) => Archive.RegisterDiagnostic(path);
    internal static void FinishRaid(Task writers) => Archive.FinishRaid(writers, Log.DebugFlush());

    internal static void Start()
    {
        if (Busy) return;
        try
        {
            Status = "Creating debug ZIP...";
            _export = Archive.Create(DiagnosticCapture.PrepareExport(), Log.DebugFlush(), RaidReviewContext());
        }
        catch (Exception error) { Status = "Could not create debug ZIP: " + error.Message; Log.Error(Status); }
    }

    internal static void Refresh()
    {
        if (_export?.IsCompleted != true) return;
        try
        {
            LastPath = _export.GetAwaiter().GetResult();
            Status = "Saved: " + LastPath;
            Log.Always("DEBUG ZIP saved: " + LastPath);
        }
        catch (Exception error) { Status = "Could not create debug ZIP: " + error.Message; Log.Error(Status); }
        finally { _export = null; }
    }

    // Read optional plugin/game state on the main thread. The archive worker only
    // receives detached strings and never touches Unity objects or config entries.
    private static RaidReviewDebug.Context RaidReviewContext()
    {
        if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue("ekky.raidreview", out var plugin)) return null;
        try
        {
            var type = plugin.Instance?.GetType();
            var server = type?.GetField("RAID_REVIEW_HTTP_Server", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as string;
            var world = Comfort.Common.Singleton<EFT.GameWorld>.Instance;
            return new RaidReviewDebug.Context { Server = server,
                ProfileId = world?.CurrentProfileId?.ToString() ?? world?.MainPlayer?.ProfileId };
        }
        catch (Exception) { return new RaidReviewDebug.Context(); }
    }
}
