using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PakStudio.App.Commands;
using PakStudio.Core.Documents;

namespace PakStudio.App.Services;

public enum AppearancePreference
{
    Automatic,
    Light,
    Dark,
}

public enum DefaultSortPreference
{
    Name,
    Type,
    Size,
}

/// <summary>
/// User preferences shared by every PakScape window, mirroring the macOS settings pane.
/// Values persist to %LocalAppData%\PakScape\settings.json as they change.
/// </summary>
public sealed class PakScapeSettings : ObservableObject
{
    public const double MinimumTextSize = 11;
    public const double MaximumTextSize = 18;
    public const double DefaultTextSize = 13;

    private const long MaximumSettingsFileSize = 1024 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly Lazy<PakScapeSettings> Lazy = new(Load, isThreadSafe: true);

    private readonly string? _settingsPath;
    private bool _isLoading;

    private AppearancePreference _appearance = AppearancePreference.Automatic;
    private ArchiveViewMode _defaultView = ArchiveViewMode.Details;
    private DefaultSortPreference _defaultSort = DefaultSortPreference.Name;
    private bool _defaultSortAscending = true;
    private double _textSize = DefaultTextSize;
    private bool _confirmDeletion = true;
    private bool _confirmOverwrite = true;
    private bool _backupBeforeSave;
    private bool _quickPreviewOnSelection;
    private bool _animateModels = true;
    private bool _showBspMarkers;
    private bool _explorerActionsEnabled = true;

    private PakScapeSettings()
    {
        _settingsPath = CreateSettingsPath();
    }

    public static PakScapeSettings Current => Lazy.Value;

    public AppearancePreference Appearance
    {
        get => _appearance;
        set => SetPersistedProperty(ref _appearance, value);
    }

    public ArchiveViewMode DefaultView
    {
        get => _defaultView;
        set => SetPersistedProperty(ref _defaultView, value);
    }

    public DefaultSortPreference DefaultSort
    {
        get => _defaultSort;
        set => SetPersistedProperty(ref _defaultSort, value);
    }

    public bool DefaultSortAscending
    {
        get => _defaultSortAscending;
        set => SetPersistedProperty(ref _defaultSortAscending, value);
    }

    /// <summary>Base font size, in points, applied to every PakScape window.</summary>
    public double TextSize
    {
        get => _textSize;
        set => SetPersistedProperty(ref _textSize, Math.Clamp(Math.Round(value), MinimumTextSize, MaximumTextSize));
    }

    public bool ConfirmDeletion
    {
        get => _confirmDeletion;
        set => SetPersistedProperty(ref _confirmDeletion, value);
    }

    public bool ConfirmOverwrite
    {
        get => _confirmOverwrite;
        set => SetPersistedProperty(ref _confirmOverwrite, value);
    }

    public bool BackupBeforeSave
    {
        get => _backupBeforeSave;
        set => SetPersistedProperty(ref _backupBeforeSave, value);
    }

    public bool QuickPreviewOnSelection
    {
        get => _quickPreviewOnSelection;
        set => SetPersistedProperty(ref _quickPreviewOnSelection, value);
    }

    public bool AnimateModels
    {
        get => _animateModels;
        set => SetPersistedProperty(ref _animateModels, value);
    }

    public bool ShowBspMarkers
    {
        get => _showBspMarkers;
        set => SetPersistedProperty(ref _showBspMarkers, value);
    }

    /// <summary>Adds PakScape's extract and pack verbs to File Explorer context menus.</summary>
    public bool ExplorerActionsEnabled
    {
        get => _explorerActionsEnabled;
        set => SetPersistedProperty(ref _explorerActionsEnabled, value);
    }

    private void SetPersistedProperty<T>(ref T storage, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (!SetProperty(ref storage, value, propertyName) || _isLoading)
        {
            return;
        }

        Save();
    }

    private static PakScapeSettings Load()
    {
        var settings = new PakScapeSettings();
        var snapshot = settings.ReadSnapshot();
        if (snapshot is null)
        {
            return settings;
        }

        settings._isLoading = true;
        try
        {
            settings.Appearance = ParseEnum(snapshot.Appearance, AppearancePreference.Automatic);
            settings.DefaultView = ParseEnum(snapshot.DefaultView, ArchiveViewMode.Details);
            settings.DefaultSort = ParseEnum(snapshot.DefaultSort, DefaultSortPreference.Name);
            settings.DefaultSortAscending = snapshot.DefaultSortAscending ?? true;
            settings.TextSize = snapshot.TextSize ?? DefaultTextSize;
            settings.ConfirmDeletion = snapshot.ConfirmDeletion ?? true;
            settings.ConfirmOverwrite = snapshot.ConfirmOverwrite ?? true;
            settings.BackupBeforeSave = snapshot.BackupBeforeSave ?? false;
            settings.QuickPreviewOnSelection = snapshot.QuickPreviewOnSelection ?? false;
            settings.AnimateModels = snapshot.AnimateModels ?? true;
            settings.ShowBspMarkers = snapshot.ShowBspMarkers ?? false;
            settings.ExplorerActionsEnabled = snapshot.ExplorerActionsEnabled ?? true;
        }
        finally
        {
            settings._isLoading = false;
        }

        return settings;
    }

    private static TEnum ParseEnum<TEnum>(string? value, TEnum fallback) where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) ? parsed : fallback;

    private SettingsSnapshot? ReadSnapshot()
    {
        if (_settingsPath is null || !File.Exists(_settingsPath))
        {
            return null;
        }

        try
        {
            var json = ReadSettingsText(_settingsPath);
            return json is null ? null : JsonSerializer.Deserialize<SettingsSnapshot>(json, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private void Save()
    {
        if (_settingsPath is null)
        {
            return;
        }

        var snapshot = new SettingsSnapshot
        {
            Appearance = Appearance.ToString(),
            DefaultView = DefaultView.ToString(),
            DefaultSort = DefaultSort.ToString(),
            DefaultSortAscending = DefaultSortAscending,
            TextSize = TextSize,
            ConfirmDeletion = ConfirmDeletion,
            ConfirmOverwrite = ConfirmOverwrite,
            BackupBeforeSave = BackupBeforeSave,
            QuickPreviewOnSelection = QuickPreviewOnSelection,
            AnimateModels = AnimateModels,
            ShowBspMarkers = ShowBspMarkers,
            ExplorerActionsEnabled = ExplorerActionsEnabled,
        };

        var directory = Path.GetDirectoryName(_settingsPath);
        if (directory is null)
        {
            return;
        }

        var json = JsonSerializer.Serialize(snapshot, SerializerOptions);
        var temporaryPath = Path.Combine(directory, $".settings.{Guid.NewGuid():N}.tmp");
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
            // Preferences are non-critical; the session keeps the in-memory value.
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
            return Path.Combine(directory, "settings.json");
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
        var text = new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true).GetString(bytes);
        /* Editors such as Notepad add a byte order mark that the JSON reader rejects. */
        return text.TrimStart('\uFEFF');
    }

    private sealed class SettingsSnapshot
    {
        public string? Appearance { get; set; }

        public string? DefaultView { get; set; }

        public string? DefaultSort { get; set; }

        public bool? DefaultSortAscending { get; set; }

        public double? TextSize { get; set; }

        public bool? ConfirmDeletion { get; set; }

        public bool? ConfirmOverwrite { get; set; }

        public bool? BackupBeforeSave { get; set; }

        public bool? QuickPreviewOnSelection { get; set; }

        public bool? AnimateModels { get; set; }

        public bool? ShowBspMarkers { get; set; }

        public bool? ExplorerActionsEnabled { get; set; }
    }
}
