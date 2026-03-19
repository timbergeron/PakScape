using System.Collections.ObjectModel;
using PakStudio.Core.Nodes;

namespace PakStudio.App.ViewModels;

public sealed class FolderTreeNodeViewModel : ViewModelBase
{
    private bool _isExpanded;
    private bool _isSelected;

    public FolderTreeNodeViewModel(ArchiveFolderNode folder, string displayName)
    {
        Folder = folder;
        DisplayName = displayName;
    }

    public ArchiveFolderNode Folder { get; }

    public ObservableCollection<FolderTreeNodeViewModel> Children { get; } = [];

    public string DisplayName { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
