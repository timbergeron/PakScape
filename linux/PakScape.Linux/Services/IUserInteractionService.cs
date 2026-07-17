using PakStudio.Core.Documents;

namespace PakScape.Linux.Services;

public interface IUserInteractionService
{
    Task<string?> PickArchiveToOpenAsync();

    Task<string?> PickArchiveSavePathAsync(string suggestedFileName, string formatId);

    Task<IReadOnlyList<string>> PickFilesToAddAsync();

    Task<string?> PickFolderToAddAsync();

    Task<string?> PickExportDirectoryAsync();

    Task<string?> PromptAsync(string title, string message, string initialValue);

    Task ShowInfoAsync(string title, string message);

    Task ShowErrorAsync(string title, string message);

    Task<bool> ConfirmAsync(string title, string message, string confirmText = "Continue");

    Task<SaveChangesDecision> ConfirmSaveChangesAsync(string displayName);
}
