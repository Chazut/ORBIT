#nullable disable
using System;
using System.IO;
using System.Reflection;

namespace Orbit;

/// <summary>
/// Keeps optional Fika types out of the main plugin's metadata and startup path.
/// BepInEx scans only the main plugin; the embedded transport is loaded after Fika.
/// </summary>
internal sealed class EmbeddedFika : IDisposable
{
    private bool started;
    private object bridge;
    private Action tick;
    private Action release;
    private Action<string> error;

    internal static object Load(Assembly owner, string resource, string entryPoint)
    {
        using (var stream = owner.GetManifestResourceStream(resource))
        {
            if (stream == null) throw new InvalidOperationException("Missing embedded Fika integration: " + resource);
            using (var bytes = new MemoryStream())
            {
                stream.CopyTo(bytes);
                var assembly = Assembly.Load(bytes.ToArray());
                return Activator.CreateInstance(assembly.GetType(entryPoint, true));
            }
        }
    }

    internal void Start(bool fikaPresent, bool legacyPresent, Func<object> factory,
        object logger, Action<string> info, Action<string> reportError)
    {
        if (started) return;
        started = true;
        error = reportError;
        if (!fikaPresent)
        {
            info("Solo: embedded Fika integration stays unloaded.");
            return;
        }
        if (legacyPresent)
        {
            info("Legacy Fika addon detected: using it for this launch. Remove the old addon DLL to use the built-in integration.");
            return;
        }
        try
        {
            bridge = factory();
            var type = bridge.GetType();
            release = (Action)Delegate.CreateDelegate(typeof(Action), bridge, type.GetMethod("Dispose"));
            tick = (Action)Delegate.CreateDelegate(typeof(Action), bridge, type.GetMethod("Update"));
            type.GetMethod("Initialize").Invoke(bridge, new[] { logger });
            info("Fika detected: embedded integration activated.");
        }
        catch (Exception ex)
        {
            var cause = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
            try { reportError("Embedded Fika integration failed to initialize: " + cause); }
            finally { Dispose(); }
        }
    }

    internal void Update()
    {
        try { tick?.Invoke(); }
        catch (Exception ex)
        {
            try { error?.Invoke("Embedded Fika integration stopped after an update error: " + ex); }
            finally { Dispose(); }
        }
    }

    public void Dispose()
    {
        var cleanup = release;
        tick = null;
        release = null;
        bridge = null;
        try { cleanup?.Invoke(); }
        catch (Exception ex) { error?.Invoke("Embedded Fika cleanup failed: " + ex); }
    }
}
