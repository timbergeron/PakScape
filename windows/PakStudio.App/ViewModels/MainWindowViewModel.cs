using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using PakStudio.App.Commands;
using PakStudio.App.Models;
using PakStudio.Core.Documents;
using PakStudio.Core.Interfaces;
using PakStudio.Core.Nodes;

namespace PakStudio.App.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
{
    private readonly IArchiveService _archiveService;
    private readonly IFileDialogService _fileDialogService;
    private readonly IMessageBoxService _messageBoxService;
    private readonly IRecentFilesService _recentFilesService;
    private readonly IIconService _iconService;
    private readonly Dictionary<ArchiveFolderNode, FolderTreeNodeViewModel> _folderLookup = [];

    private ArchiveDocument? _document;
    private FolderTreeNodeViewModel? _selectedFolder;
    private ArchiveItemViewModel? _selectedItem;
    private ArchiveFolderNode? _currentFolder;
    private ArchiveViewMode _activeViewMode = ArchiveViewMode.Details;
    private string _statusText = "Ready";
    private string _selectionStatus = "0 selected";
    private bool _isInitialized;

    public MainWindowViewModel(
        IArchiveService archiveService,
        IFileDialogService fileDialogService,
        IMessageBoxService messageBoxService,
        IRecentFilesService recentFilesService,
        IIconService iconService)
    {
        _archiveService = archiveService;
        _fileDialogService = fileDialogService;
        _messageBoxService = messageBoxService;
        _recentFilesService = recentFilesService;
        _iconService = iconService;

        NewCommand = new RelayCommand(CreateNewArchive);
        OpenCommand = new AsyncRelayCommand(OpenAsync);
        SaveCommand = new AsyncRelayCommand(SaveAsync, CanSave);
        SaveAsCommand = new AsyncRelayCommand(SaveAsAsync, () => Document is not null);
        RefreshCommand = new RelayCommand(RefreshCurrentFolder, () => Document is not null);
        ExitCommand = new RelayCommand(() => Application.Current.Shutdown());
        AboutCommand = new RelayCommand(ShowAbout);

        ShowLargeIconsCommand = new RelayCommand(() => SetViewMode(ArchiveViewMode.LargeIcons));
        ShowSmallIconsCommand = new RelayCommand(() => SetViewMode(ArchiveViewMode.SmallIcons));
        ShowListCommand = new RelayCommand(() => SetViewMode(ArchiveViewMode.List));
        ShowDetailsCommand = new RelayCommand(() => SetViewMode(ArchiveViewMode.Details));
    }

    public ObservableCollection<FolderTreeNodeViewModel> FolderRoots { get; } = [];

    public ObservableCollection<ArchiveItemViewModel> CurrentItems { get; } = [];

