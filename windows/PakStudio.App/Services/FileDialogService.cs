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
            Filter = "Quake archives (*.pak;*.pk3)|*.pak;*.pk3|PAK archives (*.pak)|*.pak|PK3 archives (*.pk3)|*.pk3|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickArchiveSavePath(string suggestedFileName, string formatId, string? existingPath = null)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save Archive",
            Filter = "PAK archives (*.pak)|*.pak|PK3 archives (*.pk3)|*.pk3",
            FileName = suggestedFileName,
            OverwritePrompt = true,
            AddExtension = true,
            DefaultExt = string.Equals(formatId, "pk3", StringComparison.OrdinalIgnoreCase)
                ? ".pk3"
                : ".pak",
            FilterIndex = string.Equals(formatId, "pk3", StringComparison.OrdinalIgnoreCase) ? 2 : 1,
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
}
