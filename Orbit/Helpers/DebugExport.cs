using System;
using System.IO;
using System.Reflection;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Bootstrap;
using Comfort.Common;
using EFT;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Orbit.Debugging;
using SPT.Common.Http;

namespace Orbit.Helpers;

internal static class DebugExport
{
    private static readonly DebugArchive Archive = new(BepInEx.Paths.BepInExRootPath, Plugin.ReleaseVersion, log: Log.Always);
    private static readonly DebugRelayTransfer Relay = new(Post);
    private static readonly Task<DebugArchive.RaidRecord> Restore = Archive.LoadLastRaid();
    private static Task<string> _export;
    private static Task<DebugRelayReply> _accept;
    private static DebugRelayListener _listener;
    private static Task _serving;
    private static DebugArchive.RaidRecord _record, _acceptRecord, _listenerRecord;
    private static GameWorld _world;
    private static bool _restored;
    private static DateTime _retryListener;
    private static string _servingTicket, _status = "Create a ZIP to attach to your bug report.";
    private static CancellationTokenSource _cancel;
    private static DebugTransferProgress _progress;
    private static volatile bool _hostTransfer;
    internal static bool Busy => _export != null;
    internal static bool CanCancelHost => _hostTransfer && _cancel?.IsCancellationRequested == false;
    internal static DebugTransferProgress Progress => Volatile.Read(ref _progress);
    internal static string Status => Busy ? Progress?.Label ?? "Creating debug ZIP..." : _status;
    internal static string LastPath { get; private set; }
    internal static string Folder => Path.Combine(BepInEx.Paths.BepInExRootPath, "ORBIT", "debug");

    internal static void BeginRaid(string map)
    {
        var world = Singleton<GameWorld>.Instance;
        if (world != null && ReferenceEquals(_world, world)) return;
        _world = world;
        Archive.BeginRaid(map, RaidReviewContext(), Log.ReportSettings("raid-start"));
        _record = Archive.Snapshot();
        _restored = true;
    }
    internal static void WorldStarted()
    {
        try
        {
            var world = Singleton<GameWorld>.Instance;
            if (world == null) return;
            BeginRaid(MapVariants.ZoneKey(world.LocationId, MapVariants.Detect(world.LocationId)));
            // Fika's shared server GUID is available after its connection handshake.
            AttachFika();
        }
        catch (Exception e) { Log.Warning("DEBUG ZIP raid detection failed: " + e.Message); }
    }
    private static void AttachFika()
    {
        var link = FikaContext();
        Archive.SetFika(link, link == null && Chainloader.PluginInfos.ContainsKey("com.fika.core")
            ? "Fika raid identity unavailable. Only local files can be included; check compatible ORBIT/Fika versions." : null);
        _record = Archive.Snapshot();
        _restored = true;
    }
    internal static void RegisterDiagnostic(string path) => Archive.RegisterDiagnostic(path);
    internal static void FinishRaid(Task writers)
    {
        Archive.FinishRaid(writers, Log.DebugFlush(), Log.ReportSettings("raid-end"));
        _record = Archive.Snapshot() ?? _record;
    }
    internal static void WorldEnded()
    {
        FinishRaid(Task.CompletedTask);
        _world = null;
    }

    internal static void Start()
    {
        if (Busy) return;
        try
        {
            SetProgress(new DebugTransferProgress("Preparing local logs, diagnostics and RR replay"));
            _cancel = new CancellationTokenSource();
            var local = Archive.Create(DiagnosticCapture.PrepareExport(), Log.DebugFlush(), RaidReviewContext(),
                logging: Log.ReportSettings("export"));
            _status = null;
            _export = Task.Run(async () =>
            {
                var path = await local.ConfigureAwait(false);
                DebugArchive.RaidRecord record;
                using (var zip = System.IO.Compression.ZipFile.OpenRead(path))
                using (var reader = new StreamReader(zip.GetEntry("report.json").Open()))
                    record = JObject.Parse(reader.ReadToEnd())["Raid"]?.ToObject<DebugArchive.RaidRecord>();
                if (DebugRelayProtocol.Valid(record?.Fika) && !record.Fika.Authority)
                {
                    // Start the remote budget after local RR preparation. Allow host preparation
                    // plus upload/download of a large ZIP at the relay's existing rate limit.
                    _cancel.CancelAfter(TimeSpan.FromMinutes(20));
                    _hostTransfer = true;
                    try
                    {
                        await Relay.Send(new DebugRelayRequest { Operation = "join", Raid = record.Fika }, _cancel.Token).ConfigureAwait(false);
                    }
                    catch { /* Collect records relay failures in the usable local ZIP. */ }
                    var warning = await Relay.Collect(path, record.Fika, SetProgress, _cancel.Token).ConfigureAwait(false);
                    if (warning != null) _status = "Local ZIP saved; host files unavailable: " + warning;
                }
                return path;
            });
        }
        catch (Exception error) { _status = "Could not create debug ZIP: " + error.Message; Log.Error(_status); }
    }

    internal static void Cancel() => _cancel?.Cancel();
    private static void SetProgress(DebugTransferProgress progress) => Volatile.Write(ref _progress, progress);

