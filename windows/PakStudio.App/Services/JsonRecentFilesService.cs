using System.Text.Json;
using PakStudio.Core.Interfaces;

namespace PakStudio.App.Services;

public sealed class JsonRecentFilesService : IRecentFilesService
{
    private readonly string _settingsPath;

    public JsonRecentFilesService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var directory = Path.Combine(appData, "PakStudio");
        Directory.CreateDirectory(directory);
        _settingsPath = Path.Combine(directory, "recent-files.json");
    }

    public IReadOnlyList<string> GetRecentFiles()
    {
        if (!File.Exists(_settingsPath))
        {
            return Array.Empty<string>();
        }

        try
        {
            var json = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public void Add(string path)
    {
        var files = GetRecentFiles()
            .Where(existing => !string.Equals(existing, path, StringComparison.OrdinalIgnoreCase))
            .Prepend(path)
            .Take(10)
            .ToList();

        var json = JsonSerializer.Serialize(files, new JsonSerializerOptions
        {
            WriteIndented = true,
        });

        File.WriteAllText(_settingsPath, json);
    }
}
