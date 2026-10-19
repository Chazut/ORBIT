using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Orbit.Helpers;

// Optional RR integration through its existing read-only API, without loading SQLite
// or referencing the RR client assembly. All network and file work stays off-thread.
internal sealed class RaidReviewDebug
{
    internal sealed class Context
    {
        public string Server, ProfileId;
    }

    internal sealed class Snapshot
    {
        public string OrbitRaidId, RaidId, Status;
        public DateTime CapturedUtc;
        public bool InProgress, Cached;
        public string[] Files = Array.Empty<string>();
        public List<string> Warnings = new();
        public List<Transfer> Transfers = new();
        public string Positions = "Missing";
    }

    internal sealed class Transfer
    {
        public string Endpoint, Phase, Error;
        public int Attempt;
        public long Bytes;
        public double Seconds;
        public bool Success;
    }

    internal sealed class Budget
    {
        public TimeSpan Total = TimeSpan.FromMinutes(4);
        public TimeSpan Headers = TimeSpan.FromSeconds(90);
        public TimeSpan Idle = TimeSpan.FromSeconds(30);
    }

    private static readonly string[] Endpoints = {
        "positions", "loose_loot", "bot_quests", "bot_objectives", "orbit_field",
        "orbit_main_objectives", "orbit_bot_objectives", "switches", "airdrop_flights",
        "airdrops", "orbit_ghost_wake", "orbit_ghost_hearing", "orbit_ghost_fights"
    };
    private static readonly HttpClient DefaultClient = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient _client;
    private readonly string _folder;
    private readonly Action<string> _log;
    private readonly Budget _budget;
    private const long MaxFileBytes = 256L * 1024 * 1024;

    internal RaidReviewDebug(string folder, HttpClient client = null, Action<string> log = null, Budget budget = null)
    { _folder = folder; _client = client ?? DefaultClient; _log = log; _budget = budget ?? new Budget(); }

    private void Report(string message) { try { _log?.Invoke("RR EXPORT: " + message); } catch { } }

    internal string FilePath(string orbitId, string name) => Path.Combine(_folder, "last-raid", orbitId + "-rr", name);
    internal static bool IsExportFile(string name) => name == "raid.json" || Endpoints.Any(e => name == e + ".json");

