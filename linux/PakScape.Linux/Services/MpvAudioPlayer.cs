using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;

namespace PakScape.Linux.Services;

internal sealed class MpvAudioPlayer : IDisposable
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(2);

    private readonly Process _process;
    private readonly string _socketPath;
    private readonly Socket _socket;
    private readonly NetworkStream _stream;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private int _requestId;
    private bool _disposed;

    private MpvAudioPlayer(Process process, string socketPath, Socket socket)
    {
        _process = process;
        _socketPath = socketPath;
        _socket = socket;
        _stream = new NetworkStream(socket, ownsSocket: false);
        _reader = new StreamReader(_stream);
        _writer = new StreamWriter(_stream) { AutoFlush = true };
    }

    public double DurationSeconds { get; private set; }

    public bool HasExited => _process.HasExited;

    public static async Task<MpvAudioPlayer> StartAsync(
        string mediaPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaPath);
        if (!Path.IsPathFullyQualified(mediaPath) || !File.Exists(mediaPath))
        {
            throw new FileNotFoundException("The audio preview file does not exist.", mediaPath);
        }

        var socketPath = Path.Combine(
            Path.GetTempPath(),
            $"pakscape-mpv-{Guid.NewGuid():N}.sock");
        Process? process = null;
        Socket? socket = null;
        MpvAudioPlayer? player = null;
        try
        {
            process = Process.Start(CreateStartInfo(mediaPath, socketPath))
                ?? throw new InvalidOperationException("Could not start mpv.");

            using var startupCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startupCancellation.CancelAfter(StartupTimeout);
            while (!File.Exists(socketPath))
            {
                if (process.HasExited)
                {
                    var detail = await process.StandardError.ReadToEndAsync(cancellationToken);
                    throw new InvalidOperationException(
                        string.IsNullOrWhiteSpace(detail)
                            ? "mpv exited before audio playback was ready."
                            : $"mpv could not open this audio file: {detail.Trim()}");
                }

                await Task.Delay(40, startupCancellation.Token);
            }

            socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(
                new UnixDomainSocketEndPoint(socketPath),
                startupCancellation.Token);
            player = new MpvAudioPlayer(process, socketPath, socket);
            process = null;
            socket = null;
            await player.WaitUntilReadyAsync(startupCancellation.Token);
            return player;
        }
        catch (Exception exception)
        {
            player?.Dispose();
            socket?.Dispose();
            StopProcess(process);
            TryDeleteSocket(socketPath);
            if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new InvalidOperationException(
                "Native audio preview requires the mpv media player and a codec that supports this file.",
                exception);
        }
    }

    public Task<double> GetPositionAsync(CancellationToken cancellationToken = default) =>
        GetDoublePropertyAsync("time-pos", cancellationToken);

    public async Task SetPausedAsync(
        bool paused,
        CancellationToken cancellationToken = default)
    {
        _ = await SendCommandAsync(
            ["set_property", "pause", paused],
            cancellationToken);
    }

    public async Task SeekAsync(
        double seconds,
        CancellationToken cancellationToken = default)
    {
        _ = await SendCommandAsync(
            ["set_property", "time-pos", Math.Clamp(seconds, 0, DurationSeconds)],
            cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _writer.Dispose();
        _reader.Dispose();
        _stream.Dispose();
        _socket.Dispose();
        StopProcess(_process);
        _commandLock.Dispose();
        TryDeleteSocket(_socketPath);
    }

    private static ProcessStartInfo CreateStartInfo(string mediaPath, string socketPath)
    {
        var startInfo = new ProcessStartInfo("mpv")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("--no-config");
        startInfo.ArgumentList.Add("--no-video");
        startInfo.ArgumentList.Add("--audio-display=no");
        startInfo.ArgumentList.Add("--terminal=no");
        startInfo.ArgumentList.Add("--really-quiet");
        startInfo.ArgumentList.Add("--pause=yes");
        startInfo.ArgumentList.Add("--keep-open=yes");
        startInfo.ArgumentList.Add("--volume=80");
        startInfo.ArgumentList.Add($"--input-ipc-server={socketPath}");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(mediaPath);
        return startInfo;
    }

    private async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var duration = await GetDoublePropertyAsync("duration", cancellationToken);
            if (duration > 0)
            {
                DurationSeconds = duration;
                return;
            }

            if (HasExited)
            {
                throw new InvalidOperationException("mpv exited before audio playback was ready.");
            }

            await Task.Delay(80, cancellationToken);
        }
    }

    private async Task<double> GetDoublePropertyAsync(
        string propertyName,
        CancellationToken cancellationToken)
    {
        var data = await SendCommandAsync(
            ["get_property", propertyName],
            cancellationToken);
        return data.ValueKind == JsonValueKind.Number && data.TryGetDouble(out var value)
            ? value
            : 0;
    }

    private async Task<JsonElement> SendCommandAsync(
        object[] command,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var commandCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        commandCancellation.CancelAfter(CommandTimeout);
        await _commandLock.WaitAsync(commandCancellation.Token);
        try
        {
            var requestId = Interlocked.Increment(ref _requestId);
            var request = JsonSerializer.Serialize(new
            {
                command,
                request_id = requestId,
            });
            await _writer.WriteLineAsync(
                request.AsMemory(),
                commandCancellation.Token);

            while (true)
            {
                var line = await _reader.ReadLineAsync(commandCancellation.Token)
                    ?? throw new EndOfStreamException("mpv closed its control channel.");
                using var response = JsonDocument.Parse(line);
                var root = response.RootElement;
                if (!root.TryGetProperty("request_id", out var responseId) ||
                    responseId.GetInt32() != requestId)
                {
                    continue;
                }

                var error = root.TryGetProperty("error", out var errorElement)
                    ? errorElement.GetString()
                    : null;
                if (!string.Equals(error, "success", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"mpv rejected the audio command: {error ?? "unknown error"}.");
                }

                return root.TryGetProperty("data", out var data)
                    ? data.Clone()
                    : default;
            }
        }
        finally
        {
            _commandLock.Release();
        }
    }

    private static void StopProcess(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // The private playback process may already be exiting.
        }
        finally
        {
            process.Dispose();
        }
    }

    private static void TryDeleteSocket(string socketPath)
    {
        try
        {
            File.Delete(socketPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Unix socket cleanup is best effort.
        }
    }
}
