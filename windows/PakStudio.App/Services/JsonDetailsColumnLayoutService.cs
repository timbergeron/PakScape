using System.IO;
using System.Text;
using System.Text.Json;

namespace PakStudio.App.Services;

public sealed class JsonDetailsColumnLayoutService : IDetailsColumnLayoutService
{
    private const int MaximumColumns = 16;
    private const long MaximumSettingsFileSize = 64 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };
    private readonly string? _settingsPath;

    public JsonDetailsColumnLayoutService()
    {
        _settingsPath = CreateSettingsPath();
    }

    public IReadOnlyList<DetailsColumnState> Load()
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

            var columns = JsonSerializer.Deserialize<List<DetailsColumnState>>(json);
            return columns?
                .Where(column =>
                    column is not null &&
                    !string.IsNullOrWhiteSpace(column.Key) &&
                    double.IsFinite(column.Weight) &&
                    column.Weight > 0)
                .DistinctBy(column => column.Key, StringComparer.OrdinalIgnoreCase)
                .Take(MaximumColumns)
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

    public void Save(IReadOnlyList<DetailsColumnState> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        var directory = Path.GetDirectoryName(_settingsPath);
        if (_settingsPath is null || directory is null)
        {
            return;
        }

        var json = JsonSerializer.Serialize(columns.Take(MaximumColumns).ToList(), SerializerOptions);
        var temporaryPath = Path.Combine(directory, $".details-columns.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);
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
            // Column layout is non-critical; archive operations must still succeed.
        }
        catch (UnauthorizedAccessException)
        {
            // A read-only settings directory disables persistence for this update.
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

    private static string? CreateSettingsPath()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(appData))
            {
                return null;
            }

            var directory = Path.Combine(appData, "PakScape");
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, "details-columns.json");
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
