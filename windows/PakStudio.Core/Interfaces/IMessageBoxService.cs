using PakStudio.Core.Documents;

namespace PakStudio.Core.Interfaces;

public interface IMessageBoxService
{
    void ShowInfo(string title, string message);

    void ShowError(string title, string message);

    bool Confirm(string title, string message);

    SaveChangesDecision ConfirmSaveChanges(string displayName);
}