    internal async Task<Snapshot> Capture(DebugArchive.RaidRecord record, Context fallback = null)
    {
        if (record == null) return new Snapshot { Status = "No recorded ORBIT raid" };
        var saved = ReadSaved(record);
        var context = record.RaidReview ?? fallback;
        if (context == null) return saved ?? new Snapshot { Status = "Raid Review not detected" };
        var result = new Snapshot { OrbitRaidId = record.Id, Status = "Unavailable", InProgress = !record.EndedUtc.HasValue };
        using var deadline = new CancellationTokenSource(_budget.Total);
        try
        {
            if (!Uri.TryCreate(context.Server, UriKind.Absolute, out var server)
                || (server.Scheme != "http" && server.Scheme != "https") || !string.IsNullOrEmpty(server.UserInfo))
                throw new InvalidDataException("Invalid Raid Review server address.");
            var folder = Path.GetDirectoryName(FilePath(record.Id, "raid.json"));
            Directory.CreateDirectory(folder);
            var listing = Path.Combine(folder, "raids.tmp");
            JObject matched;
            try
            {
                await Download(new Uri(server, "/api/raids"), listing, deadline.Token, result, 16 * 1024 * 1024).ConfigureAwait(false);
                // DateParseHandling.None preserves offsets supplied by remote/Fika servers.
                using var reader = new JsonTextReader(File.OpenText(listing)) { DateParseHandling = DateParseHandling.None, CloseInput = true };
                matched = Match(JArray.Load(reader), record, context);
            }
            finally { if (File.Exists(listing)) File.Delete(listing); }
            if (matched == null) throw new InvalidDataException("No unique RR raid matches this map, profile and time window.");
            result.RaidId = (string)matched["raidId"];
            if (!Guid.TryParse(result.RaidId, out _)) throw new InvalidDataException("Invalid RR raid identifier.");
            if (saved != null && saved.RaidId != result.RaidId)
                throw new InvalidDataException("RR raid differs from the saved snapshot; files were not mixed.");
            var prefix = "/api/raids/" + Uri.EscapeDataString(result.RaidId);
            var raidPath = FilePath(record.Id, "raid.json");
            await Download(new Uri(server, prefix), raidPath + ".part", deadline.Token, result).ConfigureAwait(false);
            try
            {
                using (var reader = new JsonTextReader(File.OpenText(raidPath + ".part")) { CloseInput = true })
                {
                    var raid = JObject.Load(reader);
                    if ((string)raid["raidId"] != result.RaidId)
                        throw new InvalidDataException("RR response did not identify the selected raid.");
                }
                Promote(raidPath + ".part", raidPath);
            }
            finally { if (File.Exists(raidPath + ".part")) File.Delete(raidPath + ".part"); }
            var files = new List<string> { "raid.json" };
            long bytes = new FileInfo(raidPath).Length;
            foreach (var endpoint in Endpoints)
            {
                var name = endpoint + ".json";
                var path = FilePath(record.Id, name);
                try
                {
                    await Download(new Uri(server, prefix + "/" + endpoint), path + ".part", deadline.Token, result,
                        Math.Min(MaxFileBytes, 512L * 1024 * 1024 - bytes)).ConfigureAwait(false);
                    Promote(path + ".part", path);
                }
                catch (Exception error)
                {
                    var retained = File.Exists(path);
                    result.Cached |= retained;
                    result.Warnings.Add(name + ": " + error.Message + (retained ? " (kept cached copy)" : ""));
                    Report($"file={name} outcome={(retained ? "cached" : "missing")} reason={error.Message}");
                }
                finally { if (File.Exists(path + ".part")) File.Delete(path + ".part"); }
                if (File.Exists(path)) { files.Add(name); bytes += new FileInfo(path).Length; }
                if (deadline.IsCancellationRequested) break;
            }
            // Retain previously fetched files when an optional endpoint is temporarily unavailable.
            foreach (var name in saved?.Files ?? Array.Empty<string>())
                if (IsExportFile(name) && File.Exists(FilePath(record.Id, name)) && !files.Contains(name))
                { files.Add(name); result.Cached = true; }
            result.Files = files.ToArray(); result.CapturedUtc = DateTime.UtcNow;
            result.Positions = !files.Contains("positions.json") ? "Missing" :
                result.Transfers.Any(t => t.Endpoint.EndsWith("/positions") && t.Success) ? "Included" : "Cached";
            if (result.Positions == "Missing") result.Warnings.Add("Position data is MISSING; movement cannot be reconstructed from this ZIP.");
            result.Status = result.Warnings.Count == 0 ? "Included" : "Partial";
            Report($"raid={result.RaidId} status={result.Status} positions={result.Positions} files={files.Count} bytes={bytes} warnings={result.Warnings.Count}");
            var manifest = FilePath(record.Id, "snapshot.json");
            File.WriteAllText(manifest + ".part", JsonConvert.SerializeObject(result, Formatting.Indented));
            Promote(manifest + ".part", manifest);
            return result;
        }
        catch (Exception error)
        {
            result.Warnings.Add("Raid Review snapshot unavailable: " + error.Message);
            result.Positions = "Missing";
            Report($"status=Unavailable positions={(saved?.Files.Contains("positions.json") == true ? "Cached" : "Missing")} reason={error.Message}");
            if (saved == null) return result;
            saved.Cached = true; saved.Warnings.AddRange(result.Warnings);
            saved.Positions = saved.Files.Contains("positions.json") ? "Cached" : "Missing";
            saved.Transfers ??= new(); saved.Transfers.AddRange(result.Transfers);
            saved.Status = "Partial";
            return saved;
        }
    }

    internal static JObject Match(JArray raids, DebugArchive.RaidRecord record, Context context)
    {
        // ORBIT starts before RR's OnGameStarted. Restrict to this actual raid window,
        // and reject ambiguity instead of taking another player's most recent raid.
        var end = record.EndedUtc ?? DateTime.UtcNow;
        var map = (record.Map ?? "").Split('@');
        var variant = map.Length > 1 ? map[1] : "";
        var matches = raids.OfType<JObject>().Where(raid =>
            string.Equals((string)raid["location"], map[0], StringComparison.OrdinalIgnoreCase)
            && string.Equals((string)raid["locationVariant"] ?? "", variant, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrEmpty(context.ProfileId) || (string)raid["profileId"] == context.ProfileId)
            && TryUtc((string)raid["time"], out var start)
            && start >= record.StartedUtc.AddSeconds(-2) && start <= end.AddSeconds(2)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool TryUtc(string value, out DateTime utc)
    {
        utc = default;
        // Old RR timestamps without an offset are local to the recording client.
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var date)) return false;
        utc = date.UtcDateTime; return true;
    }

