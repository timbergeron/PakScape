namespace PakStudio.App.Services;

public interface IDetailsColumnLayoutService
{
    IReadOnlyList<DetailsColumnState> Load();

    void Save(IReadOnlyList<DetailsColumnState> columns);
}
