using System.Text;
using System.Text.Json;
using PakStudio.Core.Interfaces;

namespace PakScape.Linux.Services;

public sealed class XdgRecentFilesService : IRecentFilesService
{
    private const int MaximumRecentFiles = 10;
    private const long MaximumSettingsFileSize = 1024 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };
    private readonly string? _settingsPath;

    public XdgRecentFilesService()
    {
        _settingsPath = CreateSettingsPath(stateHomeOverride: null);
    }

    public XdgRecentFilesService(string stateHome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateHome);
        _settingsPath = CreateSettingsPath(stateHome);
    }

    private static string? CreateSettingsPath(string? stateHomeOverride)
    {
        var stateHome = stateHomeOverride ?? Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (string.IsNullOrWhiteSpace(stateHome) || !Path.IsPathFullyQualified(stateHome))
        {
            stateHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local",
                "state");
        }

        var directory = Path.Combine(stateHome, "pakscape");
        try
        {
            Directory.CreateDirectory(directory);
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(
                    directory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            return Path.Combine(directory, "recent-files.json");
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public IReadOnlyList<string> GetRecentFiles()
    {
        if (_settingsPath is null || !File.Exists(_settingsPath))
        {
            return [];
        }

        try
        {
            var json = ReadSettingsText(_settingsPath);
            if (json is null)
            {
                return [];
            }

            var files = JsonSerializer.Deserialize<List<string>>(json);
            return files?
                .Where(path => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path))
                .Distinct(StringComparer.Ordinal)
                .Take(MaximumRecentFiles)
                .ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
        catch (DecoderFallbackException)
        {
            return [];
        }
    }

    public void Add(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (_settingsPath is null)
        {
            return;
        }

        var fullPath = Path.GetFullPath(path);
        var files = GetRecentFiles()
            .Where(existing => !string.Equals(existing, fullPath, StringComparison.Ordinal))
            .Prepend(fullPath)
            .Take(MaximumRecentFiles)
            .ToList();

        var json = JsonSerializer.Serialize(files, SerializerOptions);
        var directory = Path.GetDirectoryName(_settingsPath)
            ?? throw new InvalidOperationException("The recent-file path has no parent directory.");
        var temporaryPath = Path.Combine(directory, $".recent-files.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, _settingsPath, overwrite: true);
        }
        catch (IOException)
        {
            // Recent files are non-critical; archive operations must still succeed.
        }
        catch (UnauthorizedAccessException)
        {
            // A read-only state directory disables persistence for this update.
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup of an uncommitted state file.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort cleanup.
            }
        }
    }

    private static string? ReadSettingsText(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length < 0 || stream.Length > MaximumSettingsFileSize)
        {
            return null;
        }

        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1)
        {
            return null;
        }
        return new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true).GetString(bytes);
    }
}