    private Snapshot ReadSaved(DebugArchive.RaidRecord record)
    {
        try
        {
            var file = FilePath(record.Id, "snapshot.json");
            if (!File.Exists(file)) return null;
            var saved = JsonConvert.DeserializeObject<Snapshot>(File.ReadAllText(file));
            if (saved?.OrbitRaidId != record.Id || !Guid.TryParse(saved.RaidId, out _)) return null;
            saved.Files = (saved.Files ?? Array.Empty<string>()).Where(IsExportFile)
                .Where(name => File.Exists(FilePath(record.Id, name))).Distinct().ToArray();
            if (!saved.Files.Contains("raid.json")) return null;
            saved.Warnings ??= new(); saved.Cached = true;
            saved.Positions = saved.Files.Contains("positions.json") ? "Cached" : "Missing";
            return saved;
        }
        catch (Exception) { return null; }
    }

    private async Task Download(Uri url, string path, CancellationToken deadline, Snapshot snapshot, long limit = MaxFileBytes)
    {
        var attempts = url.AbsolutePath.EndsWith("/positions") ? 2 : 1;
        for (var attempt = 1; ; attempt++)
        {
            var transfer = new Transfer { Endpoint = url.AbsolutePath, Attempt = attempt, Phase = "headers" };
            snapshot.Transfers.Add(transfer);
            var watch = Stopwatch.StartNew();
            try
            {
                await DownloadAttempt(url, path, deadline, limit, transfer).ConfigureAwait(false);
                transfer.Success = true;
                return;
            }
            catch (Exception error)
            {
                transfer.Error = error is OperationCanceledException
                    ? (deadline.IsCancellationRequested ? "total export deadline" : transfer.Phase + " timeout")
                    : error.Message;
                if (attempt >= attempts || deadline.IsCancellationRequested
                    || error is not (OperationCanceledException or HttpRequestException or IOException)
                    || error is InvalidDataException)
                    throw new IOException($"{transfer.Phase}: {transfer.Error}; bytes={transfer.Bytes}; elapsed={watch.Elapsed.TotalSeconds:F1}s; attempt={attempt}", error);
            }
            finally
            {
                transfer.Seconds = watch.Elapsed.TotalSeconds;
                Report($"endpoint={transfer.Endpoint} attempt={attempt} phase={transfer.Phase} success={transfer.Success} bytes={transfer.Bytes} seconds={transfer.Seconds:F2} error={transfer.Error ?? "none"}");
            }
        }
    }

    private async Task DownloadAttempt(Uri url, string path, CancellationToken deadline, long limit, Transfer transfer)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(deadline);
        timeout.CancelAfter(_budget.Headers);
        try
        {
            using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            transfer.Phase = "body";
            timeout.CancelAfter(_budget.Idle);
            if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException($"RR response size {response.Content.Headers.ContentLength} exceeds {limit} byte limit.");
            using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920]; int read;
                while ((read = await input.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false)) != 0)
                {
                    transfer.Bytes += read;
                    if (transfer.Bytes > limit) throw new InvalidDataException($"RR response exceeds {limit} byte limit.");
                    await output.WriteAsync(buffer, 0, read, timeout.Token).ConfigureAwait(false);
                    timeout.CancelAfter(_budget.Idle);
                }
            }
            // Parsing large replay files is bounded by the export deadline, not network inactivity.
            timeout.CancelAfter(Timeout.InfiniteTimeSpan);
            transfer.Phase = "validation";
            using var reader = new JsonTextReader(File.OpenText(path)) { CloseInput = true };
            if (!reader.Read() || reader.TokenType is not (JsonToken.StartArray or JsonToken.StartObject))
                throw new InvalidDataException("RR response is not JSON data.");
            var complete = false;
            while (reader.Read())
            {
                timeout.Token.ThrowIfCancellationRequested();
                complete = reader.Depth == 0 && reader.TokenType is JsonToken.EndArray or JsonToken.EndObject;
            }
            if (!complete)
                throw new InvalidDataException("RR response is incomplete JSON.");
            transfer.Phase = "complete";
        }
        catch { if (File.Exists(path)) File.Delete(path); throw; }
    }

    private static void Promote(string temporary, string path)
    { if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path); }
}
