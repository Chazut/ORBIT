using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Orbit.Helpers;

// File work runs on one background queue. Records are detached on the caller thread;
// no Unity object, config entry or live diagnostic collection is read by the worker.
internal sealed class DebugArchive
{
    internal sealed class RaidRecord
    {
        public string Id, Map, Version;
        public DateTime StartedUtc;
        public DateTime? EndedUtc;
        public string[] Diagnostics = Array.Empty<string>();
    }

    private readonly string _root, _folder, _recordPath, _version;
    private readonly List<string> _diagnostics = new();
    private RaidRecord _current;
    private Task _queue = Task.CompletedTask;
    private string _backgroundError;

    internal DebugArchive(string bepinexRoot, string version)
    {
        _root = bepinexRoot; _version = version;
        _folder = Path.Combine(_root, "ORBIT", "debug");
        _recordPath = Path.Combine(_folder, "last-raid.json");
    }

    internal void BeginRaid(string map)
    {
        _diagnostics.Clear();
        _current = new RaidRecord { Id = Guid.NewGuid().ToString("N"), Map = map,
            Version = _version, StartedUtc = DateTime.UtcNow };
        Checkpoint();
    }

    internal void RegisterDiagnostic(string path)
    {
        if (_current == null) return;
        var name = Path.GetFileName(path);
        if (_diagnostics.Contains(name)) return;
        _diagnostics.Add(name);
        Checkpoint();
    }

    private RaidRecord Snapshot() => _current == null ? null : new RaidRecord {
        Id = _current.Id, Map = _current.Map, Version = _current.Version,
        StartedUtc = _current.StartedUtc, EndedUtc = _current.EndedUtc,
        Diagnostics = _diagnostics.ToArray() };

    private void Checkpoint()
    {
        var record = Snapshot();
        Queue(async () => { await Task.CompletedTask; Persist(record); });
    }

    internal void FinishRaid(Task diagnosticsReady, Func<string> flushLogs)
    {
        if (_current == null || _current.EndedUtc.HasValue) return;
        _current.EndedUtc = DateTime.UtcNow;
        var record = Snapshot();
        Queue(async () => {
            await diagnosticsReady.ConfigureAwait(false);
            flushLogs();
            SaveLog(record);
            Persist(record);
        });
    }

    // Capture the record before dispatch so a new raid cannot change an in-progress export.
    internal Task<string> Create(Task diagnosticsReady, Func<string> flushLogs)
    {
        var record = Snapshot();
        var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue(async () => {
            try
            {
                await diagnosticsReady.ConfigureAwait(false);
                var warnings = new List<string>();
                var flushWarning = flushLogs();
                if (flushWarning != null) warnings.Add(flushWarning);
                if (_backgroundError != null) warnings.Add(_backgroundError);
                var sameSession = record != null;
                if (record == null && File.Exists(_recordPath))
                {
                    try { record = JsonConvert.DeserializeObject<RaidRecord>(File.ReadAllText(_recordPath)); }
                    catch (Exception error) { warnings.Add("Last raid metadata unavailable: " + error.Message); }
                }
                if (record != null && !SafeId(record.Id))
                    throw new InvalidDataException("Invalid last raid identifier.");
                if (sameSession)
                {
                    try { SaveLog(record); }
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                    { warnings.Add("Could not refresh the raid log snapshot: " + error.Message); }
                    Persist(record);
                }
                result.SetResult(WriteZip(record, warnings));
            }
            catch (Exception error) { result.SetException(error); }
        });
        return result.Task;
    }

    private void Queue(Func<Task> work)
    {
        var previous = _queue;
        _queue = Task.Run(async () => {
            await previous.ConfigureAwait(false);
            try { await work().ConfigureAwait(false); }
            catch (Exception error) { _backgroundError = "Raid debug snapshot failed: " + error.Message; }
        });
    }

    private static bool SafeId(string id) => Guid.TryParseExact(id, "N", out _);
    private string LogPath(RaidRecord record) => Path.Combine(_folder, "last-raid", record.Id + ".log");

    private void Persist(RaidRecord record)
    {
        Directory.CreateDirectory(_folder);
        var temporary = _recordPath + ".tmp";
        File.WriteAllText(temporary, JsonConvert.SerializeObject(record, Formatting.Indented));
        if (File.Exists(_recordPath)) File.Replace(temporary, _recordPath, null);
        else File.Move(temporary, _recordPath);
    }

