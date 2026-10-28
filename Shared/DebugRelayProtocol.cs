#nullable disable
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Orbit.Debugging;

// Only raid identities and archive chunks cross this boundary, never remote file paths.
public sealed class DebugRaidLink
{
    public string Session { get; set; }
    public string Map { get; set; }
    public string Host { get; set; }
    public bool Authority { get; set; }
    public bool Headless { get; set; }
}

public sealed class DebugRelayRequest
{
    public string Operation { get; set; }
    public DebugRaidLink Raid { get; set; }
    public string Ticket { get; set; }
    public string Hash { get; set; }
    public string Data { get; set; }
    public string Error { get; set; }
    public long Offset { get; set; }
    public long Length { get; set; }
    public bool Final { get; set; }
}

public sealed class DebugRelayReply
{
    public int Protocol { get; set; } = 1;
    public string State { get; set; }
    public string Ticket { get; set; }
    public string Hash { get; set; }
    public string Data { get; set; }
    public string Error { get; set; }
    public long Offset { get; set; }
    public long Length { get; set; }
    public bool Final { get; set; }
    public bool Cached { get; set; }
}

public static class DebugRelayProtocol
{
    public const string Route = "/orbit/debug/relay";
    public const string SocketRoute = "/orbit/debug/notifications";
    // Also retained for older hosts using the HTTP relay. New hosts use socket presence.
    public static readonly TimeSpan HostTimeout = TimeSpan.FromSeconds(60);
    public const int ChunkBytes = 128 * 1024;
    public const int BytesPerSecond = 1024 * 1024;
    public const long MaxArchiveBytes = 256L * 1024 * 1024;
    public static bool Id(string value, int length) => value?.Length == length
        && value.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f');
    public static bool Valid(DebugRaidLink raid) => raid != null && Id(raid.Session, 32)
        && Id(raid.Host, 24) && !string.IsNullOrEmpty(raid.Map) && raid.Map.Length <= 100
        && raid.Map.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '@');
    public static string Key(DebugRaidLink raid)
    {
        if (!Valid(raid)) throw new ArgumentException("Invalid Fika raid identity.");
        using var sha = SHA256.Create();
        return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(raid.Session + ":" + raid.Host + ":" + raid.Map)));
    }
    public static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
}