    internal static void Refresh()
    {
        try { RefreshRelay(); }
        catch (Exception e) { _retryListener = DateTime.UtcNow.AddSeconds(30); Log.Warning("DEBUG ZIP relay unavailable: " + e.Message); }
        if (_export?.IsCompleted != true) return;
        try
        {
            LastPath = _export.GetAwaiter().GetResult();
            _status ??= "Saved: " + LastPath;
            Log.Always("DEBUG ZIP saved: " + LastPath);
        }
        catch (Exception error) { _status = "Could not create debug ZIP: " + error.Message; Log.Error(_status); }
        finally { _export = null; _hostTransfer = false; _cancel?.Dispose(); _cancel = null; }
    }

    private static void RefreshRelay()
    {
        if (!_restored && Restore.IsCompleted)
        {
            _restored = true;
            if (Restore.Status == TaskStatus.RanToCompletion) _record = Restore.Result;
        }
        var record = _record;
        var canListen = _restored && record?.Fika?.Authority == true && record.Fika.Host == RequestHandler.SessionId
            && Chainloader.PluginInfos.ContainsKey("com.fika.core");
        if ((!ReferenceEquals(record, _listenerRecord) || canListen != (_listener != null)) && DateTime.UtcNow >= _retryListener)
        {
            _listener?.Dispose(); _listener = null; _listenerRecord = record;
            if (canListen && DebugRelayProtocol.Valid(record.Fika))
            {
                using var request = RequestHandler.HttpClient.CreateNewHttpRequest(HttpMethod.Get, DebugRelayProtocol.SocketRoute);
                _listener = new DebugRelayListener(request.RequestUri, RequestHandler.SessionId, record.Fika,
                    record.EndedUtc.HasValue, Log.Warning);
            }
        }
        if (_accept?.IsCompleted == true)
        {
            if (_accept.Status == TaskStatus.RanToCompletion && _accept.Result.State == "accepted"
                && _acceptRecord.Id == record?.Id && canListen
                && DebugRelayProtocol.Key(_acceptRecord.Fika) == DebugRelayProtocol.Key(record.Fika))
            {
                var ticket = _accept.Result.Ticket;
                _servingTicket = ticket;
                // Capture live diagnostics only on the Unity thread after the server confirms this ticket.
                Task<string> archive;
                try { archive = Archive.Create(DiagnosticCapture.PrepareExport(), Log.DebugFlush(), expectedRaid: record.Id,
                    logging: Log.ReportSettings("host-export")); }
                catch (Exception e) { archive = Task.FromException<string>(e); }
                _serving = Task.Run(() => Serve(record.Fika, ticket, archive));
            }
            // Cancellation or a new raid may invalidate a queued request. Never export it anyway.
            if (_accept.IsFaulted) _ = _accept.Exception;
            _accept = null;
        }
        if (_accept != null || _serving?.IsCompleted == false || !canListen) return;
        var notice = _listener?.TakeRequest();
        if (notice == null || notice.Ticket == _servingTicket) return;
        _acceptRecord = record;
        _accept = Task.Run(() => Relay.Send(new DebugRelayRequest { Operation = "accept", Raid = record.Fika, Ticket = notice.Ticket }));
    }

    internal static void Shutdown() { _listener?.Dispose(); _listener = null; _listenerRecord = null; _cancel?.Cancel(); }

    private static async Task Serve(DebugRaidLink link, string ticket, Task<string> archive)
    {
        string path = null;
        try
        {
            path = await archive.ConfigureAwait(false);
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
            await Relay.Upload(link, ticket, path, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            try { await Relay.Send(new DebugRelayRequest { Operation = "fail", Raid = link, Ticket = ticket, Error = e.Message }).ConfigureAwait(false); }
            catch { }
        }
        finally { if (path != null) try { File.Delete(path); } catch { } }
    }

    private static DebugRaidLink FikaContext()
    {
        if (!Chainloader.PluginInfos.TryGetValue("com.fika.core", out var plugin)) return null;
        try
        {
            var type = plugin.Instance.GetType().Assembly.GetType("Fika.Core.Main.Utils.FikaBackendUtils");
            object Read(string name) => type?.GetProperty(name, BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
            var guid = Read("ServerGuid");
            if (!(guid is Guid session) || session == Guid.Empty) return null;
            var record = Archive.Snapshot();
            var result = new DebugRaidLink { Session = session.ToString("N"), Map = record.Map,
                Host = Read("GroupId") as string, Authority = Read("IsServer") is true, Headless = Read("IsHeadlessGame") is true || Read("IsHeadless") is true };
            return DebugRelayProtocol.Valid(result) ? result : null;
        }
        catch { return null; }
    }

    private static async Task<DebugRelayReply> Post(DebugRelayRequest payload, CancellationToken cancel)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var client = RequestHandler.HttpClient;
        using var request = client.CreateNewHttpRequest(HttpMethod.Post, DebugRelayProtocol.Route);
        var body = System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new { Payload = payload }));
        request.Content = new ByteArrayContent(SPT.Common.Utils.Zlib.Compress(body, SPT.Common.Utils.ZlibCompression.Maximum));
        using var response = await client.HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, 0, chunk.Length, timeout.Token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > 512 * 1024) throw new InvalidDataException("Oversized debug relay response.");
            buffer.Write(chunk, 0, read);
        }
        var data = buffer.ToArray();
        if (SPT.Common.Utils.Zlib.IsCompressed(data)) data = SPT.Common.Utils.Zlib.Decompress(data);
        return JsonConvert.DeserializeObject<DebugRelayReply>(System.Text.Encoding.UTF8.GetString(data));
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
