using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using PakStudio.Core.Documents;
using PakStudio.Core.Interfaces;

namespace PakScape.Linux.Services;

/// <summary>
/// Runs the file manager scripts installed by <see cref="DesktopIntegrationService"/>.
/// These mirror the macOS Finder services: extract an archive, or pack a folder into one.
/// </summary>
public static class DesktopCommandRunner
{
    /// <summary>
    /// Returns true when the arguments named a script verb, in which case a small task
    /// window takes over and no archive window should open.
    /// </summary>
    public static bool TryStart(
        IClassicDesktopStyleApplicationLifetime desktop,
        IArchiveService archiveService,
        IReadOnlyList<string>? arguments)
    {
        ArgumentNullException.ThrowIfNull(desktop);
        ArgumentNullException.ThrowIfNull(archiveService);

        var verb = arguments?.FirstOrDefault(argument =>
            string.Equals(argument, DesktopIntegrationService.ExtractSwitch, StringComparison.Ordinal) ||
            string.Equals(argument, DesktopIntegrationService.PackSwitch, StringComparison.Ordinal));
        if (verb is null || arguments is null)
        {
            return false;
        }

        var isExtract = string.Equals(
            verb,
            DesktopIntegrationService.ExtractSwitch,
            StringComparison.Ordinal);
        var target = arguments
            .SkipWhile(argument => !string.Equals(argument, verb, StringComparison.Ordinal))
            .Skip(1)
            .FirstOrDefault(argument => !string.IsNullOrWhiteSpace(argument));

        var status = new TextBlock
        {
            Text = isExtract ? "Choose where to extract the archive." : "Choose where to save the archive.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = Avalonia.Media.TextAlignment.Center,
        };
        var host = new Window
        {
            Title = isExtract ? "Extract with PakScape" : "Pack Folder with PakScape",
            Width = 380,
            Height = 130,
            CanResize = false,
            ShowInTaskbar = true,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new Border { Padding = new Avalonia.Thickness(24), Child = status },
        };

        desktop.MainWindow = host;
        host.Show();
        _ = RunAsync(host, status, archiveService, isExtract, target);
        return true;
    }

    private static async Task RunAsync(
        Window host,
        TextBlock status,
        IArchiveService archiveService,
        bool isExtract,
        string? target)
    {
        var interactionService = new AvaloniaUserInteractionService(() => host);
        try
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                await interactionService.ShowErrorAsync(
                    "PakScape",
                    "Select a .pak or .pk3 archive to extract, or a folder to pack.");
                return;
            }

            if (isExtract)
            {
                await ExtractAsync(interactionService, status, archiveService, target);
            }
            else
            {
                await PackAsync(interactionService, status, archiveService, target);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await interactionService.ShowErrorAsync("PakScape", exception.Message);
        }
        finally
        {
            host.Close();
        }
    }

    private static async Task ExtractAsync(
        AvaloniaUserInteractionService interactionService,
        TextBlock status,
        IArchiveService archiveService,
        string archivePath)
    {
        if (!File.Exists(archivePath))
        {
            await interactionService.ShowErrorAsync(
                "Extract failed",
                $"'{archivePath}' no longer exists.");
            return;
        }

        var outputRoot = await interactionService.PickExportDirectoryAsync();
        if (string.IsNullOrWhiteSpace(outputRoot))
        {
            return;
        }

        status.Text = $"Extracting {Path.GetFileName(archivePath)}...";
        var document = await archiveService.OpenAsync(archivePath);
        var destination = AvailablePath(
            outputRoot,
            Path.GetFileNameWithoutExtension(archivePath),
            extension: null);
        Directory.CreateDirectory(destination);

        using var transferService = new LinuxArchiveFileTransferService();
        var children = document.Root.Children.ToList();
        await Task.Run(() =>
        {
            foreach (var child in children)
            {
                _ = transferService.Export(child, destination);
            }
        });

        RevealInFileManager(destination);
    }

    private static async Task PackAsync(
        AvaloniaUserInteractionService interactionService,
        TextBlock status,
        IArchiveService archiveService,
        string folderPath)
    {
        if (!Directory.Exists(folderPath))
        {
            await interactionService.ShowErrorAsync("Pack failed", $"'{folderPath}' no longer exists.");
            return;
        }

        var folder = new DirectoryInfo(folderPath);
        var outputPath = await interactionService.PickArchiveSavePathAsync($"{folder.Name}.pak", "pak");
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return;
        }

        status.Text = $"Packing {folder.Name}...";
        var formatId = Path.GetExtension(outputPath).Equals(".pk3", StringComparison.OrdinalIgnoreCase)
            ? "pk3"
            : "pak";
        var document = new ArchiveDocument { FormatId = formatId };

        using var transferService = new LinuxArchiveFileTransferService();
        await Task.Run(() =>
        {
            foreach (var childDirectory in folder.EnumerateDirectories())
            {
                _ = transferService.ImportDirectory(document.Root, childDirectory.FullName);
            }
            foreach (var childFile in folder.EnumerateFiles())
            {
                _ = transferService.ImportFile(document.Root, childFile.FullName);
            }
        });

        await archiveService.SaveAsync(document, outputPath);
        RevealInFileManager(Path.GetDirectoryName(outputPath) ?? outputPath);
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

    private static void RevealInFileManager(string path)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("xdg-open")
            {
                UseShellExecute = false,
                ArgumentList = { path },
            });
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Revealing the result is a convenience; the output is already written.
        }
    }
}