    private void SaveLog(RaidRecord record)
    {
        var source = Path.Combine(_root, "LogOutput.log");
        if (!File.Exists(source)) return;
        var destination = LogPath(record);
        Directory.CreateDirectory(Path.GetDirectoryName(destination));
        var temporary = destination + ".tmp";
        using (var input = OpenShared(source))
        using (var output = File.Create(temporary)) CopySnapshot(input, output);
        if (File.Exists(destination)) File.Replace(temporary, destination, null);
        else File.Move(temporary, destination);
        // Retain one session log, not an ever-growing duplicate of every raid.
        foreach (var old in Directory.GetFiles(Path.GetDirectoryName(destination), "*.log"))
            if (!string.Equals(old, destination, StringComparison.OrdinalIgnoreCase)) File.Delete(old);
    }

    private string WriteZip(RaidRecord record, List<string> warnings)
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "ORBIT-debug-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff")
            + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".zip");
        var temporary = path + ".tmp";
        try
        {
            using (var file = File.Create(temporary))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                var log = record == null ? Path.Combine(_root, "LogOutput.log") : LogPath(record);
                var logIncluded = AddFile(zip, log, "BepInEx/LogOutput.log", warnings);
                if (record == null) warnings.Add("No recorded raid metadata. Only the current BepInEx session log is available.");
                if (record != null && !logIncluded)
                {
                    warnings.Add("The last raid log was not preserved (for example, the client stopped before raid teardown).");
                    AddFile(zip, Path.Combine(_root, "LogOutput.log"), "BepInEx/current-session-LogOutput.log", warnings);
                }
                var count = 0;
                var included = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var name in record?.Diagnostics ?? Array.Empty<string>())
                {
                    if (name == null || name != Path.GetFileName(name) || name.Contains("\\") || name.Contains("/")
                        || !((name.StartsWith("performance-", StringComparison.Ordinal) && name.EndsWith(".jsonl", StringComparison.Ordinal))
                            || (name.StartsWith("capture-", StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal))))
                    { warnings.Add("Ignored invalid diagnostic filename."); continue; }
                    if (included.Add(name) && AddFile(zip, Path.Combine(_root, "ORBIT", "diagnostics", name),
                        "ORBIT/diagnostics/" + name, warnings)) count++;
                }
                if (count == 0) warnings.Add("No performance diagnostics for this raid. Performance logging may have been disabled.");
                WriteText(zip, "report.json", JsonConvert.SerializeObject(new {
                    CreatedUtc = DateTime.UtcNow, ExporterVersion = _version, Raid = record,
                    DiagnosticFiles = count, Warnings = warnings,
                    LogScope = "BepInEx session log, including startup and any earlier raids in that session. Diagnostics belong only to the selected raid."
                }, Formatting.Indented));
                WriteText(zip, "README.txt", "ORBIT debug report\nAttach this ZIP to your bug report and describe what happened.\n"
                    + "The BepInEx log contains messages from installed mods and may include player names and local paths.\n"
                    + "report.json identifies the selected raid and lists missing files. Nothing was uploaded automatically.\n");
            }
            File.Move(temporary, path);
            return path;
        }
        catch { if (File.Exists(temporary)) File.Delete(temporary); throw; }
    }

    private static FileStream OpenShared(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    private static void CopySnapshot(FileStream input, Stream output)
    {
        var remaining = input.Length;
        var buffer = new byte[81920];
        while (remaining > 0)
        {
            var read = input.Read(buffer, 0, (int)Math.Min(remaining, buffer.Length));
            if (read == 0) break;
            output.Write(buffer, 0, read); remaining -= read;
        }
    }
    private static bool AddFile(ZipArchive zip, string path, string name, List<string> warnings)
    {
        try
        {
            using var input = OpenShared(path);
            using var output = zip.CreateEntry(name, CompressionLevel.Fastest).Open();
            CopySnapshot(input, output);
            return true;
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
        { warnings.Add("Could not fully include " + name + ": " + error.Message); return false; }
    }
    private static void WriteText(ZipArchive zip, string name, string text)
    { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(text); }
}
