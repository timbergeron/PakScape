namespace PakStudio.Core.Interfaces;

public interface IFileDialogService
{
    string? PickArchiveToOpen();

    string? PickArchiveSavePath(string suggestedFileName, string formatId, string? existingPath = null);
}
