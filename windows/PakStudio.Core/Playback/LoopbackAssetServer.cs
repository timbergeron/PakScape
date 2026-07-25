using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace PakStudio.Core.Playback;

/// <summary>
/// A minimal loopback HTTP server that only ever answers with assets registered for a
/// handoff. It has no document root, so there is nothing to traverse into.
/// </summary>
/// <remarks>
/// This is deliberately a raw <see cref="TcpListener"/> rather than HttpListener, which on
/// Windows needs a URL reservation for anything but a plain localhost prefix.
/// </remarks>
public sealed class LoopbackAssetServer : IDisposable
{
    private const int MaximumRequestBytes = 16 * 1024;

    public static LoopbackAssetServer Shared { get; } = new();

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly object _stateLock = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cancellation;
    private int _port;

    private sealed record Entry(byte[] Data, DateTimeOffset Expires);

    public bool IsRunning
    {
        get
        {
            lock (_stateLock)
            {
                return _listener is not null;
            }
        }
    }

    /// <summary>The port currently listening, or 0. Exposed for tests and diagnostics.</summary>
    public int BoundPort
    {
        get
        {
            lock (_stateLock)
            {
                return _port;
            }
        }
    }

    /// <summary>Registers <paramref name="assets"/> under unguessable paths and returns their URLs.</summary>
    public IReadOnlyList<Uri> Publish(IReadOnlyList<DemoPlaybackAsset> assets, TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(assets);

        PurgeExpired();
        Start();

        var port = BoundPort;
        if (port == 0)
        {
            throw new DemoPlaybackException(
                "PakScape could not open a local port to hand the demo to your browser.");
        }

        var expires = DateTimeOffset.UtcNow + lifetime;
        var urls = new List<Uri>(assets.Count);
        foreach (var asset in assets)
        {
            var path = "/" + RandomToken() + "/" + DemoPlaybackHandoff.VirtualFileName(asset.FileName);
            _entries[path] = new Entry(asset.Data, expires);
            urls.Add(new Uri(
                string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}{path}"),
                UriKind.Absolute));
        }
        return urls;
    }

    /// <summary>Drops every published asset and stops listening. Safe to call when idle.</summary>
    public void Stop()
    {
        TcpListener? stopping;
        CancellationTokenSource? cancellation;
        lock (_stateLock)
        {
            stopping = _listener;
            cancellation = _cancellation;
            _listener = null;
            _cancellation = null;
            _port = 0;
        }

        _entries.Clear();
        cancellation?.Cancel();
        stopping?.Stop();
        cancellation?.Dispose();
    }

    public void Dispose() => Stop();

    private void Start()
    {
        lock (_stateLock)
        {
            if (_listener is not null)
            {
                return;
            }

            var listener = new TcpListener(IPAddress.Loopback, 0);
            try
            {
                listener.Start();
            }
            catch (SocketException error)
            {
                throw new DemoPlaybackException(
                    "PakScape could not open a local port to hand the demo to your browser. " + error.Message);
            }

            var cancellation = new CancellationTokenSource();
            _listener = listener;
            _cancellation = cancellation;
            _port = ((IPEndPoint)listener.LocalEndpoint).Port;

            _ = Task.Run(() => AcceptLoopAsync(listener, cancellation.Token));
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client, token), CancellationToken.None);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                var request = await ReadRequestLineAsync(stream, token).ConfigureAwait(false);
                if (request is null)
                {
                    return;
                }

                var fields = request.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 2)
                {
                    await WriteAsync(stream, "400 Bad Request", null, token).ConfigureAwait(false);
                    return;
                }

                var method = fields[0].ToUpperInvariant();
                var target = fields[1];
                var queryStart = target.IndexOf('?', StringComparison.Ordinal);
                var path = queryStart >= 0 ? target[..queryStart] : target;

                if (method == "OPTIONS")
                {
                    await WriteAsync(stream, "204 No Content", null, token).ConfigureAwait(false);
                    return;
                }
                if (method is not ("GET" or "HEAD"))
                {
                    await WriteAsync(stream, "405 Method Not Allowed", null, token).ConfigureAwait(false);
                    return;
                }
                if (!_entries.TryGetValue(path, out var entry) || entry.Expires <= DateTimeOffset.UtcNow)
                {
                    await WriteAsync(stream, "404 Not Found", null, token).ConfigureAwait(false);
                    return;
                }

                await WriteAsync(
                    stream,
                    "200 OK",
                    method == "HEAD" ? null : entry.Data,
                    token,
                    entry.Data.Length).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // The browser hung up; nothing to recover.
            }
            catch (OperationCanceledException)
            {
                // The handoff was stopped while a request was in flight.
            }
            catch (SocketException)
            {
                // Same.
            }
        }
    }

    /// <summary>Reads just far enough to see the request line and its headers.</summary>
    private static async Task<string?> ReadRequestLineAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(MaximumRequestBytes);
        try
        {
            var used = 0;
            while (used < MaximumRequestBytes)
            {
                var read = await stream
                    .ReadAsync(buffer.AsMemory(used, MaximumRequestBytes - used), token)
                    .ConfigureAwait(false);
                if (read <= 0)
                {
                    return null;
                }
                used += read;

                var text = Encoding.Latin1.GetString(buffer, 0, used);
                var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (headerEnd < 0)
                {
                    continue;
                }

                var lineEnd = text.IndexOf("\r\n", StringComparison.Ordinal);
                return lineEnd < 0 ? null : text[..lineEnd];
            }
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task WriteAsync(
        NetworkStream stream,
        string status,
        byte[]? body,
        CancellationToken token,
        int? contentLength = null)
    {
        var header = new StringBuilder();
        header.Append("HTTP/1.1 ").Append(status).Append("\r\n");
        header.Append("Access-Control-Allow-Origin: ").Append(DemoPlaybackHandoff.PlayerOrigin).Append("\r\n");
        header.Append("Access-Control-Allow-Methods: GET, HEAD, OPTIONS\r\n");
        header.Append("Cache-Control: no-store\r\n");
        header.Append("X-Content-Type-Options: nosniff\r\n");
        if (contentLength is not null)
        {
            header.Append("Content-Type: application/octet-stream\r\n");
        }
        header.Append("Content-Length: ")
            .Append((contentLength ?? body?.Length ?? 0).ToString(CultureInfo.InvariantCulture))
            .Append("\r\n");
        header.Append("Connection: close\r\n\r\n");

        await stream.WriteAsync(Encoding.Latin1.GetBytes(header.ToString()), token).ConfigureAwait(false);
        if (body is not null)
        {
            await stream.WriteAsync(body, token).ConfigureAwait(false);
        }
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    private void PurgeExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _entries)
        {
            if (pair.Value.Expires <= now)
            {
                _entries.TryRemove(pair.Key, out _);
            }
        }
    }

    private static string RandomToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
}
