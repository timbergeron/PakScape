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
            Filter = "PAK archives (*.pak)|*.pak|All files (*.*)|*.*",
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
            Filter = "PAK archives (*.pak)|*.pak|All files (*.*)|*.*",
            FileName = suggestedFileName,
            OverwritePrompt = true,
            AddExtension = true,
            DefaultExt = ".pak",
        };

        if (!string.IsNullOrWhiteSpace(existingPath))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(existingPath);
        }

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
