using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using PakStudio.Core.Documents;
using PakStudio.Core.Interfaces;
using PakStudio.Core.Nodes;

namespace PakStudio.App.Services;

/// <summary>
/// Runs the File Explorer context menu verbs registered by <see cref="ShellIntegrationService"/>.
/// These mirror the macOS Finder services: extract an archive, or pack a folder into one.
/// </summary>
public static class ShellCommandRunner
{
    /// <summary>
    /// Returns true when the arguments named a shell verb, in which case the work has
    /// already run (or been cancelled) and no archive window should open.
    /// </summary>
    public static async Task<bool> TryRunAsync(IServiceProvider services, string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(arguments);

        var verb = arguments.FirstOrDefault(argument =>
            string.Equals(argument, ShellIntegrationService.ExtractSwitch, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(argument, ShellIntegrationService.PackSwitch, StringComparison.OrdinalIgnoreCase));
        if (verb is null)
        {
            return false;
        }

        var target = arguments
            .SkipWhile(argument => !string.Equals(argument, verb, StringComparison.OrdinalIgnoreCase))
            .Skip(1)
            .FirstOrDefault(argument => !string.IsNullOrWhiteSpace(argument));

        using var scope = services.CreateScope();
        var messageBoxService = scope.ServiceProvider.GetRequiredService<IMessageBoxService>();
        if (string.IsNullOrWhiteSpace(target))
        {
            messageBoxService.ShowError(
                "PakScape",
                "Select a .pak or .pk3 archive to extract, or a folder to pack.");
            return true;
        }

        try
        {
            if (string.Equals(verb, ShellIntegrationService.ExtractSwitch, StringComparison.OrdinalIgnoreCase))
            {
                await ExtractAsync(scope.ServiceProvider, target).ConfigureAwait(true);
            }
            else
            {
                await PackAsync(scope.ServiceProvider, target).ConfigureAwait(true);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            messageBoxService.ShowError("PakScape", exception.Message);
        }

        return true;
    }

    private static async Task ExtractAsync(IServiceProvider services, string archivePath)
    {
        var messageBoxService = services.GetRequiredService<IMessageBoxService>();
        if (!File.Exists(archivePath))
        {
            messageBoxService.ShowError("Extract Failed", $"'{archivePath}' no longer exists.");
            return;
        }

        var outputRoot = services.GetRequiredService<IFileDialogService>().PickExportDirectory();
        if (string.IsNullOrWhiteSpace(outputRoot))
        {
            return;
        }

        var document = await services.GetRequiredService<IArchiveService>()
            .OpenAsync(archivePath)
            .ConfigureAwait(true);

        var destination = AvailablePath(
            outputRoot,
            Path.GetFileNameWithoutExtension(archivePath),
            extension: null);
        Directory.CreateDirectory(destination);

        var transferService = services.GetRequiredService<IArchiveFileTransferService>();
        foreach (var child in document.Root.Children.ToList())
        {
            _ = transferService.Export(child, destination);
        }

        RevealInExplorer(destination);
    }

    private static async Task PackAsync(IServiceProvider services, string folderPath)
    {
        var messageBoxService = services.GetRequiredService<IMessageBoxService>();
        if (!Directory.Exists(folderPath))
        {
            messageBoxService.ShowError("Pack Failed", $"'{folderPath}' no longer exists.");
            return;
        }

        var folder = new DirectoryInfo(folderPath);
        var outputPath = services.GetRequiredService<IFileDialogService>()
            .PickArchiveSavePath($"{folder.Name}.pak", "pak", folder.FullName);
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return;
        }

        var formatId = string.Equals(
            Path.GetExtension(outputPath),
            ".pk3",
            StringComparison.OrdinalIgnoreCase)
            ? "pk3"
            : "pak";
        var document = new ArchiveDocument { FormatId = formatId };
        var transferService = services.GetRequiredService<IArchiveFileTransferService>();
        foreach (var childDirectory in folder.EnumerateDirectories())
        {
            _ = transferService.ImportDirectory(document.Root, childDirectory.FullName);
        }
        foreach (var childFile in folder.EnumerateFiles())
        {
            _ = transferService.ImportFile(document.Root, childFile.FullName);
        }

        await services.GetRequiredService<IArchiveService>()
            .SaveAsync(document, outputPath)
            .ConfigureAwait(true);

        RevealInExplorer(outputPath);
    }

    private static string AvailablePath(string directory, string baseName, string? extension)
    {
        var suffix = extension is null ? string.Empty : $".{extension}";
        var candidate = Path.Combine(directory, $"{baseName}{suffix}");
        for (var attempt = 2; File.Exists(candidate) || Directory.Exists(candidate); attempt++)
        {
            candidate = Path.Combine(directory, $"{baseName} {attempt}{suffix}");
        }

        return candidate;
    }

    private static void RevealInExplorer(string path)
    {
        try
        {
            _ = Process.Start(new ProcessStartInfo("explorer.exe")
            {
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Revealing the result is a convenience; the output is already written.
        }
    }
}
