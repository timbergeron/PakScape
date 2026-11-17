using System.Diagnostics;
using System.Globalization;

namespace PakScape.Linux.Services;

/// <summary>
/// Linux counterpart to the macOS Finder integration: claims the PAK/PK3 MIME types
/// through xdg-mime and installs extract/pack scripts into the file manager's menu.
/// </summary>
public static class DesktopIntegrationService
{
    public const string ExtractSwitch = "--extract";
    public const string PackSwitch = "--pack";

    private const string DesktopFileId = "io.github.timbergeron.PakScape.desktop";
    private const string ExtractScriptName = "Extract with PakScape";
    private const string PackScriptName = "Pack Folder with PakScape";
    private static readonly string[] MimeTypes =
    [
        "application/x-quake-pak",
        "application/x-quake-pk3",
    ];

    /* Nautilus, Nemo, and Caja all read executable scripts from these folders. */
    private static readonly string[] ScriptFolders =
    [
        "nautilus/scripts",
        "nemo/scripts",
        "caja/scripts",
    ];

    /// <summary>True when both archive MIME types already open in PakScape.</summary>
    public static bool IsAssociated =>
        MimeTypes.All(mimeType => string.Equals(
            RunCapturing("xdg-mime", ["query", "default", mimeType]),
            DesktopFileId,
            StringComparison.Ordinal));

    /// <summary>Makes PakScape the default application for PAK and PK3 archives.</summary>
    public static void Associate()
    {
        _ = Run("xdg-mime", ["default", DesktopFileId, .. MimeTypes]);
    }

    /// <summary>The folder the extract and pack scripts are written to first.</summary>
    public static string PrimaryScriptFolder => Path.Combine(DataHome(), ScriptFolders[0]);

    public static bool AreFileManagerActionsInstalled() =>
        ScriptFolders.Any(folder => File.Exists(
            Path.Combine(DataHome(), folder, ExtractScriptName)));

    /// <summary>Installs or removes the file manager's extract and pack scripts.</summary>
    public static void UpdateFileManagerActions(bool isEnabled)
    {
        if (Environment.ProcessPath is not { Length: > 0 } executable)
        {
            return;
        }

        foreach (var folder in ScriptFolders)
        {
            var directory = Path.Combine(DataHome(), folder);
            var extractPath = Path.Combine(directory, ExtractScriptName);
            var packPath = Path.Combine(directory, PackScriptName);

            try
            {
                if (!isEnabled)
                {
                    DeleteScript(extractPath);
                    DeleteScript(packPath);
                    continue;
                }

                /* Only Nautilus' folder is created outright; the rest follow their file manager. */
                if (!Directory.Exists(directory))
                {
                    if (!string.Equals(folder, ScriptFolders[0], StringComparison.Ordinal) &&
                        !Directory.Exists(Path.Combine(DataHome(), folder.Split('/')[0])))
                    {
                        continue;
                    }

                    Directory.CreateDirectory(directory);
                }

                WriteScript(extractPath, executable, ExtractSwitch);
                WriteScript(packPath, executable, PackSwitch);
            }
            catch (IOException)
            {
                // File manager integration is optional; the in-app commands still work.
            }
            catch (UnauthorizedAccessException)
            {
                // A read-only data directory disables the scripts for this session.
            }
        }
    }

    /// <summary>Opens the scripts folder so the user can inspect or remove the actions by hand.</summary>
    public static void OpenScriptsFolder()
    {
        var directory = PrimaryScriptFolder;
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        _ = Run("xdg-open", [directory]);
    }

    private static void WriteScript(string path, string executable, string argumentSwitch)
    {
        File.WriteAllText(path, ScriptBody(executable, argumentSwitch));
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    private static void DeleteScript(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static string ScriptBody(string executable, string argumentSwitch) =>
        string.Format(
            CultureInfo.InvariantCulture,
            """
            #!/bin/sh
            # Installed by PakScape. Turn it off again in Settings > Files.
            selection="${{NAUTILUS_SCRIPT_SELECTED_FILE_PATHS:-}}"
            [ -n "$selection" ] || selection="${{NEMO_SCRIPT_SELECTED_FILE_PATHS:-}}"
            [ -n "$selection" ] || selection="${{CAJA_SCRIPT_SELECTED_FILE_PATHS:-}}"

            if [ -n "$selection" ]; then
                printf '%s\n' "$selection" | while IFS= read -r target; do
                    [ -n "$target" ] || continue
                    '{0}' {1} "$target" &
                done
            else
                for target in "$@"; do
                    [ -n "$target" ] || continue
                    '{0}' {1} "$target" &
                done
            fi

            """,
            executable.Replace("'", "'\\''", StringComparison.Ordinal),
            argumentSwitch);

    private static string DataHome()
    {
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(dataHome) || !Path.IsPathFullyQualified(dataHome))
        {
            dataHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local",
                "share");
        }

        return dataHome;
    }

    private static bool Run(string fileName, string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo(fileName) { UseShellExecute = false };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            return process.WaitForExit(TimeSpan.FromSeconds(10)) && process.ExitCode == 0;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            /* Desktops without the xdg-utils tools simply keep their current defaults. */
            return false;
        }
    }

    private static string? RunCapturing(string fileName, string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            return process.WaitForExit(TimeSpan.FromSeconds(10)) && process.ExitCode == 0
                ? output.Trim()
                : null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }
}
