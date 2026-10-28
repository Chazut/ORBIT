using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using Orbit.Debugging;
using Orbit.Server.Routers;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Servers.Ws;

namespace Orbit.Server.Debugging;

[Injectable(InjectionType.Singleton)]
public sealed class DebugRelaySocket(DebugRelayService relay) : IWebSocketConnectionHandler
{
    private readonly ConcurrentDictionary<WebSocket, Connection> _connections = new();
    public string GetHookUrl() => DebugRelayProtocol.SocketRoute;
    public string GetSocketId() => "ORBIT debug notifications";

    private sealed class Connection(WebSocket socket, string profile, CancellationToken requestAborted) : IDisposable
    {
        public readonly string Profile = profile;
        public readonly Channel<DebugRelayReply> Messages = Channel.CreateBounded<DebugRelayReply>(4);
        public readonly CancellationTokenSource Cancel = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        public IDisposable? Subscription;
        public Task? Writer;
        public bool Connected => !Cancel.IsCancellationRequested && socket.State == WebSocketState.Open;
        public bool Notify(DebugRelayReply message) => Connected && Messages.Writer.TryWrite(message);
        public void Stop() { Cancel.Cancel(); socket.Abort(); }
        public async Task Write()
        {
            try
            {
                await foreach (var message in Messages.Reader.ReadAllAsync(Cancel.Token))
                {
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(message, new JsonSerializerOptions { IncludeFields = true });
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Cancel.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(10));
                    await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, deadline.Token);
                }
            }
            catch (Exception e) when (e is OperationCanceledException or WebSocketException or IOException)
            { Stop(); }
        }
        public void Dispose() { Subscription?.Dispose(); Cancel.Dispose(); }
    }

    public async Task OnConnectionAsync(WebSocket ws, HttpContext context, string sessionIdContext)
    {
        var profile = context.Request.Cookies["PHPSESSID"];
        if (context.Request.Path.Value != DebugRelayProtocol.SocketRoute || !DebugRelayProtocol.Id(profile, 24))
        {
            await ws.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "Invalid debug session.", context.RequestAborted);
            return;
        }
        var connection = new Connection(ws, profile!, context.RequestAborted);
        connection.Cancel.CancelAfter(TimeSpan.FromSeconds(10));
        if (_connections.TryAdd(ws, connection)) connection.Writer = connection.Write();
        else connection.Dispose();
    }

    public async Task OnMessageAsync(byte[] rawData, WebSocketMessageType messageType, WebSocket ws, HttpContext context)
    {
        if (!_connections.TryGetValue(ws, out var connection)) return;
        try
        {
            if (messageType != WebSocketMessageType.Text || rawData.Length > 4096 || connection.Subscription != null)
                throw new InvalidDataException("Invalid debug subscription.");
            var request = JsonSerializer.Deserialize<DebugRelayRequest>(rawData, new JsonSerializerOptions { IncludeFields = true });
            if (request?.Operation != "subscribe") throw new InvalidDataException("Expected a debug subscription.");
            connection.Subscription = relay.Store.Subscribe(connection.Profile, request,
                connection.Notify, () => connection.Connected, connection.Stop);
            connection.Cancel.CancelAfter(Timeout.InfiniteTimeSpan);
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidDataException or UnauthorizedAccessException or IOException)
        {
            await ws.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "Invalid debug subscription.", context.RequestAborted);
            connection.Stop();
        }
    }

    public async Task OnCloseAsync(WebSocket ws, HttpContext context, string sessionIdContext)
    {
        if (!_connections.TryRemove(ws, out var connection)) return;
        connection.Cancel.Cancel();
        if (connection.Writer != null) await connection.Writer;
        connection.Dispose();
    }
}
