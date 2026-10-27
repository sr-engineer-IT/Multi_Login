using System.Net;
using System.Net.Sockets;
using System.Text;
using MultiLogin.Models;

namespace MultiLogin.Services;

/// <summary>
/// A local HTTP proxy on 127.0.0.1 that forwards to the profile's upstream proxy and
/// handles its authentication. Chromium can't take proxy credentials on the command
/// line and doesn't support SOCKS5 auth at all, so the browser talks to this instead.
/// </summary>
public sealed class ProxyForwarder : IDisposable
{
    private const int MaxHeaderBytes = 64 * 1024;

    private readonly ProxyType _type;
    private readonly string _host;
    private readonly int _port;
    private readonly string _user;
    private readonly string _password;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();

    public int LocalPort { get; }

    public ProxyForwarder(Profile profile)
    {
        if (!profile.HasProxy) throw new ArgumentException("Profile has no proxy configured.");
        _type = profile.ProxyType;
        _host = profile.ProxyHost.Trim();
        _port = profile.ProxyPort;
        _user = profile.ProxyUser;
        _password = profile.ProxyPassword;

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        LocalPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoopAsync();
    }

    public string LocalProxyUrl => $"http://127.0.0.1:{LocalPort}";

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { break; }
            _ = HandleClientAsync(client);
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using var _ = client;
        TcpClient? upstream = null;
        try
        {
            client.NoDelay = true;
            var clientStream = client.GetStream();
            var (header, leftover) = await ReadHeaderAsync(clientStream, _cts.Token);
            if (header == null) return;

            var lines = header.Split("\r\n");
            var requestLine = lines[0].Split(' ');
            if (requestLine.Length < 3) return;
            var method = requestLine[0];
            var target = requestLine[1];
            var version = requestLine[2];
            var isConnect = method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase);

