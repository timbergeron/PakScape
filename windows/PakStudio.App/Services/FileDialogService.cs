using System.IO;
using Microsoft.Win32;
using PakStudio.Core.Interfaces;

namespace PakStudio.App.Services;

public sealed class FileDialogService : IFileDialogService
{
    public string? PickArchiveToOpen()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open Archive",
            Filter = "Quake archives (*.pak;*.pk3;*.kpf)|*.pak;*.pk3;*.kpf|PAK archives (*.pak)|*.pak|PK3 archives (*.pk3)|*.pk3|KPF archives (*.kpf)|*.kpf|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickArchiveSavePath(string suggestedFileName, string formatId, string? existingPath = null)
    {
        var normalizedFormat = formatId.ToLowerInvariant();
        var filterIndex = normalizedFormat switch
        {
            "pk3" => 2,
            "kpf" => 3,
            _ => 1,
        };
        var dialog = new SaveFileDialog
        {
            Title = "Save Archive",
            Filter = "PAK archives (*.pak)|*.pak|PK3 archives (*.pk3)|*.pk3|KPF archives (*.kpf)|*.kpf",
            FileName = suggestedFileName,
            OverwritePrompt = true,
            AddExtension = true,
            DefaultExt = $".{normalizedFormat}",
            FilterIndex = filterIndex,
        };

        if (!string.IsNullOrWhiteSpace(existingPath))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(existingPath);
        }

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public IReadOnlyList<string> PickFilesToAdd()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Add Files to Archive",
            Filter = "All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = true,
        };

        return dialog.ShowDialog() == true ? dialog.FileNames : Array.Empty<string>();
    }

    public string? PickFolderToAdd()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Add Folder to Archive",
            Multiselect = false,
        };

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public string? PickExportDirectory()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose Export Folder",
            Multiselect = false,
        };

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public string? PickImageSavePath(string suggestedFileName, string formatId)
    {
        var extension = formatId.Equals("jpeg", StringComparison.OrdinalIgnoreCase)
            ? "jpg"
            : formatId.ToLowerInvariant();
        var description = extension switch
        {
            "lmp" => "Quake LMP image",
            "jpg" => "JPEG image",
            "png" => "PNG image",
            "tga" => "TGA image",
            _ => "Image",
        };
        var dialog = new SaveFileDialog
        {
            Title = "Save Image As",
            Filter = $"{description} (*.{extension})|*.{extension}",
            FileName = suggestedFileName,
            OverwritePrompt = true,
            AddExtension = true,
            DefaultExt = $".{extension}",
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
