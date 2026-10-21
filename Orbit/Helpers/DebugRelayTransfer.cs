using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Orbit.Debugging;

namespace Orbit.Helpers;

internal sealed class DebugTransferProgress
{
    internal readonly string Stage;
    internal readonly long Done, Total;
    internal DebugTransferProgress(string stage, long done = 0, long total = 0)
    { Stage = stage; Done = done; Total = total; }
    internal float Fraction => Total > 0 ? (float)Math.Min(1d, (double)Done / Total) : -1;
    internal string Label => Total > 0
        ? $"{Stage}: {Done / 1048576d:F1} / {Total / 1048576d:F1} MiB ({Fraction * 100:F0}%)" : Stage;
}

internal sealed class DebugRelayTransfer
{
    private readonly Func<DebugRelayRequest, CancellationToken, Task<DebugRelayReply>> _send;
    private readonly Func<int, CancellationToken, Task> _delay;
    internal DebugRelayTransfer(Func<DebugRelayRequest, CancellationToken, Task<DebugRelayReply>> send,
        Func<int, CancellationToken, Task> delay = null)
    { _send = send; _delay = delay ?? Task.Delay; }

    internal async Task<DebugRelayReply> Send(DebugRelayRequest request, CancellationToken cancel = default)
    {
        var reply = await _send(request, cancel).ConfigureAwait(false);
        if (reply == null || reply.Protocol != 1 || string.IsNullOrEmpty(reply.State))
            throw new InvalidDataException("Update ORBIT on the SPT server to use Fika debug collection.");
        if (reply.State == "error") throw new InvalidDataException(reply.Error ?? "Debug relay rejected the request.");
        return reply;
    }

    internal async Task Upload(DebugRaidLink raid, string ticket, string path, CancellationToken cancel)
    {
        using var file = File.OpenRead(path);
        if (file.Length > DebugRelayProtocol.MaxArchiveBytes) throw new IOException("Host ZIP exceeds the 256 MiB transfer limit.");
        string hash;
        using (var sha = SHA256.Create()) hash = DebugRelayProtocol.Hex(sha.ComputeHash(file));
        file.Position = 0;
        bool final;
        using (var zip = ZipFile.OpenRead(path))
        {
            var report = Report(zip);
            VerifyIdentity(report, raid);
            final = report["Raid"]?["EndedUtc"]?.Type != JTokenType.Null && report["Raid"]?["EndedUtc"] != null;
        }
        await Send(new DebugRelayRequest { Operation = "begin", Raid = raid, Ticket = ticket,
            Hash = hash, Length = file.Length, Final = final }, cancel).ConfigureAwait(false);
        var buffer = new byte[DebugRelayProtocol.ChunkBytes];
        var timer = Stopwatch.StartNew();
        long offset = 0;
        while (offset < file.Length)
        {
            cancel.ThrowIfCancellationRequested();
            var read = await file.ReadAsync(buffer, 0, buffer.Length, cancel).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException();
            await Send(new DebugRelayRequest { Operation = "chunk", Raid = raid, Ticket = ticket,
                Offset = offset, Data = Convert.ToBase64String(buffer, 0, read) }, cancel).ConfigureAwait(false);
            offset += read;
            await Throttle(offset, timer, cancel).ConfigureAwait(false);
        }
        await Send(new DebugRelayRequest { Operation = "complete", Raid = raid, Ticket = ticket }, cancel).ConfigureAwait(false);
    }

