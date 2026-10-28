using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Orbit.Debugging;
using WebSocketSharp;
using WebSocketSharp.Net;

namespace Orbit.Helpers;

// Only receives notifications. A healthy connection has no ORBIT timer or application heartbeat.
internal sealed class DebugRelayListener : IDisposable
{
    private readonly CancellationTokenSource _cancel = new();
    private DebugRelayReply _pending;
    internal readonly Task Completion;

    internal DebugRelayListener(Uri backend, string profile, DebugRaidLink raid, bool final, Action<string> warning)
    {
        var url = new UriBuilder(backend) { Scheme = backend.Scheme == "https" ? "wss" : "ws" }.Uri;
        var subscribe = JsonConvert.SerializeObject(new DebugRelayRequest { Operation = "subscribe", Raid = raid, Final = final });
        Completion = Task.Run(() => Listen(url, profile, subscribe, warning));
    }

    internal DebugRelayReply TakeRequest() => Interlocked.Exchange(ref _pending, null);

    private async Task Listen(Uri url, string profile, string subscribe, Action<string> warning)
    {
        var retrySeconds = 1;
        var reported = false;
        var failure = "Connection closed.";
        var cancel = _cancel.Token;
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                var ended = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var subscribed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (var socket = new WebSocket(url.AbsoluteUri) { WaitTime = TimeSpan.FromSeconds(10) })
                {
                    // SPT uses a self-signed backend certificate; scope this to this socket only,
                    // matching SPT.Common.Http.Client rather than changing global TLS validation.
                    if (url.Scheme == "wss")
                    {
                        socket.SslConfiguration.EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12;
                        socket.SslConfiguration.ServerCertificateValidationCallback = (_, _, _, _) => true;
                    }
                    socket.SetCookie(new Cookie("PHPSESSID", profile));
                    socket.Log.Output = (_, _) => { }; // Report once per outage through ORBIT, not on every reconnect.
                    socket.Log.Level = LogLevel.Fatal;
                    socket.OnOpen += (_, _) => socket.Send(subscribe);
                    socket.OnClose += (_, _) => ended.TrySetResult(true);
                    socket.OnError += (_, error) =>
                    { failure = error.Exception?.GetBaseException().Message ?? error.Message; ended.TrySetResult(true); };
                    socket.OnMessage += (_, message) =>
                    {
                        if (!message.IsText || message.RawData.Length > 4096) return;
                        try
                        {
                            var reply = JsonConvert.DeserializeObject<DebugRelayReply>(message.Data);
                            if (reply?.Protocol != 1) return;
                            if (reply.State == "subscribed") subscribed.TrySetResult(true);
                            if (reply.State == "pending" && DebugRelayProtocol.Id(reply.Ticket, 32))
                                Interlocked.Exchange(ref _pending, reply);
                        }
                        catch (JsonException) { }
                    };
                    using (cancel.Register(() => ended.TrySetResult(true)))
                    {
                        try
                        {
                            socket.Connect();
                            if (socket.ReadyState == WebSocketState.Open)
                            {
                                var handshake = await Task.WhenAny(subscribed.Task, ended.Task, Task.Delay(10000, cancel)).ConfigureAwait(false);
                                if (handshake == subscribed.Task)
                                {
                                    retrySeconds = 1; reported = false;
                                    await ended.Task.ConfigureAwait(false);
                                }
                            }
                        }
                        catch (Exception) { }
                        finally { socket.Close(); }
                    }
                }
                cancel.ThrowIfCancellationRequested();
                if (!reported)
                {
                    reported = true;
                    warning?.Invoke("DEBUG ZIP notification connection unavailable; reconnecting. " + failure
                        + " Check matching ORBIT client/server versions.");
                }
                await Task.Delay(TimeSpan.FromSeconds(retrySeconds), cancel).ConfigureAwait(false);
                retrySeconds = Math.Min(60, retrySeconds * 2);
            }
        }
        catch (OperationCanceledException) { }
        finally { _cancel.Dispose(); }
    }

    public void Dispose()
    {
        try { _cancel.Cancel(); } catch (ObjectDisposedException) { }
    }
}
