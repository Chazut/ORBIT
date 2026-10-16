using System.Security.Cryptography;
using System.Text.Json;
using Orbit.Debugging;

namespace Orbit.Server.Debugging;

// A bounded, persistent mailbox on the player's SPT server. Announcements contain
// metadata only. The request operation is the only way to authorize an upload.
public sealed class DebugRelayStore
{
    private sealed class Entry
    {
        public string Host { get; set; } = "";
        public HashSet<string> Members { get; set; } = [];
        public HashSet<string> Requesters { get; set; } = [];
        public DateTime Created { get; set; }
        public DateTime Seen { get; set; }
        public DateTime Requested { get; set; }
        public string State { get; set; } = "idle";
        public string Ticket { get; set; } = "";
        public string Hash { get; set; } = "";
        public string Error { get; set; } = "";
        public bool Ended { get; set; }
        public bool SnapshotFinal { get; set; }
        public long Length { get; set; }
        public long Offset { get; set; }
    }

    private readonly string _root;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = [];
    private readonly Func<DateTime> _now;
    private const long MaxCacheBytes = 1024L * 1024 * 1024;
    private const int MaxRaids = 32;

    public DebugRelayStore(string root, Func<DateTime>? now = null)
    {
        _root = Path.GetFullPath(root);
        _now = now ?? (() => DateTime.UtcNow);
        Directory.CreateDirectory(_root);
        foreach (var path in Directory.GetFiles(_root, "*.json"))
        {
            var key = Path.GetFileNameWithoutExtension(path);
            if (!DebugRelayProtocol.Id(key, 64)) continue;
            try
            {
                var entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(path));
                if (entry == null || !DebugRelayProtocol.Id(entry.Host, 24)) continue;
                entry.Seen = DateTime.MinValue;
                if (entry.State != "ready" || !File.Exists(FilePath(key)) || entry.Length > DebugRelayProtocol.MaxArchiveBytes
                    || new FileInfo(FilePath(key)).Length != entry.Length)
                { DeletePayload(key); entry.State = "idle"; entry.Offset = entry.Length = 0; }
                _entries[key] = entry;
            }
            catch (Exception e) when (e is IOException or JsonException) { }
        }
        foreach (var file in Directory.GetFiles(_root))
        {
            var name = Path.GetFileName(file);
            if (name.Length < 65 || !DebugRelayProtocol.Id(name[..64], 64)) continue;
            var suffix = name[64..];
            if (suffix is ".zip.part" or ".json.tmp" || suffix == ".zip" && !_entries.ContainsKey(name[..64])) File.Delete(file);
        }
        Prune();
    }

    public DebugRelayReply Handle(string profile, DebugRelayRequest request)
    {
        lock (_gate)
        {
            try { return HandleLocked(profile, request); }
            catch (Exception e) when (e is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException)
            { return new DebugRelayReply { State = "error", Error = e.Message }; }
        }
    }

    private DebugRelayReply HandleLocked(string profile, DebugRelayRequest q)
    {
        if (!DebugRelayProtocol.Id(profile, 24) || q == null || !DebugRelayProtocol.Valid(q.Raid))
            throw new InvalidDataException("Invalid debug relay identity.");
        var key = DebugRelayProtocol.Key(q.Raid);
        var now = _now();
        Prune();
        if (q.Operation == "join")
        {
            if (!_entries.TryGetValue(key, out var joined))
            {
                if (_entries.Count >= MaxRaids)
                {
                    var oldest = _entries.Where(p => p.Value.State != "pending" && p.Value.State != "uploading"
                        && now - p.Value.Seen > TimeSpan.FromMinutes(1)).OrderBy(p => p.Value.Created).FirstOrDefault();
                    if (oldest.Key == null) throw new IOException("Debug cache is full; try again later.");
                    DeletePayload(oldest.Key); File.Delete(Path.Combine(_root, oldest.Key + ".json")); _entries.Remove(oldest.Key);
                }
                joined = new Entry { Host = q.Raid.Host, Created = now };
                _entries.Add(key, joined);
            }
            if (q.Raid.Authority && profile != joined.Host) throw new UnauthorizedAccessException("Only the raid host can announce authority.");
            if (joined.Members.Count >= 32 && !joined.Members.Contains(profile)) throw new IOException("Too many raid participants.");
            joined.Members.Add(profile);
            Save(key, joined);
            return Reply(joined);
        }
        if (!_entries.TryGetValue(key, out var e) || !e.Members.Contains(profile))
            throw new UnauthorizedAccessException("This client has no registered debug session for this raid.");
        var owner = profile == e.Host && q.Raid.Authority;
        if (q.Operation == "poll")
        {
            if (!owner) throw new UnauthorizedAccessException("Only the raid host can collect requests.");
            var changed = q.Final != e.Ended;
            e.Seen = now; e.Ended = q.Final;
            if (changed) Save(key, e);
            return Reply(e);
        }
        if (q.Operation == "request")
        {
            if (e.State == "ready" && e.SnapshotFinal) return Reply(e, cached: true);
            if (e.State is "pending" or "uploading") { e.Requesters.Add(profile); return Reply(e); }
            if (now - e.Seen > TimeSpan.FromSeconds(20))
                return new DebugRelayReply { State = "unavailable", Error = "Host/headless is offline or has no matching raid snapshot. Keep it running and update ORBIT on both machines and the SPT server." };
            DeletePayload(key);
            e.State = "pending"; e.Ticket = Guid.NewGuid().ToString("N"); e.Requested = now;
            e.Offset = e.Length = 0; e.Hash = e.Error = ""; e.SnapshotFinal = false;
            e.Requesters.Clear(); e.Requesters.Add(profile);
            Save(key, e);
            return Reply(e);
        }
        if (q.Operation == "status")
        {
            if (e.State is "pending" or "uploading" && now - e.Seen > TimeSpan.FromSeconds(20))
                return new DebugRelayReply { State = "unavailable", Ticket = e.Ticket, Error = "Host/headless disconnected before the transfer finished." };
            return Reply(e);
        }
        if (q.Ticket != e.Ticket || !DebugRelayProtocol.Id(q.Ticket, 32))
            throw new InvalidDataException("The debug transfer expired or was replaced. Export again.");
        if (q.Operation == "cancel")
        {
            e.Requesters.Remove(profile);
            if (e.Requesters.Count == 0 && e.State is "pending" or "uploading")
            {
                DeletePayload(key); e.State = "cancelled"; e.Error = "Host collection cancelled.";
                e.Length = e.Offset = 0; Save(key, e);
            }
            return Reply(e);
        }
        if (q.Operation == "read")
        {
            if (e.State != "ready" || q.Offset < 0 || q.Offset >= e.Length) throw new InvalidDataException("Invalid download offset.");
            using var input = File.OpenRead(FilePath(key));
            input.Position = q.Offset;
            var buffer = new byte[(int)Math.Min(DebugRelayProtocol.ChunkBytes, e.Length - q.Offset)];
            input.ReadExactly(buffer);
            var reply = Reply(e); reply.Data = Convert.ToBase64String(buffer); reply.Offset = q.Offset;
            return reply;
        }
        if (!owner) throw new UnauthorizedAccessException("Only the registered host can upload this raid.");
        if (q.Operation == "fail")
        {
            if (e.State != "pending" && e.State != "uploading") return Reply(e);
            e.State = "failed"; e.Error = q.Error ?? "Host export failed.";
            if (e.Error.Length > 400) e.Error = e.Error[..400];
            e.Length = e.Offset = 0;
            DeletePayload(key); Save(key, e); return Reply(e);
        }
        if (q.Operation == "begin")
        {
            if (e.State != "pending" || q.Length < 1 || q.Length > DebugRelayProtocol.MaxArchiveBytes || !DebugRelayProtocol.Id(q.Hash, 64))
                throw new InvalidDataException("Invalid archive size, checksum or request state.");
            if (_entries.Values.Sum(x => x.Length) + q.Length > MaxCacheBytes) throw new IOException("Debug cache storage limit reached.");
            using (File.Create(FilePath(key) + ".part")) { }
            e.Length = q.Length; e.Hash = q.Hash; e.Offset = 0; e.SnapshotFinal = q.Final; e.State = "uploading";
            Save(key, e); return Reply(e);
        }
        if (q.Operation == "chunk")
        {
            if (e.State != "uploading" || q.Offset != e.Offset || q.Data == null
                || q.Data.Length > (DebugRelayProtocol.ChunkBytes + 2) / 3 * 4)
                throw new InvalidDataException("Invalid upload chunk.");
            byte[] bytes;
            try { bytes = Convert.FromBase64String(q.Data); } catch (FormatException) { throw new InvalidDataException("Invalid chunk encoding."); }
            if (bytes.Length < 1 || bytes.Length > DebugRelayProtocol.ChunkBytes || e.Offset + bytes.Length > e.Length)
                throw new InvalidDataException("Upload exceeds declared size.");
            using (var output = new FileStream(FilePath(key) + ".part", FileMode.Open, FileAccess.Write))
            { output.Position = e.Offset; output.Write(bytes); }
            e.Offset += bytes.Length;
            e.Seen = now;
            return Reply(e);
        }
        if (q.Operation == "complete")
        {
            if (e.State != "uploading" || e.Offset != e.Length) throw new InvalidDataException("Incomplete upload.");
            using (var input = File.OpenRead(FilePath(key) + ".part"))
                if (DebugRelayProtocol.Hex(SHA256.HashData(input)) != e.Hash) throw new InvalidDataException("Archive checksum mismatch.");
            File.Move(FilePath(key) + ".part", FilePath(key), true);
            e.State = "ready"; Save(key, e); return Reply(e);
        }
        throw new InvalidDataException("Unknown debug relay operation.");
    }

    private static DebugRelayReply Reply(Entry e, bool cached = false) => new() {
        State = e.State, Ticket = e.Ticket, Hash = e.Hash, Offset = e.Offset,
        Length = e.Length, Final = e.SnapshotFinal, Error = e.Error, Cached = cached };
    private string FilePath(string key) => Path.Combine(_root, key + ".zip");
    private void Save(string key, Entry e)
    {
        var path = Path.Combine(_root, key + ".json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(e));
        File.Move(path + ".tmp", path, true);
    }
    private void DeletePayload(string key)
    { File.Delete(FilePath(key)); File.Delete(FilePath(key) + ".part"); }
    private void Prune()
    {
        foreach (var pair in _entries.ToArray())
        {
            var e = pair.Value;
            if (_now() - e.Created > TimeSpan.FromHours(24))
            { DeletePayload(pair.Key); File.Delete(Path.Combine(_root, pair.Key + ".json")); _entries.Remove(pair.Key); }
            else if (e.State is "pending" or "uploading" && _now() - e.Requested > TimeSpan.FromMinutes(10))
            { DeletePayload(pair.Key); e.State = "failed"; e.Error = "Debug transfer timed out."; e.Length = e.Offset = 0; Save(pair.Key, e); }
        }
    }
}