    // The request command occurs only in this user-initiated path, never in a heartbeat.
    internal async Task<string> Collect(string localZip, DebugRaidLink raid,
        Action<DebugTransferProgress> progress, CancellationToken cancel)
    {
        var temporary = localZip + ".host.part";
        DebugRelayReply reply = null;
        string error = null;
        try
        {
            progress(new DebugTransferProgress("Contacting raid host/headless"));
            reply = await Send(new DebugRelayRequest { Operation = "request", Raid = raid }, cancel).ConfigureAwait(false);
            var ticket = reply.Ticket;
            while (reply.State == "pending" || reply.State == "uploading")
            {
                progress(new DebugTransferProgress(reply.State == "pending" ? "Host: preparing logs and RR replay" : "Host to server",
                    reply.Offset, reply.Length));
                await _delay(750, cancel).ConfigureAwait(false);
                reply = await Send(new DebugRelayRequest { Operation = "status", Raid = raid }, cancel).ConfigureAwait(false);
                if (reply.Ticket != ticket) throw new IOException("The host transfer was replaced. Export again.");
            }
            if (reply.State != "ready") throw new IOException(reply.Error ?? "Host snapshot unavailable.");
            if (reply.Length < 1 || reply.Length > DebugRelayProtocol.MaxArchiveBytes || !DebugRelayProtocol.Id(reply.Hash, 64))
                throw new InvalidDataException("Invalid host archive metadata.");
            var cached = reply.Cached;
            using (var output = File.Create(temporary))
            {
                var timer = Stopwatch.StartNew();
                while (output.Length < reply.Length)
                {
                    progress(new DebugTransferProgress(cached ? "Downloading cached host files" : "Server to client", output.Length, reply.Length));
                    var chunk = await Send(new DebugRelayRequest { Operation = "read", Raid = raid,
                        Ticket = ticket, Offset = output.Length }, cancel).ConfigureAwait(false);
                    if (chunk.Offset != output.Length || chunk.Length != reply.Length || chunk.Hash != reply.Hash
                        || chunk.Data == null || chunk.Data.Length > (DebugRelayProtocol.ChunkBytes + 2) / 3 * 4)
                        throw new InvalidDataException("Host download changed during transfer.");
                    var bytes = Convert.FromBase64String(chunk.Data);
                    if (bytes.Length != Math.Min(DebugRelayProtocol.ChunkBytes, reply.Length - output.Length))
                        throw new InvalidDataException("Invalid host download chunk.");
                    await output.WriteAsync(bytes, 0, bytes.Length, cancel).ConfigureAwait(false);
                    await Throttle(output.Length, timer, cancel).ConfigureAwait(false);
                }
            }
            progress(new DebugTransferProgress("Verifying host files", reply.Length, reply.Length));
            using (var input = File.OpenRead(temporary))
            using (var sha = SHA256.Create())
                if (DebugRelayProtocol.Hex(sha.ComputeHash(input)) != reply.Hash) throw new InvalidDataException("Host archive checksum mismatch.");
            progress(new DebugTransferProgress("Adding host files to ZIP"));
            Merge(localZip, temporary, raid, reply);
        }
        catch (Exception e) { error = e is OperationCanceledException ? "Host collection timed out or was cancelled." : e.Message; }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            if (error != null && DebugRelayProtocol.Id(reply?.Ticket, 32))
            {
                try { await Send(new DebugRelayRequest { Operation = "cancel", Raid = raid, Ticket = reply.Ticket }).ConfigureAwait(false); }
                catch { }
            }
        }
        if (error != null) Rewrite(localZip, null, raid, reply, error);
        return error;
    }

    private async Task Throttle(long bytes, Stopwatch timer, CancellationToken cancel)
    {
        var wait = (long)(bytes * 1000d / DebugRelayProtocol.BytesPerSecond) - timer.ElapsedMilliseconds;
        if (wait > 0) await _delay((int)Math.Min(wait, 2000), cancel).ConfigureAwait(false);
    }

    private static JObject Report(ZipArchive zip)
    {
        var entry = zip.GetEntry("report.json");
        if (entry == null || entry.Length > 1024 * 1024) throw new InvalidDataException("Missing or oversized host report.");
        using var reader = new StreamReader(entry.Open());
        return JObject.Parse(reader.ReadToEnd());
    }
    private static void VerifyIdentity(JObject report, DebugRaidLink raid)
    {
        var link = report["Raid"]?["Fika"]?.ToObject<DebugRaidLink>();
        if (!DebugRelayProtocol.Valid(link) || !link.Authority || DebugRelayProtocol.Key(link) != DebugRelayProtocol.Key(raid))
            throw new InvalidDataException("Host archive belongs to a different raid.");
    }
    private static bool Allowed(string name)
    {
        if (name == "report.json" || name == "README.txt" || name == "BepInEx/LogOutput.log"
            || name == "BepInEx/current-session-LogOutput.log") return true;
        if (name.StartsWith("RaidReview/", StringComparison.Ordinal)) return RaidReviewDebug.IsExportFile(name.Substring(11));
        const string prefix = "ORBIT/diagnostics/";
        if (!name.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var file = name.Substring(prefix.Length);
        return !file.Contains("/") && !file.Contains("\\") && !file.Contains(":")
            && (file.StartsWith("performance-", StringComparison.Ordinal) && file.EndsWith(".jsonl", StringComparison.Ordinal)
                || file.StartsWith("capture-", StringComparison.Ordinal) && file.EndsWith(".json", StringComparison.Ordinal));
    }
    private static void Merge(string target, string source, DebugRaidLink raid, DebugRelayReply reply)
    {
        using var host = ZipFile.OpenRead(source);
        VerifyIdentity(Report(host), raid);
        if (host.Entries.Count > 128 || host.Entries.Sum(e => e.Length) > 1024L * 1024 * 1024
            || host.Entries.Any(e => !Allowed(e.FullName) || e.Length > 256L * 1024 * 1024)
            || host.Entries.Select(e => e.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != host.Entries.Count)
            throw new InvalidDataException("Host archive contains unsupported or oversized files.");
        Rewrite(target, host, raid, reply, null);
    }
    private static void Rewrite(string path, ZipArchive host, DebugRaidLink raid, DebugRelayReply reply, string error)
    {
        // Stream entries into a replacement ZIP: Update mode can buffer entire RR files in RAM.
        // The original local report remains intact if decompression or a disk write fails.
        var staging = path + ".merge";
        try
        {
            using (var local = ZipFile.OpenRead(path))
            using (var output = ZipFile.Open(staging, ZipArchiveMode.Create))
            {
                var report = Report(local);
                report["HostDebug"] = JObject.FromObject(new { Status = error == null ? "Included" : "Unavailable",
                    Raid = raid, Cached = reply?.Cached ?? false, Final = reply?.Final ?? false,
                    Folder = error == null ? "Host/" : null, Error = error });
                foreach (var entry in local.Entries.Where(e => e.FullName != "report.json")) Copy(entry, output, entry.FullName);
                if (host != null) foreach (var entry in host.Entries) Copy(entry, output, "Host/" + entry.FullName);
                using var writer = new StreamWriter(output.CreateEntry("report.json").Open());
                writer.Write(report.ToString(Formatting.Indented));
            }
            File.Replace(staging, path, null);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }
    private static void Copy(ZipArchiveEntry entry, ZipArchive output, string name)
    {
        using var input = entry.Open();
        using var destination = output.CreateEntry(name, name.EndsWith(".zip", StringComparison.Ordinal)
            ? CompressionLevel.NoCompression : CompressionLevel.Fastest).Open();
        var buffer = new byte[81920];
        long written = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            written += read;
            if (written > entry.Length) throw new InvalidDataException("ZIP entry exceeds its declared size.");
            destination.Write(buffer, 0, read);
        }
        if (written != entry.Length) throw new InvalidDataException("Truncated ZIP entry.");
    }
}
