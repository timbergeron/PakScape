namespace PakStudio.Core.Interfaces;

public interface IRecentFilesService
{
    IReadOnlyList<string> GetRecentFiles();

    void Add(string path);
}
