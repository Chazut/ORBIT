using BepInEx.Bootstrap;

namespace Orbit.Helpers;

/// <summary>
/// One-shot detection of Project Fika (and its headless variant) via BepInEx plugin GUIDs. Gates the
/// door-interaction network replication in MovementSystem — host-side door state writes are invisible to
/// Fika clients.
/// </summary>
public static class FikaDetection
{
    private const string FikaGuid = "com.fika.core";
    private const string FikaHeadlessGuid = "com.fika.headless";

    private static bool _checked;
    private static bool _fika;
    private static bool _headless;

    public static bool FikaLoaded
    {
        get { Ensure(); return _fika; }
    }

    public static bool FikaHeadlessLoaded
    {
        get { Ensure(); return _headless; }
    }

    private static void Ensure()
    {
        if (_checked) return;
        _checked = true;
        _fika = Chainloader.PluginInfos.ContainsKey(FikaGuid);
        _headless = Chainloader.PluginInfos.ContainsKey(FikaHeadlessGuid);
        if (_fika)
            Log.Always($"Project Fika detected{(_headless ? " (headless)" : "")} — bot door opens will be replicated to clients");
    }
}