            // Proxy-* and Connection headers are hop-by-hop; we set our own below.
            var headers = lines.Skip(1)
                .Where(l => l.Length > 0)
                .Where(l => !l.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase)
                         && !l.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase)
                         && !l.StartsWith("Keep-Alive:", StringComparison.OrdinalIgnoreCase))
                .ToList();

            upstream = new TcpClient { NoDelay = true };
            await upstream.ConnectAsync(_host, _port, _cts.Token);
            var upstreamStream = upstream.GetStream();

            if (_type == ProxyType.Http)
            {
                var sb = new StringBuilder();
                sb.Append($"{method} {target} {version}\r\n");
                foreach (var h in headers) sb.Append(h).Append("\r\n");
                if (!string.IsNullOrEmpty(_user))
                {
                    var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_user}:{_password}"));
                    sb.Append($"Proxy-Authorization: Basic {token}\r\n");
                }
                // Credentials are only injected into the first request on a connection,
                // so plain-HTTP connections must not be reused for further requests.
                if (!isConnect) sb.Append("Connection: close\r\n");
                sb.Append("\r\n");
                await upstreamStream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), _cts.Token);
            }
            else
            {
                string destHost;
                int destPort;
                string? rewritten = null;

                if (isConnect)
                {
                    (destHost, destPort) = SplitHostPort(target, 443);
                }
                else
                {
                    if (!Uri.TryCreate(target, UriKind.Absolute, out var uri))
                    {
                        await SendErrorAsync(clientStream, "400 Bad Request");
                        return;
                    }
                    destHost = uri.IdnHost;
                    destPort = uri.Port;
                    var sb = new StringBuilder();
                    sb.Append($"{method} {uri.PathAndQuery} {version}\r\n");
                    foreach (var h in headers) sb.Append(h).Append("\r\n");
                    sb.Append("Connection: close\r\n\r\n");
                    rewritten = sb.ToString();
                }

                await Socks5ConnectAsync(upstreamStream, destHost, destPort, _cts.Token);

                if (isConnect)
                    await clientStream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), _cts.Token);
                else
                    await upstreamStream.WriteAsync(Encoding.ASCII.GetBytes(rewritten!), _cts.Token);
            }

            if (leftover.Length > 0)
                await upstreamStream.WriteAsync(leftover, _cts.Token);

            await PipeAsync(clientStream, upstreamStream, _cts.Token);
        }
        catch
        {
            try { await SendErrorAsync(client.GetStream(), "502 Bad Gateway"); } catch { }
        }
        finally
        {
            upstream?.Dispose();
        }
    }

    private async Task Socks5ConnectAsync(NetworkStream s, string host, int port, CancellationToken ct)
    {
        var hasAuth = !string.IsNullOrEmpty(_user);

        // Greeting: offer user/pass auth if we have credentials, otherwise no auth.
        await s.WriteAsync(hasAuth ? new byte[] { 5, 2, 0, 2 } : new byte[] { 5, 1, 0 }, ct);
        var reply = await ReadExactAsync(s, 2, ct);
        if (reply[0] != 5) throw new IOException("Not a SOCKS5 proxy.");

        if (reply[1] == 2)
        {
            if (!hasAuth) throw new IOException("SOCKS5 proxy requires a username and password.");
            var u = Encoding.UTF8.GetBytes(_user);
            var p = Encoding.UTF8.GetBytes(_password);
            var auth = new List<byte> { 1, (byte)u.Length };
            auth.AddRange(u);
            auth.Add((byte)p.Length);
            auth.AddRange(p);
            await s.WriteAsync(auth.ToArray(), ct);
            var authReply = await ReadExactAsync(s, 2, ct);
            if (authReply[1] != 0) throw new IOException("SOCKS5 authentication failed.");
        }
        else if (reply[1] != 0)
        {
            throw new IOException("SOCKS5 proxy rejected the authentication methods.");
        }

        // CONNECT by domain name so DNS is resolved on the proxy side (no DNS leak).
        var hostBytes = Encoding.ASCII.GetBytes(host);
        var req = new List<byte> { 5, 1, 0, 3, (byte)hostBytes.Length };
        req.AddRange(hostBytes);
        req.Add((byte)(port >> 8));
        req.Add((byte)(port & 0xFF));
        await s.WriteAsync(req.ToArray(), ct);

        var head = await ReadExactAsync(s, 4, ct);
        if (head[1] != 0) throw new IOException($"SOCKS5 connect failed (code {head[1]}).");
        var addrLen = head[3] switch
        {
            1 => 4,
            4 => 16,
            3 => (await ReadExactAsync(s, 1, ct))[0],
            _ => throw new IOException("Bad SOCKS5 reply.")
        };
        await ReadExactAsync(s, addrLen + 2, ct);
    }

    private static (string host, int port) SplitHostPort(string value, int defaultPort)
    {
        if (value.StartsWith('['))
        {
            var end = value.IndexOf(']');
            var host = value[1..end];
            var rest = value[(end + 1)..];
            return (host, rest.StartsWith(':') ? int.Parse(rest[1..]) : defaultPort);
        }
        var colon = value.LastIndexOf(':');
        return colon < 0 ? (value, defaultPort) : (value[..colon], int.Parse(value[(colon + 1)..]));
    }

    private static async Task<(string? header, byte[] leftover)> ReadHeaderAsync(NetworkStream s, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        while (ms.Length < MaxHeaderBytes)
        {
            var n = await s.ReadAsync(buffer, ct);
            if (n == 0) return (null, Array.Empty<byte>());
            ms.Write(buffer, 0, n);

            var data = ms.GetBuffer();
            var end = IndexOfHeaderEnd(data, (int)ms.Length);
            if (end >= 0)
            {
                var header = Encoding.ASCII.GetString(data, 0, end);
                var leftover = data.AsSpan(end + 4, (int)ms.Length - end - 4).ToArray();
                return (header, leftover);
            }
        }
        return (null, Array.Empty<byte>());
    }

    private static int IndexOfHeaderEnd(byte[] data, int length)
    {
        for (var i = 0; i + 3 < length; i++)
            if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n')
                return i;
        return -1;
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream s, int count, CancellationToken ct)
    {
        var buf = new byte[count];
        await s.ReadExactlyAsync(buf, ct);
        return buf;
    }

    private static async Task PipeAsync(NetworkStream a, NetworkStream b, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var t1 = a.CopyToAsync(b, linked.Token);
        var t2 = b.CopyToAsync(a, linked.Token);
        await Task.WhenAny(t1, t2);
        linked.Cancel();
        try { await Task.WhenAll(t1, t2); } catch { }
    }

    private static Task SendErrorAsync(NetworkStream s, string status) =>
        s.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")).AsTask();

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _cts.Dispose();
    }
}
