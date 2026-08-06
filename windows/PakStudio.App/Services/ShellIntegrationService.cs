using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PakStudio.App.Services;

/// <summary>
/// Windows counterpart to the macOS Finder integration: registers PakScape's file
/// types under HKCU and adds the extract/pack verbs to File Explorer context menus.
/// </summary>
public static class ShellIntegrationService
{
    public const string ExtractSwitch = "--extract";
    public const string PackSwitch = "--pack";

    private const string ClassesKey = @"Software\Classes";
    private const string ExtractVerbKey = "PakScape.Extract";
    private const string PackVerbKey = "PakScape.Pack";
    private const int ShcneAssocChanged = 0x08000000;
    private const uint ShcnfIdList = 0x0000;

    private static readonly (string Extension, string ProgId, string Description)[] ArchiveTypes =
    [
        (".pak", "PakScape.pak", "Quake PAK archive"),
        (".pk3", "PakScape.pk3", "Quake PK3 archive"),
        (".kpf", "PakScape.kpf", "Quake KPF archive"),
    ];

    /// <summary>True when all archive types resolve to this copy of PakScape.</summary>
    public static bool IsAssociated
    {
        get
        {
            if (ExecutablePath() is not { } executable)
            {
                return false;
            }

            try
            {
                return ArchiveTypes.All(type =>
                {
                    using var command = Registry.CurrentUser.OpenSubKey(
                        $@"{ClassesKey}\{type.ProgId}\shell\open\command");
                    return string.Equals(
                        command?.GetValue(null) as string,
                        OpenCommand(executable),
                        StringComparison.OrdinalIgnoreCase);
                });
            }
            catch (Exception exception) when (IsRegistryFailure(exception))
            {
                return false;
            }
        }
    }

    /// <summary>Registers the archive ProgIDs so PakScape appears as a handler for each type.</summary>
    public static void Associate()
    {
        if (ExecutablePath() is not { } executable)
        {
            return;
        }

        foreach (var (extension, progId, description) in ArchiveTypes)
        {
            using (var progIdKey = Registry.CurrentUser.CreateSubKey($@"{ClassesKey}\{progId}"))
            {
                progIdKey.SetValue(null, description);
                using var iconKey = progIdKey.CreateSubKey("DefaultIcon");
                iconKey.SetValue(null, $"\"{IconPath(executable)}\"");
                using var commandKey = progIdKey.CreateSubKey(@"shell\open\command");
                commandKey.SetValue(null, OpenCommand(executable));
            }

            using var openWithKey = Registry.CurrentUser.CreateSubKey(
                $@"{ClassesKey}\{extension}\OpenWithProgids");
            openWithKey.SetValue(progId, string.Empty, RegistryValueKind.String);
        }

        using (var capabilities = Registry.CurrentUser.CreateSubKey(@"Software\PakScape\Capabilities"))
        {
            capabilities.SetValue("ApplicationName", "PakScape");
            using var associations = capabilities.CreateSubKey("FileAssociations");
            foreach (var (extension, progId, _) in ArchiveTypes)
            {
                associations.SetValue(extension, progId);
            }
        }

        using (var registeredApplications = Registry.CurrentUser.CreateSubKey(
                   @"Software\RegisteredApplications"))
        {
            registeredApplications.SetValue("PakScape", @"Software\PakScape\Capabilities");
        }

        NotifyShell();
    }

    /// <summary>True when the Explorer extract/pack verbs point at this copy of PakScape.</summary>
    public static bool AreExplorerActionsRegistered()
    {
        if (ExecutablePath() is not { } executable)
        {
            return false;
        }

        try
        {
            using var packCommand = Registry.CurrentUser.OpenSubKey(
                $@"{ClassesKey}\Directory\shell\{PackVerbKey}\command");
            return string.Equals(
                packCommand?.GetValue(null) as string,
                VerbCommand(executable, PackSwitch),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (IsRegistryFailure(exception))
        {
            return false;
        }
    }

    /// <summary>Adds or removes PakScape's File Explorer context menu verbs.</summary>
    public static void UpdateExplorerActions(bool isEnabled)
    {
        if (ExecutablePath() is not { } executable)
        {
            return;
        }

        try
        {
            foreach (var (extension, _, _) in ArchiveTypes)
            {
                var verbPath = $@"{ClassesKey}\SystemFileAssociations\{extension}\shell\{ExtractVerbKey}";
                if (isEnabled)
                {
                    WriteVerb(verbPath, "Extract with PakScape", executable, ExtractSwitch);
                }
                else
                {
                    DeleteVerb(verbPath);
                }
            }

            var packPath = $@"{ClassesKey}\Directory\shell\{PackVerbKey}";
            if (isEnabled)
            {
                WriteVerb(packPath, "Pack Folder with PakScape", executable, PackSwitch);
            }
            else
            {
                DeleteVerb(packPath);
            }

            NotifyShell();
        }
        catch (Exception exception) when (IsRegistryFailure(exception))
        {
            // Explorer integration is optional; the in-app commands still work.
        }
    }

    /// <summary>Opens the Windows default apps page so the user can finish claiming the file types.</summary>
    public static void OpenDefaultAppsSettings()
    {
        try
        {
            _ = Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The settings app is unavailable on some SKUs; nothing else to do here.
        }
    }

    private static void WriteVerb(string verbPath, string title, string executable, string argumentSwitch)
    {
        using var verbKey = Registry.CurrentUser.CreateSubKey(verbPath);
        verbKey.SetValue(null, title);
        verbKey.SetValue("Icon", $"\"{executable}\"");
        using var commandKey = verbKey.CreateSubKey("command");
        commandKey.SetValue(null, VerbCommand(executable, argumentSwitch));
    }

    private static void DeleteVerb(string verbPath)
    {
        Registry.CurrentUser.DeleteSubKeyTree(verbPath, throwOnMissingSubKey: false);
    }

    private static string OpenCommand(string executable) => $"\"{executable}\" \"%1\"";

    private static string VerbCommand(string executable, string argumentSwitch) =>
        $"\"{executable}\" {argumentSwitch} \"%1\"";

    private static string IconPath(string executable)
    {
        var directory = Path.GetDirectoryName(executable);
        if (directory is null)
        {
            return executable;
        }

        var fileIcon = Path.Combine(directory, "PakScape.File.ico");
        return File.Exists(fileIcon) ? fileIcon : executable;
    }

    private static string? ExecutablePath()
    {
        var path = Environment.ProcessPath;
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    private static bool IsRegistryFailure(Exception exception) =>
        exception is UnauthorizedAccessException or System.Security.SecurityException or IOException;

    private static void NotifyShell()
    {
        try
        {
            SHChangeNotify(ShcneAssocChanged, ShcnfIdList, IntPtr.Zero, IntPtr.Zero);
        }
        catch (EntryPointNotFoundException)
        {
            // Explorer picks the change up on its next restart.
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
