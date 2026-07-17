using System.Collections.ObjectModel;
using PakStudio.Core.Nodes;

namespace PakScape.Linux.Models;

public sealed class FolderNodeViewModel
{
    public FolderNodeViewModel(
        ArchiveFolderNode folder,
        string displayName,
        bool isExpanded = false)
    {
        Folder = folder;
        DisplayName = displayName;
        IsExpanded = isExpanded;
    }

    public ArchiveFolderNode Folder { get; }

    public string DisplayName { get; }

    public bool IsExpanded { get; }

    public ObservableCollection<FolderNodeViewModel> Children { get; } = [];
}
