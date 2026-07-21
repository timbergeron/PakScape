using Avalonia.Controls;
using Avalonia.Platform.Storage;
using PakScape.Linux.Views;
using PakStudio.Core.Documents;

namespace PakScape.Linux.Services;

public sealed class AvaloniaUserInteractionService(Func<Window?> ownerProvider)
    : IUserInteractionService
{
    public async Task ShowAboutAsync()
    {
        var dialog = new AboutWindow();
        await dialog.ShowDialog(Owner);
    }

    private static readonly FilePickerFileType PakArchiveType = new("Quake PAK archive")
    {
        Patterns = ["*.pak"],
        MimeTypes = ["application/x-quake-pak"],
    };

    private static readonly FilePickerFileType Pk3ArchiveType = new("Quake PK3 archive")
    {
        Patterns = ["*.pk3"],
        MimeTypes = ["application/x-quake-pk3"],
    };

    public async Task<string?> PickArchiveToOpenAsync()
    {
        var files = await Owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Quake archive",
            AllowMultiple = false,
            FileTypeFilter = [PakArchiveType, Pk3ArchiveType],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickArchiveSavePathAsync(
        string suggestedFileName,
        string formatId)
    {
        var selectedType = string.Equals(formatId, "pk3", StringComparison.OrdinalIgnoreCase)
            ? Pk3ArchiveType
            : PakArchiveType;
        var file = await Owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Quake archive",
            SuggestedFileName = suggestedFileName,
            DefaultExtension = selectedType == Pk3ArchiveType ? "pk3" : "pak",
            FileTypeChoices = [PakArchiveType, Pk3ArchiveType],
            SuggestedFileType = selectedType,
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }

    public async Task<IReadOnlyList<string>> PickFilesToAddAsync()
    {
        var files = await Owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add files to archive",
            AllowMultiple = true,
            FileTypeFilter = [FilePickerFileTypes.All],
        });
        return files
            .Select(file => file.TryGetLocalPath())
            .Where(path => path is not null)
            .Cast<string>()
            .ToList();
    }

    public async Task<string?> PickFolderToAddAsync()
    {
        var folders = await Owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Add folder to archive",
            AllowMultiple = false,
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickExportDirectoryAsync()
    {
        var folders = await Owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Export archive items",
            AllowMultiple = false,
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public Task<string?> PromptAsync(
        string title,
        string message,
        string initialValue)
    {
        var dialog = new TextInputDialogWindow(title, message, initialValue);
        return dialog.ShowDialog<string?>(Owner);
    }

    public async Task ShowInfoAsync(string title, string message)
    {
        var dialog = new MessageDialogWindow(title, message, MessageDialogButtons.Ok);
        _ = await dialog.ShowDialog<MessageDialogResult>(Owner);
    }

    public async Task ShowErrorAsync(string title, string message)
    {
        var dialog = new MessageDialogWindow(title, message, MessageDialogButtons.Ok);
        _ = await dialog.ShowDialog<MessageDialogResult>(Owner);
    }

    public async Task<bool> ConfirmAsync(
        string title,
        string message,
        string confirmText = "Continue")
    {
        var dialog = new MessageDialogWindow(
            title,
            message,
            MessageDialogButtons.ConfirmCancel,
            confirmText);
        return await dialog.ShowDialog<MessageDialogResult>(Owner) == MessageDialogResult.Confirm;
    }

    public async Task<SaveChangesDecision> ConfirmSaveChangesAsync(string displayName)
    {
        var dialog = new MessageDialogWindow(
            "Save changes?",
            $"Save changes to '{displayName}' before continuing?",
            MessageDialogButtons.SaveDiscardCancel);
        var result = await dialog.ShowDialog<MessageDialogResult>(Owner);
        return MessageDialogWindow.ToSaveChangesDecision(result);
    }

    private Window Owner => ownerProvider()
        ?? throw new InvalidOperationException("The main window is not available.");
}
