using PakStudio.App.Views;

namespace PakStudio.App.Services;

public interface IArchiveWindowService
{
    MainWindow ShowBlankWorkspace();

    MainWindow ShowNewArchive(string formatId);

    MainWindow ShowArchive(string path);
}
