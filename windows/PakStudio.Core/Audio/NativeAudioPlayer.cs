using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PakStudio.Core.Audio;

/// <summary>
/// Cross-platform managed owner for PakScape's private native audio player.
/// </summary>
public sealed class NativeAudioPlayer : IDisposable
{
    private const int ErrorBufferSize = 512;
    private readonly SafeAudioPlayerHandle _handle;
    private bool _disposed;

    private NativeAudioPlayer(SafeAudioPlayerHandle handle)
    {
        _handle = handle;
    }

    public double DurationSeconds
    {
        get
        {
            ThrowIfDisposed();
            return NativeMethods.PlayerDuration(_handle);
        }
    }

    public double PositionSeconds
    {
        get
        {
            ThrowIfDisposed();
            return NativeMethods.PlayerPosition(_handle);
        }
    }

    public bool IsPlaying
    {
        get
        {
            ThrowIfDisposed();
            return NativeMethods.PlayerIsPlaying(_handle) != 0;
        }
    }

    public bool IsFinished
    {
        get
        {
            ThrowIfDisposed();
            return NativeMethods.PlayerIsFinished(_handle) != 0;
        }
    }

    public static NativeAudioPlayer Create(byte[] encodedData, string extension)
    {
        ArgumentNullException.ThrowIfNull(encodedData);
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        if (encodedData.Length == 0)
        {
            throw new ArgumentException("The audio file is empty.", nameof(encodedData));
        }

        var errorBuffer = new byte[ErrorBufferSize];
        try
        {
            var handle = NativeMethods.PlayerCreate(
                encodedData,
                (nuint)encodedData.Length,
                extension,
                errorBuffer,
                (nuint)errorBuffer.Length);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                var message = DecodeError(errorBuffer);
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(message)
                        ? "The audio decoder could not read this file."
                        : message);
            }

            return new NativeAudioPlayer(handle);
        }
        catch (DllNotFoundException exception)
        {
            throw MissingBackend(exception);
        }
        catch (EntryPointNotFoundException exception)
        {
            throw MissingBackend(exception);
        }
        catch (BadImageFormatException exception)
        {
            throw MissingBackend(exception);
        }
    }

    public void Play() => CheckResult(NativeMethods.PlayerPlay(Handle));

    public void Pause() => CheckResult(NativeMethods.PlayerPause(Handle));

    public void Seek(double seconds)
    {
        if (!double.IsFinite(seconds))
        {
            throw new ArgumentOutOfRangeException(nameof(seconds));
        }
        CheckResult(NativeMethods.PlayerSeek(Handle, seconds));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _handle.Dispose();
        GC.SuppressFinalize(this);
    }

    private SafeAudioPlayerHandle Handle
    {
        get
        {
            ThrowIfDisposed();
            return _handle;
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static void CheckResult(int result)
    {
        if (result != 0)
        {
            throw new InvalidOperationException(result switch
            {
                -3 => "The audio decoder could not continue reading this file.",
                -4 => "The system audio output is unavailable.",
                _ => "The native audio player could not complete the request.",
            });
        }
    }

    private static string DecodeError(byte[] buffer)
    {
        var length = Array.IndexOf(buffer, (byte)0);
        if (length < 0)
        {
            length = buffer.Length;
        }
        return Encoding.UTF8.GetString(buffer, 0, length);
    }

    private static InvalidOperationException MissingBackend(Exception innerException) =>
        new(
            "PakScape's native audio component is missing or incompatible. Reinstall the application.",
            innerException);

    private sealed class SafeAudioPlayerHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeAudioPlayerHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle()
        {
            NativeMethods.PlayerDestroy(handle);
            return true;
        }
    }

    private static class NativeMethods
    {
        private const string LibraryName = "pakscape_audio";

        [DllImport(LibraryName, EntryPoint = "pka_player_create", CallingConvention = CallingConvention.Cdecl)]
        internal static extern SafeAudioPlayerHandle PlayerCreate(
            [In] byte[] encodedData,
            nuint encodedSize,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string extension,
            [Out] byte[] errorMessage,
            nuint errorMessageSize);

        [DllImport(LibraryName, EntryPoint = "pka_player_destroy", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void PlayerDestroy(nint player);

        [DllImport(LibraryName, EntryPoint = "pka_player_play", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int PlayerPlay(SafeAudioPlayerHandle player);

        [DllImport(LibraryName, EntryPoint = "pka_player_pause", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int PlayerPause(SafeAudioPlayerHandle player);

        [DllImport(LibraryName, EntryPoint = "pka_player_seek", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int PlayerSeek(SafeAudioPlayerHandle player, double seconds);

        [DllImport(LibraryName, EntryPoint = "pka_player_duration", CallingConvention = CallingConvention.Cdecl)]
        internal static extern double PlayerDuration(SafeAudioPlayerHandle player);

        [DllImport(LibraryName, EntryPoint = "pka_player_position", CallingConvention = CallingConvention.Cdecl)]
        internal static extern double PlayerPosition(SafeAudioPlayerHandle player);

        [DllImport(LibraryName, EntryPoint = "pka_player_is_playing", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int PlayerIsPlaying(SafeAudioPlayerHandle player);

        [DllImport(LibraryName, EntryPoint = "pka_player_is_finished", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int PlayerIsFinished(SafeAudioPlayerHandle player);
    }
}