    public ArchiveDocument? Document
    {
        get => _document;
        private set
        {
            if (SetProperty(ref _document, value))
            {
                OnPropertyChanged(nameof(WindowTitle));
                OnPropertyChanged(nameof(CurrentFolderPath));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public FolderTreeNodeViewModel? SelectedFolder
    {
        get => _selectedFolder;
        private set => SetProperty(ref _selectedFolder, value);
    }

    public ArchiveItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (SetProperty(ref _selectedItem, value))
            {
                SelectionStatus = value is null ? "0 selected" : $"1 selected: {value.Name}";
            }
        }
    }

    public ArchiveViewMode ActiveViewMode
    {
        get => _activeViewMode;
        private set => SetProperty(ref _activeViewMode, value);
    }

    public string WindowTitle
    {
        get
        {
            if (Document is null)
            {
                return "PakStudio";
            }

            var dirtyMarker = Document.IsDirty ? "*" : string.Empty;
            return $"{Document.DisplayName}{dirtyMarker} - PakStudio";
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string CurrentFolderPath => _currentFolder?.FullPath ?? "/";

    public string SelectionStatus
    {
        get => _selectionStatus;
        private set => SetProperty(ref _selectionStatus, value);
    }

    public IReadOnlyList<string> RecentFiles => _recentFilesService.GetRecentFiles();

    public RelayCommand NewCommand { get; }

    public AsyncRelayCommand OpenCommand { get; }

    public AsyncRelayCommand SaveCommand { get; }

    public AsyncRelayCommand SaveAsCommand { get; }

    public RelayCommand RefreshCommand { get; }

    public RelayCommand ExitCommand { get; }

    public RelayCommand AboutCommand { get; }

    public RelayCommand ShowLargeIconsCommand { get; }

    public RelayCommand ShowSmallIconsCommand { get; }

    public RelayCommand ShowListCommand { get; }

    public RelayCommand ShowDetailsCommand { get; }

    public Task InitializeAsync()
    {
        if (_isInitialized)
        {
            return Task.CompletedTask;
        }

        _isInitialized = true;
        LoadDocument(SampleDocumentFactory.Create());
        StatusText = "Loaded sample archive shell.";
        return Task.CompletedTask;
    }

    public void SelectFolder(FolderTreeNodeViewModel? folder)
    {
        if (folder is null)
        {
            return;
        }

        SelectedFolder = folder;
        _currentFolder = folder.Folder;
        OnPropertyChanged(nameof(CurrentFolderPath));
        RebuildCurrentItems();
    }

    public void OpenItem(ArchiveItemViewModel? item)
    {
        if (item?.Node is not ArchiveFolderNode folder)
        {
            return;
        }

        if (_folderLookup.TryGetValue(folder, out var folderViewModel))
        {
            SelectFolder(folderViewModel);
            folderViewModel.IsExpanded = true;
            folderViewModel.IsSelected = true;
        }
    }

    private void CreateNewArchive()
    {
        LoadDocument(new ArchiveDocument
        {
            FormatId = "pak",
        });
        StatusText = "Created a new empty archive.";
    }

    private async Task OpenAsync()
    {
        var path = _fileDialogService.PickArchiveToOpen();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            StatusText = "Opening archive...";
            var document = await _archiveService.OpenAsync(path).ConfigureAwait(true);
            LoadDocument(document);
            _recentFilesService.Add(path);
            OnPropertyChanged(nameof(RecentFiles));
            StatusText = $"Opened {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            StatusText = "Open failed.";
            _messageBoxService.ShowError("Open Failed", ex.Message);
        }
    }

    private async Task SaveAsync()
    {
        if (Document is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(Document.FilePath))
        {
            await SaveAsAsync().ConfigureAwait(true);
            return;
        }

        try
        {
            StatusText = "Saving archive...";
            await _archiveService.SaveAsync(Document, Document.FilePath).ConfigureAwait(true);
            _recentFilesService.Add(Document.FilePath);
            OnPropertyChanged(nameof(RecentFiles));
            OnPropertyChanged(nameof(WindowTitle));
            StatusText = $"Saved {Path.GetFileName(Document.FilePath)}";
        }
        catch (Exception ex)
        {
            StatusText = "Save failed.";
            _messageBoxService.ShowError("Save Failed", ex.Message);
        }
    }

    private async Task SaveAsAsync()
    {
        if (Document is null)
        {
            return;
        }

        var suggestedName = Document.FilePath is null ? "Untitled.pak" : Path.GetFileName(Document.FilePath);
        var path = _fileDialogService.PickArchiveSavePath(suggestedName, Document.FormatId, Document.FilePath);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            StatusText = "Saving archive...";
            await _archiveService.SaveAsync(Document, path).ConfigureAwait(true);
            _recentFilesService.Add(path);
            OnPropertyChanged(nameof(RecentFiles));
            OnPropertyChanged(nameof(WindowTitle));
            StatusText = $"Saved {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            StatusText = "Save failed.";
            _messageBoxService.ShowError("Save Failed", ex.Message);
        }
    }

    private bool CanSave() => Document is not null;

    private void RefreshCurrentFolder()
    {
        RebuildCurrentItems();
        StatusText = "Refreshed current folder.";
    }

    private void ShowAbout()
    {
        _messageBoxService.ShowInfo(
            "About PakStudio",
            "PakStudio is a Windows WPF port scaffold for browsing and editing Quake PAK archives.");
    }

    private void SetViewMode(ArchiveViewMode mode)
    {
        ActiveViewMode = mode;
        StatusText = $"View mode: {mode}";
    }

    private void LoadDocument(ArchiveDocument document)
    {
        Document = document;
        _folderLookup.Clear();
        FolderRoots.Clear();

        var rootDisplayName = document.DisplayName;
        var rootViewModel = BuildFolderTree(document.Root, rootDisplayName);
        rootViewModel.IsExpanded = true;
        FolderRoots.Add(rootViewModel);

        SelectFolder(rootViewModel);
        SelectedItem = null;
    }

    private FolderTreeNodeViewModel BuildFolderTree(ArchiveFolderNode folder, string displayName)
    {
        var viewModel = new FolderTreeNodeViewModel(folder, displayName);
        _folderLookup[folder] = viewModel;

        foreach (var childFolder in folder.Folders)
        {
            viewModel.Children.Add(BuildFolderTree(childFolder, childFolder.Name));
        }

        return viewModel;
    }

    private void RebuildCurrentItems()
    {
        CurrentItems.Clear();

        if (_currentFolder is null)
        {
            return;
        }

        foreach (var folder in _currentFolder.Folders)
        {
            CurrentItems.Add(new ArchiveItemViewModel(folder, _iconService.GetGlyphForNode(folder)));
        }

        foreach (var file in _currentFolder.Files)
        {
            CurrentItems.Add(new ArchiveItemViewModel(file, _iconService.GetGlyphForNode(file)));
        }

        SelectionStatus = $"{CurrentItems.Count} item(s)";
        StatusText = $"{CurrentItems.Count} item(s) in {CurrentFolderPath}";
    }
}
