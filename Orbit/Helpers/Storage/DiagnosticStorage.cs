#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Configuration;

namespace RaidDiagnostics;

// Each machine owns its logs, so these settings belong to F12, not the server preset.
internal sealed class DiagnosticStorage : IDisposable
{
    private readonly ConfigEntry<bool> enabled;
    private readonly ConfigEntry<int> keep, gigabytes;
    private readonly string root, mod;
    private readonly Action<string> info, warning;
    private Task<RaidLogRetention.Report>? worker;
    private CancellationTokenSource? cancel;
    private bool pending = true, wasActive, disposed;
    private float next;
    internal DiagnosticStorage(ConfigFile config, string root, string mod, Action<string> info, Action<string> warning)
    {
        this.root = root; this.mod = mod; this.info = info; this.warning = warning;
        const string section = "Diagnostics storage";
        enabled = config.Bind(section, "Automatic cleanup", true,
            "Archive old diagnostic raids in verified ZIPs while outside raids. Quiet logging and replay telemetry are unchanged. Put comparison data in a protected subfolder to exclude it.");
        keep = config.Bind(section, "Recent raids to keep", 20,
            new ConfigDescription("Keep these most recent raids complete. Their files are never removed to meet the storage limit.", new AcceptableValueRange<int>(1, 500)));
        gigabytes = config.Bind(section, "Storage limit GB", 5,
            new ConfigDescription("Maximum diagnostic storage per mod (1 GB = 1,000,000,000 bytes). Old verified archives are removed first. Recent raids, protected data and .keep-marked files take priority. No age limit.", new AcceptableValueRange<int>(1, 1000)));
        enabled.SettingChanged += Changed; keep.SettingChanged += Changed; gigabytes.SettingChanged += Changed;
    }
    private void Changed(object? sender, EventArgs args) { pending = true; cancel?.Cancel(); }
    internal void Tick(bool active, float now)
    {
        if (disposed) return;
        if (worker?.IsCompleted == true)
        {
            try
            {
                if (!worker.IsCanceled)
                {
                    var report = worker.GetAwaiter().GetResult();
                    info($"Diagnostic storage: raids={report.Raids}, kept={report.KeptRaids}, archived={report.ArchivedRaids}, purged={report.PurgedArchives}, bytes={report.BeforeBytes}->{report.AfterBytes}, limit={report.LimitBytes}.");
                    if (report.OverBudget) warning("Diagnostic storage remains above the limit: recent raids and protected/unrecognized files were kept.");
                    for (int i = 0; i < Math.Min(5, report.Warnings.Count); i++) warning(report.Warnings[i]);
                    if (report.Warnings.Count > 5) warning($"Diagnostic storage: {report.Warnings.Count - 5} additional files/groups retained.");
                }
            }
            catch (Exception e) { warning("Diagnostic storage: " + e.GetBaseException().Message); }
            finally { worker = null; cancel?.Dispose(); cancel = null; }
        }
        if (active) { pending = true; cancel?.Cancel(); next = now + 15; }
        if (wasActive && !active) next = now + 15; // Let raid writers/export snapshots finish.
        wasActive = active;
        if (active || !pending || !enabled.Value || worker != null || now < next) return;
        pending = false;
        var settings = new RaidLogRetention.Options { KeepRaids = keep.Value, MaxBytes = gigabytes.Value * 1_000_000_000L };
        cancel = new CancellationTokenSource(); var token = cancel.Token;
        worker = Task.Run(() => new RaidLogRetention(root, mod, settings).Run(token), token);
    }
    public void Dispose()
    {
        disposed = true; cancel?.Cancel();
        enabled.SettingChanged -= Changed; keep.SettingChanged -= Changed; gigabytes.SettingChanged -= Changed;
        // Cancellation is checked between compression blocks. Never wait on the Unity thread.
    }
}
