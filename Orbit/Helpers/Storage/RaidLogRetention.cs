#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;

namespace RaidDiagnostics;

// Identical source is shipped in ORBIT and COMET. No Unity calls or raid telemetry changes.
internal sealed class RaidLogRetention
{
    internal sealed class Options
    {
        public int KeepRaids = 20;
        public long MaxBytes = 5_000_000_000L;
    }
    internal sealed class Entry
    {
        public string Path = "", Sha256 = "";
        public long Bytes;
    }
    internal sealed class Manifest
    {
        public int Schema = 1;
        public string Mod = "", Id = "";
        public DateTime StartedUtc;
        public List<Entry> Files = new();
    }
    internal sealed class Group
    {
        public string Id = "";
        public DateTime StartedUtc, LastWriteUtc;
        public readonly List<string> Files = new();
        public string? Archive;
        public bool Pinned;
    }
    internal sealed class Report
    {
        public long BeforeBytes, AfterBytes, LimitBytes;
        public int Raids, KeptRaids, ArchivedRaids, RemovedOriginals, PurgedArchives;
        public string[] KeptIds = Array.Empty<string>();
        public readonly List<string> Warnings = new();
        public bool OverBudget => AfterBytes > LimitBytes;
    }
    private static readonly Regex LogName = new(@"^(?:performance-)?(?<date>\d{8}-\d{6})-(?<id>[a-fA-F0-9]{32})(?:\.part\d{4,})?\.jsonl$", RegexOptions.CultureInvariant);
    private static readonly Regex CaptureName = new(@"^capture-(?<date>\d{8}-\d{6})-\d{3}-[a-fA-F0-9]{32}\.json$", RegexOptions.CultureInvariant);
    private readonly string root, mod;
    private readonly Options options;
    private readonly Action<string>? progress;
    internal RaidLogRetention(string root, string mod, Options? options = null, Action<string>? progress = null)
    {
        this.root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (mod != "COMET" && mod != "ORBIT") throw new ArgumentException("Unknown mod");
        this.mod = mod; this.options = options ?? new Options(); this.progress = progress;
        if (this.options.KeepRaids < 1 || this.options.MaxBytes < 1) throw new ArgumentException("Invalid retention settings");
        CheckPath(this.root, true);
    }
    private string Full(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) throw new IOException("Invalid relative path");
        string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        CheckPath(path, false); return path;
    }
    private void CheckPath(string path, bool allowRoot)
    {
        if (!(allowRoot && path.Equals(root, StringComparison.OrdinalIgnoreCase))
            && !path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Path leaves the diagnostic folder");
        // Check every parent, including ancestors of the supplied root, before any mutation.
        for (string? at = path; at != null; at = Path.GetDirectoryName(at))
            if ((File.Exists(at) || Directory.Exists(at)) && (File.GetAttributes(at) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked diagnostic paths are not managed");
    }
    private string Relative(string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
    private IEnumerable<string> Files(string relative, string pattern)
    {
        string path = Full(relative);
        if (!Directory.Exists(path)) return Array.Empty<string>();
        return Directory.GetFiles(path, pattern, SearchOption.TopDirectoryOnly)
            .Where(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0);
    }
    private static DateTime Stamp(string value) => DateTime.ParseExact(value, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    private static bool Id(string value) => Guid.TryParseExact(value, "N", out _);
    private bool OwnedRaw(string path)
    {
        string normalized = path.Replace('\\', '/');
        var parts = normalized.Split('/');
        if (parts.Length == 2 && parts[0] == "diagnostics")
            return LogName.IsMatch(parts[1]) || mod == "ORBIT" && CaptureName.IsMatch(parts[1]);
        if (parts.Length == 2 && parts[0] == "performance" && mod == "COMET") return LogName.IsMatch(parts[1]);
        return mod == "ORBIT" && parts.Length == 3 && parts[0] == "diagnostics" && parts[1] == "raids"
            && parts[2].EndsWith(".json", StringComparison.Ordinal) && Id(Path.GetFileNameWithoutExtension(parts[2]));
    }
    private void Add(Group group, string path)
    {
        if (!File.Exists(path)) return;
        CheckPath(path, false);
        if (!group.Files.Contains(path, StringComparer.OrdinalIgnoreCase)) group.Files.Add(path);
        group.LastWriteUtc = group.LastWriteUtc > File.GetLastWriteTimeUtc(path) ? group.LastWriteUtc : File.GetLastWriteTimeUtc(path);
        group.Pinned |= File.Exists(path + ".keep");
    }
    internal List<Group> Discover(Report report)
    {
        var groups = new Dictionary<string, Group>(StringComparer.OrdinalIgnoreCase);
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (mod == "ORBIT")
            foreach (string index in Files("diagnostics/raids", "*.json"))
                try
                {
                    var record = JsonConvert.DeserializeObject<Manifest>(File.ReadAllText(index));
                    if (record == null || record.Schema != 1 || record.Mod != mod || !Id(record.Id)
                        || Path.GetFileNameWithoutExtension(index) != record.Id) continue;
                    var group = new Group { Id = record.Id, StartedUtc = record.StartedUtc };
                    var paths = record.Files.Select(f => OwnedRaw(f.Path) ? Full(f.Path) : throw new IOException("Invalid raid index path")).ToArray();
                    if (paths.Any(p => claimed.Contains(p))) throw new IOException("Overlapping raid indexes");
                    foreach (string path in paths) { Add(group, path); claimed.Add(path); }
                    var end = group.LastWriteUtc;
                    Add(group, index); group.LastWriteUtc = end; // Index maintenance must not extend the raid's log window.
                    claimed.Add(index); groups.Add(group.Id, group);
                }
                catch (Exception e) { report.Warnings.Add("Index retained: " + Path.GetFileName(index) + ": " + e.Message); }
        foreach (string folder in mod == "COMET" ? new[] { "diagnostics", "performance" } : new[] { "diagnostics" })
            foreach (string path in Files(folder, "*.jsonl"))
            {
                if (claimed.Contains(path)) continue;
                var match = LogName.Match(Path.GetFileName(path)); if (!match.Success) continue;
                string id = match.Groups["id"].Value;
                if (!groups.TryGetValue(id, out var group)) groups.Add(id, group = new Group { Id = id, StartedUtc = Stamp(match.Groups["date"].Value) });
                Add(group, path);
            }
        // Legacy ORBIT captures lack a shared raid ID. Attach only unambiguous files
        // inside one journal's time window; uncertain captures remain untouched.
        if (mod == "ORBIT")
            foreach (string capture in Files("diagnostics", "capture-*.json"))
            {
                if (claimed.Contains(capture)) continue;
                var match = CaptureName.Match(Path.GetFileName(capture)); if (!match.Success) continue;
                var at = Stamp(match.Groups["date"].Value);
                var candidates = groups.Values.Where(g => at >= g.StartedUtc.AddMinutes(-1) && at <= g.LastWriteUtc).Take(2).ToArray();
                if (candidates.Length == 1) Add(candidates[0], capture);
            }
        foreach (string path in Files("archives", "raid-*.zip"))
            try
            {
                using var zip = ZipFile.OpenRead(path); var record = ReadManifest(zip);
                if (!groups.TryGetValue(record.Id, out var group)) groups.Add(record.Id, group = new Group { Id = record.Id, StartedUtc = record.StartedUtc });
                if (group.Archive != null) throw new IOException("Duplicate archive identity");
                group.Archive = path; group.Pinned |= File.Exists(path + ".keep");
            }
            catch (Exception e) { report.Warnings.Add("Archive retained: " + Path.GetFileName(path) + ": " + e.Message); }
        return groups.Values.OrderByDescending(g => g.StartedUtc).ThenBy(g => g.Id, StringComparer.Ordinal).ToList();
    }
    private Manifest ReadManifest(ZipArchive zip)
    {
        var entry = zip.GetEntry("retention.json") ?? throw new IOException("Missing retention manifest");
        if (entry.Length > 1024 * 1024) throw new IOException("Oversized retention manifest");
        using var reader = new StreamReader(entry.Open());
        var record = JsonConvert.DeserializeObject<Manifest>(reader.ReadToEnd());
        if (record == null || record.Schema != 1 || record.Mod != mod || !Id(record.Id) || record.Files.Count == 0
            || record.Files.Any(f => !OwnedRaw(f.Path) || f.Bytes < 0 || !Regex.IsMatch(f.Sha256, "^[A-F0-9]{64}$"))
            || record.Files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != record.Files.Count)
            throw new IOException("Invalid retention manifest");
        foreach (var file in record.Files) Full(file.Path);
        return record;
    }
    private static string Transfer(Stream source, Stream? destination, CancellationToken cancel)
    {
        using var sha = SHA256.Create(); var buffer = new byte[64 * 1024]; int length;
        while ((length = source.Read(buffer, 0, buffer.Length)) != 0)
        {
            cancel.ThrowIfCancellationRequested(); destination?.Write(buffer, 0, length);
            sha.TransformBlock(buffer, 0, length, buffer, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return BitConverter.ToString(sha.Hash!).Replace("-", "");
    }
    private Manifest Verify(string archive, CancellationToken cancel)
    {
        CheckPath(archive, false);
        using var zip = ZipFile.OpenRead(archive); var record = ReadManifest(zip);
        if (zip.Entries.Count != record.Files.Count + 1) throw new IOException("Unexpected archive entries");
        foreach (var file in record.Files)
        {
            cancel.ThrowIfCancellationRequested();
            var entry = zip.GetEntry(file.Path) ?? throw new IOException("Missing archive entry");
            using var stream = entry.Open();
            if (entry.Length != file.Bytes || Transfer(stream, null, cancel) != file.Sha256) throw new IOException("Archive verification failed");
        }
        return record;
    }
    private void Archive(Group group, Report report, CancellationToken cancel)
    {
        var opened = new List<(string Path, FileStream Stream)>(); string? temporary = null;
        try
        {
            foreach (string path in group.Files)
            {
                CheckPath(path, false);
                if (File.Exists(path + ".keep")) return;
                // Reject active writers, retain read access for observers, and hold
                // these handles until deletion so a writer cannot race verification.
                opened.Add((path, new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete)));
            }
            string archive;
            if (group.Archive != null)
            {
                archive = group.Archive;
                var existing = Verify(archive, cancel);
                foreach (var file in opened)
                {
                    var entry = existing.Files.SingleOrDefault(e => e.Path == Relative(file.Path));
                    if (entry == null || entry.Bytes != file.Stream.Length || entry.Sha256 != Transfer(file.Stream, null, cancel))
                        throw new IOException("Existing archive differs from remaining originals");
                }
            }
            else
            {
                var record = new Manifest { Mod = mod, Id = group.Id, StartedUtc = group.StartedUtc };
                string directory = Full("archives"); Directory.CreateDirectory(directory);
                archive = Full("archives/raid-" + group.StartedUtc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + group.Id + ".zip");
                temporary = archive + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    foreach (var file in opened)
                    {
                        cancel.ThrowIfCancellationRequested();
                        var entry = new Entry { Path = Relative(file.Path), Bytes = file.Stream.Length };
                        using (var output = zip.CreateEntry(entry.Path, CompressionLevel.Optimal).Open())
                            entry.Sha256 = Transfer(file.Stream, output, cancel);
                        record.Files.Add(entry);
                    }
                    using var writer = new StreamWriter(zip.CreateEntry("retention.json").Open(), new UTF8Encoding(false));
                    writer.Write(JsonConvert.SerializeObject(record));
                }
                Verify(temporary, cancel);
                cancel.ThrowIfCancellationRequested();
                File.Move(temporary, archive); temporary = null;
                report.ArchivedRaids++;
            }
            cancel.ThrowIfCancellationRequested();
            if (File.Exists(archive + ".keep") || opened.Any(f => File.Exists(f.Path + ".keep"))) return;
            foreach (var file in opened) { CheckPath(file.Path, false); File.Delete(file.Path); report.RemovedOriginals++; }
            progress?.Invoke("Archived raid " + group.Id);
        }
        finally
        {
            foreach (var file in opened) file.Stream.Dispose();
            if (temporary != null && File.Exists(temporary)) { CheckPath(temporary, false); File.Delete(temporary); }
        }
    }
    private long Size()
    {
        long bytes = 0; var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count != 0)
        {
            var directory = pending.Pop(); if (!Directory.Exists(directory)) continue;
            CheckPath(directory, true);
            foreach (string item in Directory.GetFileSystemEntries(directory))
            {
                var attr = File.GetAttributes(item); if ((attr & FileAttributes.ReparsePoint) != 0) continue;
                if ((attr & FileAttributes.Directory) != 0)
                {
                    // User-managed comparisons are outside both deletion and quota.
                    if (!Path.GetFileName(item).Equals("protected", StringComparison.OrdinalIgnoreCase)) pending.Push(item);
                }
                else bytes += new FileInfo(item).Length;
            }
        }
        return bytes;
    }
    internal Report Plan()
    {
        var report = new Report { BeforeBytes = Size(), LimitBytes = options.MaxBytes };
        var groups = Discover(report); report.Raids = groups.Count; report.KeptRaids = Math.Min(options.KeepRaids, groups.Count);
        report.KeptIds = groups.Take(options.KeepRaids).Select(g => g.Id).ToArray();
        report.AfterBytes = report.BeforeBytes; return report;
    }
    internal Report Run(CancellationToken cancel = default)
    {
        var report = Plan(); if (!Directory.Exists(root)) return report;
        string lockPath = Full(".retention.lock");
        using var exclusive = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var groups = Discover(report);
        foreach (var group in groups.Skip(options.KeepRaids).Where(g => !g.Pinned && g.Files.Count != 0))
        {
            cancel.ThrowIfCancellationRequested();
            try { Archive(group, report, cancel); }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { report.Warnings.Add("Raid retained: " + group.Id + ": " + e.Message); }
        }
        report.AfterBytes = Size(); groups = Discover(report);
        foreach (var group in groups.Skip(options.KeepRaids).Reverse())
        {
            cancel.ThrowIfCancellationRequested();
            if (report.AfterBytes <= options.MaxBytes) break;
            if (group.Pinned || group.Archive == null || group.Files.Count != 0) continue;
            try
            {
                Verify(group.Archive, cancel); cancel.ThrowIfCancellationRequested();
                if (File.Exists(group.Archive + ".keep")) continue;
                CheckPath(group.Archive, false); long length = new FileInfo(group.Archive).Length;
                File.Delete(group.Archive); report.AfterBytes -= length; report.PurgedArchives++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { report.Warnings.Add("Archive retained: " + group.Id + ": " + e.Message); }
        }
        report.AfterBytes = Size(); return report;
    }
}
