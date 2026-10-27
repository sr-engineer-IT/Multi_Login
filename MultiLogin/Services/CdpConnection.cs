using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MultiLogin.Services;

public class CdpException(string message) : Exception(message);

/// <summary>Minimal Chrome DevTools Protocol client over a browser-level WebSocket (flat sessions).</summary>
public sealed class CdpConnection : IAsyncDisposable
{
    // CDP rejects explicit nulls for optional parameters; they must be left out.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ClientWebSocket _ws = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private int _nextId;

    /// <summary>Raised on the receive thread with (method, params, sessionId).</summary>
    public event Action<string, JsonElement, string?>? EventReceived;

    public static async Task<CdpConnection> ConnectAsync(Uri endpoint, CancellationToken ct)
    {
        var conn = new CdpConnection();
        conn._ws.Options.KeepAliveInterval = TimeSpan.Zero;
        await conn._ws.ConnectAsync(endpoint, ct);
        _ = conn.ReceiveLoopAsync();
        return conn;
    }

    public async Task<JsonElement> SendAsync(string method, object? parameters = null, string? sessionId = null)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var msg = new JsonObject
        {
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters == null ? new JsonObject() : JsonSerializer.SerializeToNode(parameters, JsonOptions)
        };
        if (sessionId != null) msg["sessionId"] = sessionId;
        var bytes = Encoding.UTF8.GetBytes(msg.ToJsonString());

        await _sendLock.WaitAsync();
        try { await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, _cts.Token); }
        catch { _pending.TryRemove(id, out var _); throw; }
        finally { _sendLock.Release(); }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var registration = timeout.Token.Register(() => tcs.TrySetException(new CdpException($"{method} timed out.")));
        return await tcs.Task;
    }

    /// <summary>Receives a line for each command that failed in <see cref="TrySendAsync"/>.</summary>
    public static Action<string>? DiagnosticLog { get; set; }

    /// <summary>Sends a command and ignores failures (unsupported in this target type, target gone, ...).</summary>
    public async Task TrySendAsync(string method, object? parameters = null, string? sessionId = null)
    {
        try { await SendAsync(method, parameters, sessionId); }
        catch (Exception ex) when (ex is CdpException or WebSocketException or OperationCanceledException)
        {
            DiagnosticLog?.Invoke($"{method}: {ex.Message}");
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (_ws.State == WebSocketState.Open)
            {
                var result = await _ws.ReceiveAsync(buffer, _cts.Token);
                if (result.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;

                using (var doc = JsonDocument.Parse(message.ToArray()))
                    Dispatch(doc.RootElement);
                message.SetLength(0);
            }
        }
        catch { }
        finally
        {
            foreach (var tcs in _pending.Values)
                tcs.TrySetException(new CdpException("Browser connection closed."));
            _pending.Clear();
        }
    }

    private void Dispatch(JsonElement root)
    {
        if (root.TryGetProperty("id", out var idEl))
        {
            if (!_pending.TryRemove(idEl.GetInt32(), out var tcs)) return;
            if (root.TryGetProperty("error", out var err))
                tcs.TrySetException(new CdpException(err.GetProperty("message").GetString() ?? "CDP error"));
            else
                tcs.TrySetResult(root.GetProperty("result").Clone());
            return;
        }

        if (root.TryGetProperty("method", out var method))
        {
            var sessionId = root.TryGetProperty("sessionId", out var s) ? s.GetString() : null;
            var p = root.TryGetProperty("params", out var pe) ? pe.Clone() : default;
            try { EventReceived?.Invoke(method.GetString()!, p, sessionId); } catch { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        }
        catch { }
        _ws.Dispose();
    }
}
