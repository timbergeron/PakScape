using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using PakStudio.App.Commands;
using PakStudio.App.Services;
using PakStudio.Core.Documents;
using PakStudio.Core.Interfaces;
using PakStudio.Core.Models;
using PakStudio.Core.Nodes;
using PakStudio.Core.Operations;
using PakStudio.Core.Playback;
using PakStudio.Core.Preview;

namespace PakStudio.App.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
{
    private const string ClipboardFormat = "PakScape.ArchiveClipboardId";
    private const int MaximumHistoryEntries = 50;

    private readonly IArchiveService _archiveService;
    private readonly IArchiveWindowService _archiveWindowService;
    private readonly IArchiveFileTransferService _fileTransferService;
    private readonly IFileDialogService _fileDialogService;
    private readonly IMessageBoxService _messageBoxService;
    private readonly IRecentFilesService _recentFilesService;
    private readonly IIconService _iconService;
    private readonly ArchiveThumbnailService _thumbnailService;
    private readonly ItemInfoWindowService _itemInfoWindowService;
    private readonly Dictionary<ArchiveFolderNode, FolderTreeNodeViewModel> _folderLookup = [];
    private readonly Stack<ArchiveFolderNode> _backHistory = [];
    private readonly Stack<ArchiveFolderNode> _forwardHistory = [];
    private readonly List<ArchiveHistoryEntry> _undoHistory = [];
    private readonly List<ArchiveHistoryEntry> _redoHistory = [];

    private ArchiveDocument? _document;
    private FolderTreeNodeViewModel? _selectedFolder;
    private ArchiveItemViewModel? _selectedItem;
    private IReadOnlyList<ArchiveItemViewModel> _selectedItems = [];
    private ArchiveFolderNode? _currentFolder;
    private ArchiveNode? _contextTarget;
    private ArchiveViewMode _activeViewMode = ArchiveViewMode.Details;
    private ArchiveSortColumn _sortColumn = ArchiveSortColumn.Name;
    private bool _sortDescending;
    private ArchiveClipboardPayload? _clipboardPayload;
    private IReadOnlyList<string> _clipboardExportedPaths = [];
    private string _searchText = string.Empty;
    private string _statusText = "Ready";
    private string _selectionStatus = "0 selected";
    private bool _isBusy;
    private bool _isInitialized;
    private int _currentRevision;
    private int _savedRevision;
    private int _nextRevision;
    private int _iconZoomLevel = 1;

    public MainWindowViewModel(
        IArchiveService archiveService,
        IArchiveWindowService archiveWindowService,
        IArchiveFileTransferService fileTransferService,
        IFileDialogService fileDialogService,
        IMessageBoxService messageBoxService,
        IRecentFilesService recentFilesService,
        IIconService iconService,
        ArchiveThumbnailService thumbnailService,
        ItemInfoWindowService itemInfoWindowService)
    {
        _archiveService = archiveService;
        _archiveWindowService = archiveWindowService;
        _fileTransferService = fileTransferService;
        _fileDialogService = fileDialogService;
        _messageBoxService = messageBoxService;
        _recentFilesService = recentFilesService;
        _iconService = iconService;
        _thumbnailService = thumbnailService;
        _itemInfoWindowService = itemInfoWindowService;

        NewCommand = new AsyncRelayCommand(() => CreateNewArchiveAsync("pak"), () => !IsBusy);
        NewPk3Command = new AsyncRelayCommand(() => CreateNewArchiveAsync("pk3"), () => !IsBusy);
        OpenCommand = new AsyncRelayCommand(OpenAsync, () => !IsBusy);
        OpenRecentCommand = new AsyncRelayCommand<string>(OpenRecentAsync, path =>
            !IsBusy && !string.IsNullOrWhiteSpace(path));
        SaveCommand = new AsyncRelayCommand(SaveAsync, CanSave);
        SaveAsCommand = new AsyncRelayCommand(SaveAsAsync, () => Document is not null && !IsBusy);
        OpenPakFolderCommand = new RelayCommand(OpenPakFolder, CanOpenPakFolder);
        UndoCommand = new RelayCommand(
            Undo,
            () => _undoHistory.Count > 0 && !IsInlineRenameActive && !IsBusy);
        RedoCommand = new RelayCommand(
            Redo,
            () => _redoHistory.Count > 0 && !IsInlineRenameActive && !IsBusy);
        RefreshCommand = new RelayCommand(RefreshCurrentFolder, () => Document is not null && !IsBusy);
        ExitCommand = new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty));
        AboutCommand = new RelayCommand(ShowAbout);

        NewFolderCommand = new RelayCommand(CreateFolder, CanModifyCurrentFolder);
        AddFilesCommand = new AsyncRelayCommand(AddFilesAsync, CanModifyCurrentFolder);
        AddFolderCommand = new AsyncRelayCommand(AddFolderAsync, CanModifyCurrentFolder);
        ContextNewFolderCommand = new RelayCommand(
            CreateFolderInContext,
            CanModifyContextDestination);
        ContextAddFilesCommand = new AsyncRelayCommand(
            AddFilesInContextAsync,
            CanModifyContextDestination);
        ContextAddFolderCommand = new AsyncRelayCommand(
            AddFolderInContextAsync,
            CanModifyContextDestination);
        RenameCommand = new RelayCommand(
            RequestSelectedItemRename,
            () => _selectedItems.Count == 1 && !IsInlineRenameActive && !IsBusy);
        DeleteCommand = new RelayCommand(
            DeleteSelectedItems,
            () => !IsInlineRenameActive && CanModifySelectedItems());
        ExportCommand = new AsyncRelayCommand(ExportSelectedItemsAsync, CanModifySelectedItems);
        SaveModelSkinAsCommand = new AsyncRelayCommand<string>(
            SaveSelectedModelSkinAsAsync,
            CanSaveSelectedModelSkinAs);
        SaveBspTexturesAsCommand = new AsyncRelayCommand<string>(
            SaveSelectedBspTexturesAsAsync,
            CanSaveSelectedBspTexturesAs);
        SaveWadTexturesAsCommand = new AsyncRelayCommand<string>(
            SaveSelectedWadTexturesAsAsync,
            CanSaveSelectedWadTexturesAs);
        PlayDemoInBrowserCommand = new RelayCommand(PlayDemoInBrowser, CanPlayDemoInBrowser);
        GetInfoCommand = new RelayCommand(
            ShowSelectedItemInfo,
            () => !IsBusy && (_selectedItems.Count > 0 || _currentFolder is not null));
        OpenSelectedCommand = new RelayCommand(
            OpenSelectedItem,
            () => _selectedItems.Count == 1 && !IsInlineRenameActive && !IsBusy);
        UpCommand = new RelayCommand(
            NavigateUp,
            () => _currentFolder?.Parent is not null && !IsInlineRenameActive && !IsBusy);
        BackCommand = new RelayCommand(NavigateBack, CanNavigateBack);
        ForwardCommand = new RelayCommand(NavigateForward, CanNavigateForward);
        CutCommand = new RelayCommand(() => CopySelection(isCut: true), CanModifySelectedItems);
        CopyCommand = new RelayCommand(() => CopySelection(isCut: false), CanModifySelectedItems);
        PasteCommand = new AsyncRelayCommand(PasteAsync, CanPaste);

        ShowLargeIconsCommand = new RelayCommand(() => SetViewMode(ArchiveViewMode.LargeIcons));
        ShowSmallIconsCommand = new RelayCommand(() => SetViewMode(ArchiveViewMode.SmallIcons));
        ShowListCommand = new RelayCommand(() => SetViewMode(ArchiveViewMode.List));
        ShowDetailsCommand = new RelayCommand(() => SetViewMode(ArchiveViewMode.Details));
        ZoomInIconsCommand = new RelayCommand(
            () => SetIconZoomLevel(_iconZoomLevel + 1),
            () => ActiveViewMode == ArchiveViewMode.LargeIcons && _iconZoomLevel < 2 && !IsBusy);
        ZoomOutIconsCommand = new RelayCommand(
            () => SetIconZoomLevel(_iconZoomLevel - 1),
            () => ActiveViewMode == ArchiveViewMode.LargeIcons && _iconZoomLevel > 0 && !IsBusy);
    }

    public event EventHandler? RenameRequested;

    public event EventHandler? CloseRequested;

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
                OnPropertyChanged(nameof(ArchiveDisplayName));
                OnPropertyChanged(nameof(SearchPlaceholder));
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
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public ArchiveViewMode ActiveViewMode
    {
        get => _activeViewMode;
        private set => SetProperty(ref _activeViewMode, value);
    }

    public double LargeIconTileWidth => _iconZoomLevel switch
    {
        0 => 104,
        2 => 164,
        _ => 128,
    };

    public double LargeIconPreviewWidth => _iconZoomLevel switch
    {
        0 => 64,
        2 => 120,
        _ => 88,
    };

    public double LargeIconPreviewHeight => _iconZoomLevel switch
    {
        0 => 54,
        2 => 98,
        _ => 72,
    };

    public double LargeIconGlyphSize => _iconZoomLevel switch
    {
        0 => 34,
        2 => 54,
        _ => 42,
    };

    public double LargeFolderWidth => _iconZoomLevel switch
    {
        0 => 40,
        2 => 64,
        _ => 48,
    };

    public double LargeFolderHeight => _iconZoomLevel switch
    {
        0 => 30,
        2 => 48,
        _ => 36,
    };

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                OnPropertyChanged(nameof(IsSearchActive));
                RebuildCurrentItems();
            }
        }
    }

    public string WindowTitle
    {
        get
        {
            if (Document is null)
            {
                return "PakScape";
            }

            var dirtyMarker = Document.IsDirty ? "*" : string.Empty;
            return $"{Document.DisplayName}{dirtyMarker} - PakScape";
        }
    }

    public string ArchiveDisplayName => Document?.DisplayName ?? "PakScape";

    public string SearchPlaceholder => $"Search {ArchiveDisplayName}";

    public bool IsSearchActive => !string.IsNullOrWhiteSpace(SearchText);

    public string SearchResultText => CurrentItems.Count == 1
        ? "1 result"
        : $"{CurrentItems.Count:N0} results";

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

    public string NameSortHeader => SortHeader("Name", ArchiveSortColumn.Name);

    public string TypeSortHeader => SortHeader("Type", ArchiveSortColumn.Type);

    public string DetailsSortHeader => SortHeader("Details", ArchiveSortColumn.Details);

    public string SizeSortHeader => SortHeader("Size", ArchiveSortColumn.Size);

    public string ModifiedSortHeader => SortHeader("Modified", ArchiveSortColumn.Modified);

    public bool HasModelSkinSaveOptions =>
        SelectedFile is { } file &&
        Path.GetExtension(file.Name).Equals(".mdl", StringComparison.OrdinalIgnoreCase);

    public bool HasBspTextureSaveOptions =>
        SelectedFile is { } file &&
        Path.GetExtension(file.Name).Equals(".bsp", StringComparison.OrdinalIgnoreCase);

    public bool HasWadTextureSaveOptions =>
        SelectedFile is { } file &&
        Path.GetExtension(file.Name).Equals(".wad", StringComparison.OrdinalIgnoreCase);

    public bool HasSkyboxPreview => SkyboxFaceSet.Find(SelectedFile) is not null;

    public AsyncRelayCommand NewCommand { get; }

    public AsyncRelayCommand NewPk3Command { get; }

    public AsyncRelayCommand OpenCommand { get; }

    public AsyncRelayCommand<string> OpenRecentCommand { get; }

    public AsyncRelayCommand SaveCommand { get; }

    public AsyncRelayCommand SaveAsCommand { get; }

    public RelayCommand OpenPakFolderCommand { get; }

    public RelayCommand UndoCommand { get; }

    public RelayCommand RedoCommand { get; }

    public RelayCommand RefreshCommand { get; }

    public RelayCommand ExitCommand { get; }

    public RelayCommand AboutCommand { get; }

    public RelayCommand NewFolderCommand { get; }

    public AsyncRelayCommand AddFilesCommand { get; }

    public AsyncRelayCommand AddFolderCommand { get; }

    public RelayCommand ContextNewFolderCommand { get; }

    public AsyncRelayCommand ContextAddFilesCommand { get; }

    public AsyncRelayCommand ContextAddFolderCommand { get; }

    public RelayCommand RenameCommand { get; }

    public RelayCommand DeleteCommand { get; }

    public AsyncRelayCommand ExportCommand { get; }

    public AsyncRelayCommand<string> SaveModelSkinAsCommand { get; }

    public AsyncRelayCommand<string> SaveBspTexturesAsCommand { get; }

    public AsyncRelayCommand<string> SaveWadTexturesAsCommand { get; }

    public RelayCommand PlayDemoInBrowserCommand { get; }

    public RelayCommand GetInfoCommand { get; }

    public RelayCommand OpenSelectedCommand { get; }

    public RelayCommand UpCommand { get; }

    public RelayCommand BackCommand { get; }

    public RelayCommand ForwardCommand { get; }

    public RelayCommand CutCommand { get; }

    public RelayCommand CopyCommand { get; }

    public AsyncRelayCommand PasteCommand { get; }

    public RelayCommand ShowLargeIconsCommand { get; }

    public RelayCommand ShowSmallIconsCommand { get; }

    public RelayCommand ShowListCommand { get; }

    public RelayCommand ShowDetailsCommand { get; }

    public RelayCommand ZoomInIconsCommand { get; }

    public RelayCommand ZoomOutIconsCommand { get; }

    public async Task InitializeAsync(
        string? archivePath = null,
        string initialFormatId = "pak")
    {
        if (_isInitialized)
        {
            return;
        }

        _isInitialized = true;
        LoadDocument(CreateEmptyDocument(initialFormatId));
        StatusText = "Ready. Open an archive or add files to a new one.";
        if (!string.IsNullOrWhiteSpace(archivePath))
        {
            await OpenPathAsync(archivePath).ConfigureAwait(true);
        }
    }

    public void SelectFolder(FolderTreeNodeViewModel? folder)
    {
        if (folder is null || IsBusy)
        {
            return;
        }

        NavigateToFolder(folder.Folder);
    }

    private void SelectFolderCore(FolderTreeNodeViewModel folder)
    {

        SelectedFolder = folder;
        _currentFolder = folder.Folder;
        OnPropertyChanged(nameof(CurrentFolderPath));
        RebuildCurrentItems();
        CommandManager.InvalidateRequerySuggested();
    }

    public void OpenItem(ArchiveItemViewModel? item)
    {
        if (item is null || IsBusy)
        {
            return;
        }

        if (item.Node is ArchiveFolderNode folder)
        {
            NavigateToFolder(folder);
            return;
        }

        if (item.Node is ArchiveFileNode file)
        {
            // A demo has a better destination than whichever application claims .dem.
            if (IsPlayableDemo(file))
            {
                PlayDemoInBrowser(file);
                return;
            }

            try
            {
                _fileTransferService.OpenWithDefaultApplication(file);
                StatusText = $"Opened {file.Name} in its default application.";
            }
            catch (Exception exception)
            {
                StatusText = "Could not open the selected file.";
                _messageBoxService.ShowError("Open File Failed", exception.Message);
            }
        }
    }

    public void SetSelectedItems(IEnumerable<ArchiveItemViewModel> items)
    {
        _selectedItems = items.Distinct().ToList();
        SelectedItem = _selectedItems.FirstOrDefault();
        OnPropertyChanged(nameof(HasModelSkinSaveOptions));
        OnPropertyChanged(nameof(HasBspTextureSaveOptions));
        OnPropertyChanged(nameof(HasWadTextureSaveOptions));
        OnPropertyChanged(nameof(HasSkyboxPreview));
        SelectionStatus = _selectedItems.Count switch
        {
            0 => $"{CurrentItems.Count} item(s)",
            1 => $"1 selected: {_selectedItems[0].Name}",
            _ => $"{_selectedItems.Count} selected",
        };
        CommandManager.InvalidateRequerySuggested();
    }

    public async Task AddDroppedPathsAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0 || !CanModifyCurrentFolder())
        {
            return;
        }

        await ImportPathsAsync(paths).ConfigureAwait(true);
    }

    public void SetContextTarget(ArchiveNode? node)
    {
        _contextTarget = node;
        CommandManager.InvalidateRequerySuggested();
    }

    public IReadOnlyList<string> PrepareSelectedItemsForDrag()
    {
        if (_selectedItems.Count == 0 || IsBusy)
        {
            return [];
        }

        return _fileTransferService.ExportToTemporaryLocation(
            _selectedItems.Select(item => item.Node).ToList());
    }

    public void ReleaseTemporaryTransfer(IReadOnlyList<string> paths) =>
        _fileTransferService.ReleaseTemporaryLocation(paths);

    public void SortBy(string? columnName)
    {
        if (!Enum.TryParse<ArchiveSortColumn>(columnName, ignoreCase: true, out var column))
        {
            return;
        }

        if (_sortColumn == column)
        {
            _sortDescending = !_sortDescending;
        }
        else
        {
            _sortColumn = column;
            _sortDescending = false;
        }

        OnPropertyChanged(nameof(NameSortHeader));
        OnPropertyChanged(nameof(TypeSortHeader));
        OnPropertyChanged(nameof(DetailsSortHeader));
        OnPropertyChanged(nameof(SizeSortHeader));
        OnPropertyChanged(nameof(ModifiedSortHeader));
        RebuildCurrentItems(_selectedItems.FirstOrDefault()?.Node);
    }

    public async Task<bool> CanCloseAsync()
    {
        if (IsBusy)
        {
            _messageBoxService.ShowInfo("Operation in Progress", "Wait for the current archive operation to finish before closing PakScape.");
            return false;
        }

        return await ConfirmDocumentReplacementAsync().ConfigureAwait(true);
    }

    private Task CreateNewArchiveAsync(string formatId)
    {
        _archiveWindowService.ShowNewArchive(formatId);
        return Task.CompletedTask;
    }

    private async Task OpenAsync()
    {
        var path = _fileDialogService.PickArchiveToOpen();
        if (!string.IsNullOrWhiteSpace(path))
        {
            _archiveWindowService.ShowArchive(path);
        }
        await Task.CompletedTask;
    }

    private async Task OpenRecentAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (!File.Exists(path))
        {
            _messageBoxService.ShowError("File Not Found", $"The recent archive no longer exists:\n{path}");
            return;
        }

        _archiveWindowService.ShowArchive(path);
        await Task.CompletedTask;
    }

    private async Task OpenPathAsync(string path)
    {
        try
        {
            IsBusy = true;
            StatusText = "Opening archive...";
            var document = await _archiveService.OpenAsync(path).ConfigureAwait(true);
            LoadDocument(document);
            RecordRecentFile(path);
            StatusText = $"Opened {Path.GetFileName(path)}";
        }
        catch (Exception exception)
        {
            StatusText = "Open failed.";
            _messageBoxService.ShowError("Open Failed", exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SaveAsync()
    {
        _ = await SaveDocumentAsync(saveAs: false).ConfigureAwait(true);
    }

    private async Task SaveAsAsync()
    {
        _ = await SaveDocumentAsync(saveAs: true).ConfigureAwait(true);
    }

    private async Task<bool> SaveDocumentAsync(bool saveAs)
    {
        if (Document is null)
        {
            return false;
        }

        var path = Document.FilePath;
        if (saveAs || string.IsNullOrWhiteSpace(path))
        {
            var suggestedName = string.IsNullOrWhiteSpace(Document.FilePath)
                ? Document.DisplayName
                : Path.GetFileName(Document.FilePath);
            path = _fileDialogService.PickArchiveSavePath(
                suggestedName,
                Document.FormatId,
                Document.FilePath);
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            IsBusy = true;
            StatusText = "Saving archive...";
            await _archiveService.SaveAsync(Document, path).ConfigureAwait(true);
            _savedRevision = _currentRevision;
            RecordRecentFile(path);
            RebuildFolderTree(_currentFolder ?? Document.Root);
            OnPropertyChanged(nameof(WindowTitle));
            OnPropertyChanged(nameof(ArchiveDisplayName));
            OnPropertyChanged(nameof(SearchPlaceholder));
            StatusText = $"Saved {Path.GetFileName(path)}";
            return true;
        }
        catch (Exception exception)
        {
            StatusText = "Save failed.";
            _messageBoxService.ShowError("Save Failed", exception.Message);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanOpenPakFolder()
    {
        return !IsBusy &&
               Document?.FilePath is { Length: > 0 } path &&
               File.Exists(path);
    }

    private void OpenPakFolder()
    {
        if (!CanOpenPakFolder() || Document?.FilePath is not { } path)
        {
            return;
        }

        try
        {
            _ = Process.Start(new ProcessStartInfo("explorer.exe")
            {
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true,
            }) ?? throw new InvalidOperationException("Windows could not open File Explorer.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _messageBoxService.ShowError("Open PAK Folder Failed", exception.Message);
        }
    }

    private async Task<bool> ConfirmDocumentReplacementAsync()
    {
        if (Document?.IsDirty != true)
        {
            return true;
        }

        return _messageBoxService.ConfirmSaveChanges(Document.DisplayName) switch
        {
            SaveChangesDecision.Discard => true,
            SaveChangesDecision.Cancel => false,
            SaveChangesDecision.Save => await SaveDocumentAsync(saveAs: false).ConfigureAwait(true),
            _ => false,
        };
    }

    private void CreateFolder()
    {
        CreateFolderIn(_currentFolder);
    }

    private void CreateFolderInContext()
    {
        CreateFolderIn(ResolveContextDestination());
    }

    private void CreateFolderIn(ArchiveFolderNode? destination)
    {
        if (destination is null)
        {
            return;
        }

        var initialName = ArchiveTreeEditor.GetAvailableName(
            destination,
            "New Folder",
            preserveExtension: false);
        try
        {
            var history = CaptureHistory("Create Folder");
            var folder = ArchiveTreeEditor.CreateFolder(destination, initialName);
            RecordMutation(history);
            MarkDirty($"Created folder '{folder.Name}'.");
            if (!ReferenceEquals(destination, _currentFolder))
            {
                NavigateToFolder(destination);
            }
            RefreshAfterMutation(folder);
            RenameRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            _messageBoxService.ShowError("Create Folder Failed", exception.Message);
        }
    }

    private async Task AddFilesAsync()
    {
        await AddFilesToAsync(_currentFolder).ConfigureAwait(true);
    }

    private async Task AddFilesInContextAsync()
    {
        await AddFilesToAsync(ResolveContextDestination()).ConfigureAwait(true);
    }

    private async Task AddFilesToAsync(ArchiveFolderNode? destination)
    {
        if (destination is null)
        {
            return;
        }

        var paths = _fileDialogService.PickFilesToAdd();
        if (paths.Count == 0)
        {
            return;
        }

        await ImportPathsAsync(paths, destination).ConfigureAwait(true);
    }

    private async Task AddFolderAsync()
    {
        await AddFolderToAsync(_currentFolder).ConfigureAwait(true);
    }

    private async Task AddFolderInContextAsync()
    {
        await AddFolderToAsync(ResolveContextDestination()).ConfigureAwait(true);
    }

    private async Task AddFolderToAsync(ArchiveFolderNode? destination)
    {
        if (destination is null)
        {
            return;
        }

        var path = _fileDialogService.PickFolderToAdd();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        await ImportPathsAsync([path], destination).ConfigureAwait(true);
    }

    private void RequestSelectedItemRename()
    {
        RenameRequested?.Invoke(this, EventArgs.Empty);
    }

    public bool CommitItemRename(ArchiveItemViewModel item, string newName)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrWhiteSpace(newName))
        {
            _messageBoxService.ShowError("Rename Failed", "The name cannot be empty.");
            return false;
        }

        if (string.Equals(item.Name, newName, StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            var history = CaptureHistory("Rename");
            ArchiveTreeEditor.Rename(item.Node, newName);
            RecordMutation(history);
            MarkDirty($"Renamed item to '{newName}'.");
            RefreshAfterMutation(item.Node);
            return true;
        }
        catch (Exception exception)
        {
            _messageBoxService.ShowError("Rename Failed", exception.Message);
            return false;
        }
    }

    private void DeleteSelectedItems()
    {
        var items = _selectedItems.ToList();
        if (items.Count == 0)
        {
            return;
        }

        var description = items.Count == 1
            ? $"'{items[0].Name}'"
            : $"these {items.Count} items";
        if (!_messageBoxService.Confirm(
                items.Count == 1 ? "Delete Item" : "Delete Items",
                $"Delete {description} from this archive? Folder contents will also be removed."))
        {
            return;
        }

        try
        {
            var history = CaptureHistory("Delete");
            foreach (var item in items)
            {
                ArchiveTreeEditor.Remove(item.Node);
            }
            RecordMutation(history);
            MarkDirty(items.Count == 1 ? $"Deleted '{items[0].Name}'." : $"Deleted {items.Count} items.");
            RefreshAfterMutation();
        }
        catch (Exception exception)
        {
            _messageBoxService.ShowError("Delete Failed", exception.Message);
        }
    }

    private async Task ExportSelectedItemsAsync()
    {
        var items = _selectedItems.ToList();
        if (items.Count == 0)
        {
            return;
        }

        var directory = _fileDialogService.PickExportDirectory();
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        try
        {
            IsBusy = true;
            StatusText = items.Count == 1 ? $"Exporting {items[0].Name}..." : $"Exporting {items.Count} items...";
            var outputs = new List<string>();
            var failures = new List<string>();
            foreach (var item in items)
            {
                try
                {
                    var output = await Task.Run(() =>
                        _fileTransferService.Export(item.Node, directory)).ConfigureAwait(true);
                    outputs.Add(output);
                }
                catch (Exception exception)
                {
                    failures.Add($"{item.Name}: {exception.Message}");
                }
            }
            StatusText = outputs.Count == 1
                ? $"Exported to {outputs[0]}"
                : $"Exported {outputs.Count} items to {directory}";
            ReportTransferFailures("Some Items Were Not Exported", failures);
        }
        catch (Exception exception)
        {
            StatusText = "Export failed.";
            _messageBoxService.ShowError("Export Failed", exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SaveSelectedModelSkinAsAsync(string? formatId)
    {
        if (!ImageFormatConverter.TryParseFormat(formatId, out var format) ||
            _selectedItems is not [var item] ||
            item.Node is not ArchiveFileNode file ||
            !Path.GetExtension(file.Name).Equals(".mdl", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            IsBusy = true;
            StatusText = $"Reading skins from {file.Name}...";
            var convertedSkins = await Task.Run(() =>
            {
                using var viewer = NativeModelViewer.Create(file.Data, ".mdl");
                var outputs = new List<byte[]>();
                for (var skinIndex = 0; skinIndex < viewer.Statistics.SkinCount; skinIndex++)
                {
                    var skin = viewer.GetSkin(skinIndex) ??
                        throw new InvalidDataException("The model contains an unreadable skin.");
                    outputs.Add(ImageFormatConverter.EncodeRgba(
                        skin.Width, skin.Height, skin.RgbaPixels, format));
                }
                return outputs.Count > 0
                    ? outputs
                    : throw new InvalidDataException("The model does not contain a readable skin.");
            }).ConfigureAwait(true);

            var extension = ImageFormatConverter.ExtensionFor(format);
            var baseName = Path.GetFileNameWithoutExtension(file.Name);
            if (convertedSkins.Count == 1)
            {
                var outputPath = _fileDialogService.PickImageSavePath(
                    baseName + "_skin" + extension,
                    extension.TrimStart('.'));
                if (string.IsNullOrWhiteSpace(outputPath))
                {
                    return;
                }
                await File.WriteAllBytesAsync(outputPath, convertedSkins[0]).ConfigureAwait(true);
                StatusText = $"Saved {Path.GetFileName(outputPath)}";
            }
            else
            {
                var directory = _fileDialogService.PickExportDirectory();
                if (string.IsNullOrWhiteSpace(directory))
                {
                    return;
                }
                var paths = convertedSkins
                    .Select((_, index) => Path.Combine(
                        directory, $"{baseName}_skin{index + 1}{extension}"))
                    .ToList();
                if (paths.Any(File.Exists))
                {
                    throw new IOException("One or more model skin files already exist in that folder.");
                }
                for (var skinIndex = 0; skinIndex < convertedSkins.Count; skinIndex++)
                {
                    await File.WriteAllBytesAsync(
                        paths[skinIndex], convertedSkins[skinIndex]).ConfigureAwait(true);
                }
                StatusText = $"Saved {convertedSkins.Count} skins to {directory}";
            }
        }
        catch (Exception exception)
        {
            StatusText = "Model skin export failed.";
            _messageBoxService.ShowError("Save Model Skins As Failed", exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SaveSelectedBspTexturesAsAsync(string? formatId)
    {
        if (!ImageFormatConverter.TryParseFormat(formatId, out var format) ||
            _selectedItems is not [var item] ||
            item.Node is not ArchiveFileNode file ||
            !Path.GetExtension(file.Name).Equals(".bsp", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var directory = _fileDialogService.PickExportDirectory();
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        try
        {
            IsBusy = true;
            StatusText = $"Reading textures from {file.Name}...";
            var textures = await Task.Run(() =>
            {
                using var viewer = NativeModelViewer.Create(file.Data, ".bsp");
                var outputs = new List<(string Name, byte[] Data)>();
                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var textureIndex = 0; textureIndex < viewer.EmbeddedTextureCount; textureIndex++)
                {
                    var texture = viewer.GetEmbeddedTexture(textureIndex) ??
                        throw new InvalidDataException("The BSP contains an unreadable texture.");
                    var baseName = ImageFormatConverter.SafeTextureFileStem(
                        texture.Name, textureIndex);
                    var uniqueName = baseName;
                    for (var duplicate = 2; !usedNames.Add(uniqueName); duplicate++)
                    {
                        uniqueName = $"{baseName}_{duplicate}";
                    }
                    outputs.Add((
                        uniqueName,
                        ImageFormatConverter.EncodeRgba(
                            texture.Width, texture.Height, texture.RgbaPixels, format)));
                }
                return outputs.Count > 0
                    ? outputs
                    : throw new InvalidDataException("The BSP does not contain readable textures.");
            }).ConfigureAwait(true);

            var extension = ImageFormatConverter.ExtensionFor(format);
            var paths = textures
                .Select(texture => Path.Combine(directory, texture.Name + extension))
                .ToList();
            if (paths.Any(File.Exists))
            {
                throw new IOException("One or more BSP texture files already exist in that folder.");
            }
            for (var index = 0; index < textures.Count; index++)
            {
                await File.WriteAllBytesAsync(paths[index], textures[index].Data).ConfigureAwait(true);
            }
            StatusText = $"Saved {textures.Count} textures to {directory}";
        }
        catch (Exception exception)
        {
            StatusText = "BSP texture export failed.";
            _messageBoxService.ShowError("Save BSP Textures As Failed", exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SaveSelectedWadTexturesAsAsync(string? formatId)
    {
        if (!ImageFormatConverter.TryParseFormat(formatId, out var format) ||
            _selectedItems is not [var item] ||
            item.Node is not ArchiveFileNode file ||
            !Path.GetExtension(file.Name).Equals(".wad", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var directory = _fileDialogService.PickExportDirectory();
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        try
        {
            IsBusy = true;
            StatusText = $"Reading textures from {file.Name}...";
            var textures = await Task.Run(() =>
            {
                var outputs = new List<(string Name, byte[] Data)>();
                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var extracted = WadTextureExtractor.Extract(file.Data);
                for (var textureIndex = 0; textureIndex < extracted.Count; textureIndex++)
                {
                    var texture = extracted[textureIndex];
                    var baseName = ImageFormatConverter.SafeTextureFileStem(
                        texture.Name, textureIndex);
                    var uniqueName = baseName;
                    for (var duplicate = 2; !usedNames.Add(uniqueName); duplicate++)
                    {
                        uniqueName = $"{baseName}_{duplicate}";
                    }
                    outputs.Add((
                        uniqueName,
                        ImageFormatConverter.EncodeRgba(
                            texture.Width, texture.Height, texture.RgbaPixels, format)));
                }
                return outputs.Count > 0
                    ? outputs
                    : throw new InvalidDataException("The WAD does not contain readable mip textures.");
            }).ConfigureAwait(true);

            var extension = ImageFormatConverter.ExtensionFor(format);
            var paths = textures
                .Select(texture => Path.Combine(directory, texture.Name + extension))
                .ToList();
            if (paths.Any(File.Exists))
            {
                throw new IOException("One or more WAD texture files already exist in that folder.");
            }
            for (var index = 0; index < textures.Count; index++)
            {
                await File.WriteAllBytesAsync(paths[index], textures[index].Data).ConfigureAwait(true);
            }
            StatusText = $"Saved {textures.Count} textures to {directory}";
        }
        catch (Exception exception)
        {
            StatusText = "WAD texture export failed.";
            _messageBoxService.ShowError("Save WAD Textures As Failed", exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OpenSelectedItem()
    {
        OpenItem(SelectedItem);
    }

    private void ShowSelectedItemInfo()
    {
        var nodes = _selectedItems.Count > 0
            ? _selectedItems.Select(item => item.Node).ToList()
            : _currentFolder is { } folder
                ? [folder]
                : [];
        if (nodes.Count == 0)
        {
            return;
        }

        _itemInfoWindowService.Show(nodes, ArchiveDisplayName);
    }

    private void CopySelection(bool isCut)
    {
        var nodes = _selectedItems.Select(item => item.Node).ToList();
        if (nodes.Count == 0)
        {
            return;
        }

        IReadOnlyList<string> exportedPaths = [];
        try
        {
            var payload = new ArchiveClipboardPayload(
                Guid.NewGuid(),
                ArchiveTreeEditor.CreateSnapshot(nodes),
                nodes,
                isCut);
            var data = new DataObject();
            data.SetData(ClipboardFormat, payload.Id.ToString("D"));
            try
            {
                exportedPaths = _fileTransferService.ExportToTemporaryLocation(nodes);
                data.SetData(DataFormats.FileDrop, exportedPaths.ToArray());
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Internal archive clipboard operations remain available even when an
                // archive name cannot be represented on the Windows file system.
            }

            try
            {
                Clipboard.SetDataObject(data, copy: true);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _fileTransferService.ReleaseTemporaryLocation(exportedPaths);
                _messageBoxService.ShowError(
                    isCut ? "Cut Failed" : "Copy Failed",
                    $"The Windows clipboard is unavailable. {exception.Message}");
                return;
            }

            _fileTransferService.ReleaseTemporaryLocation(_clipboardExportedPaths);
            _clipboardPayload = payload;
            _clipboardExportedPaths = exportedPaths;

            StatusText = isCut
                ? $"Cut {nodes.Count} item(s)."
                : $"Copied {nodes.Count} item(s).";
            CommandManager.InvalidateRequerySuggested();
        }
        catch (Exception exception)
        {
            _fileTransferService.ReleaseTemporaryLocation(exportedPaths);
            _messageBoxService.ShowError(
                isCut ? "Cut Failed" : "Copy Failed",
                exception.Message);
        }
    }

    private async Task PasteAsync()
    {
        if (_currentFolder is null)
        {
            return;
        }

        var payload = GetOwnedClipboardPayload();
        if (payload is not null)
        {
            try
            {
                if (payload.IsCut && payload.Originals.All(node => ReferenceEquals(node.Parent, _currentFolder)))
                {
                    ClearClipboardPayload(payload);
                    StatusText = "The cut items are already in this folder.";
                    return;
                }

                var history = CaptureHistory(payload.IsCut ? "Move" : "Paste");
                var inserted = payload.IsCut
                    ? ArchiveTreeEditor.MoveTo(payload.Originals, _currentFolder)
                    : ArchiveTreeEditor.CopyTo(payload.Templates, _currentFolder);
                if (inserted.Count == 0)
                {
                    return;
                }

                if (payload.IsCut)
                {
                    ClearClipboardPayload(payload);
                }

                RecordMutation(history);
                MarkDirty(payload.IsCut
                    ? $"Moved {inserted.Count} item(s)."
                    : $"Pasted {inserted.Count} item(s).");
                RefreshAfterMutation(inserted[0]);
            }
            catch (Exception exception)
            {
                _messageBoxService.ShowError("Paste Failed", exception.Message);
            }
            return;
        }

        var paths = GetClipboardFileDropPaths();
        if (paths.Count > 0)
        {
            await ImportPathsAsync(paths).ConfigureAwait(true);
        }
    }

    private void NavigateUp()
    {
        if (_currentFolder?.Parent is { } parent)
        {
            NavigateToFolder(parent);
        }
    }

    private void NavigateToFolder(ArchiveFolderNode folder)
    {
        if (ReferenceEquals(folder, _currentFolder) ||
            !_folderLookup.TryGetValue(folder, out var folderViewModel))
        {
            return;
        }

        if (IsSearchActive)
        {
            SearchText = string.Empty;
        }
        if (_currentFolder is { } current)
        {
            _backHistory.Push(current);
            _forwardHistory.Clear();
        }
        SelectFolderCore(folderViewModel);
        folderViewModel.IsExpanded = true;
        folderViewModel.IsSelected = true;
    }

    private void NavigateBack()
    {
        if (TryPopHistory(_backHistory, out var folder))
        {
            if (_currentFolder is { } current)
            {
                _forwardHistory.Push(current);
            }
            SelectFolderFromHistory(folder);
        }
    }

    private void NavigateForward()
    {
        if (TryPopHistory(_forwardHistory, out var folder))
        {
            if (_currentFolder is { } current)
            {
                _backHistory.Push(current);
            }
            SelectFolderFromHistory(folder);
        }
    }

    private void SelectFolderFromHistory(ArchiveFolderNode folder)
    {
        if (_folderLookup.TryGetValue(folder, out var folderViewModel))
        {
            SelectFolderCore(folderViewModel);
            folderViewModel.IsExpanded = true;
            folderViewModel.IsSelected = true;
        }
    }

    private bool TryPopHistory(Stack<ArchiveFolderNode> history, out ArchiveFolderNode folder)
    {
        while (history.TryPop(out var candidate))
        {
            if (!ReferenceEquals(candidate, _currentFolder) && _folderLookup.ContainsKey(candidate))
            {
                folder = candidate;
                return true;
            }
        }

        folder = null!;
        return false;
    }

    private bool CanNavigateBack() =>
        !IsBusy && _backHistory.Any(folder =>
            !ReferenceEquals(folder, _currentFolder) && _folderLookup.ContainsKey(folder));

    private bool CanNavigateForward() =>
        !IsBusy && _forwardHistory.Any(folder =>
            !ReferenceEquals(folder, _currentFolder) && _folderLookup.ContainsKey(folder));

    private bool CanPaste()
    {
        if (!CanModifyCurrentFolder())
        {
            return false;
        }

        return GetOwnedClipboardPayload() is not null || GetClipboardFileDropPaths().Count > 0;
    }

    private ArchiveClipboardPayload? GetOwnedClipboardPayload()
    {
        if (_clipboardPayload is null)
        {
            return null;
        }

        try
        {
            var marker = Clipboard.GetData(ClipboardFormat) as string;
            if (!string.Equals(marker, _clipboardPayload.Id.ToString("D"), StringComparison.Ordinal))
            {
                _fileTransferService.ReleaseTemporaryLocation(_clipboardExportedPaths);
                _clipboardExportedPaths = [];
                _clipboardPayload = null;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // If the OS clipboard is temporarily locked, retain the in-process clipboard.
        }

        return _clipboardPayload;
    }

    private void ClearClipboardPayload(ArchiveClipboardPayload payload)
    {
        _fileTransferService.ReleaseTemporaryLocation(_clipboardExportedPaths);
        _clipboardExportedPaths = [];
        _clipboardPayload = null;
        try
        {
            if (string.Equals(
                    Clipboard.GetData(ClipboardFormat) as string,
                    payload.Id.ToString("D"),
                    StringComparison.Ordinal))
            {
                Clipboard.Clear();
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Clipboard cleanup is best effort after completing a move.
        }
        CommandManager.InvalidateRequerySuggested();
    }

    private static IReadOnlyList<string> GetClipboardFileDropPaths()
    {
        try
        {
            return Clipboard.GetData(DataFormats.FileDrop) is string[] paths ? paths : [];
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return [];
        }
    }

    private bool CanSave()
    {
        return Document is { IsDirty: true } && !IsBusy;
    }

    private bool CanModifyCurrentFolder()
    {
        return Document is not null && _currentFolder is not null && !IsBusy;
    }

    private bool CanModifyContextDestination()
    {
        return Document is not null && ResolveContextDestination() is not null && !IsBusy;
    }

    private ArchiveFolderNode? ResolveContextDestination() =>
        _contextTarget as ArchiveFolderNode ?? _currentFolder;

    private bool CanModifySelectedItems()
    {
        return _selectedItems.Count > 0 && !IsBusy;
    }

    private ArchiveFileNode? SelectedFile =>
        _selectedItems is [var item] ? item.Node as ArchiveFileNode : null;

    private bool IsInlineRenameActive => _selectedItems.Any(item => item.IsRenaming);

    private static bool IsPlayableDemo(ArchiveFileNode file) =>
        file.Data.LongLength <= DemoPlaybackHandoff.MaximumSessionBytes &&
        string.Equals(Path.GetExtension(file.Name), ".dem", StringComparison.OrdinalIgnoreCase);

    private bool CanPlayDemoInBrowser()
    {
        return !IsBusy &&
               _selectedItems is [var item] &&
               item.Node is ArchiveFileNode file &&
               IsPlayableDemo(file);
    }

    private void PlayDemoInBrowser()
    {
        if (_selectedItems is [var item] && item.Node is ArchiveFileNode file)
        {
            PlayDemoInBrowser(file);
        }
    }

    /// <summary>
    /// Publishes the demo on a loopback socket and opens the web player on it. The demo is
    /// never uploaded: the browser fetches it back from this machine.
    /// </summary>
    private void PlayDemoInBrowser(ArchiveFileNode file)
    {
        try
        {
            var summary = QuakeDemoInspector.Inspect(file.Data);
            var uri = DemoPlaybackHandoff.BuildLaunchUri(
                new DemoPlaybackAsset(file.Name, file.Data),
                ArchivePackages(summary),
                summary,
                LoopbackAssetServer.Shared);

            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            StatusText = $"Opened {file.Name} in your browser.";
        }
        catch (Exception exception)
        {
            _messageBoxService.ShowError("Play Demo Failed", exception.Message);
        }
    }

    /// <summary>
    /// Offers the open archive to the player only when it actually holds a map the demo
    /// visits, so a large archive is not shipped across for a stock level.
    /// </summary>
    private IReadOnlyList<DemoPlaybackAsset> ArchivePackages(QuakeDemoSummary? summary)
    {
        if (summary is null || Document?.FilePath is not { Length: > 0 } path || !File.Exists(path))
        {
            return [];
        }

        var wanted = new HashSet<string>(
            summary.Segments.Select(segment => segment.Map).Where(map => map.Length > 0),
            StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0 || !ContainsAnyMap(Document.Root, wanted))
        {
            return [];
        }

        var info = new FileInfo(path);
        if (info.Length > DemoPlaybackHandoff.MaximumSessionBytes)
        {
            return [];
        }

        try
        {
            return [new DemoPlaybackAsset(Path.GetFileName(path), File.ReadAllBytes(path))];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool ContainsAnyMap(ArchiveFolderNode folder, HashSet<string> wanted)
    {
        foreach (var file in folder.Files)
        {
            if (string.Equals(Path.GetExtension(file.Name), ".bsp", StringComparison.OrdinalIgnoreCase) &&
                wanted.Contains(Path.GetFileNameWithoutExtension(file.Name)))
            {
                return true;
            }
        }
        return folder.Folders.Any(child => ContainsAnyMap(child, wanted));
    }

    private bool CanSaveSelectedModelSkinAs(string? formatId)
    {
        return !IsBusy &&
               ImageFormatConverter.TryParseFormat(formatId, out _) &&
               _selectedItems is [var item] &&
               item.Node is ArchiveFileNode file &&
               Path.GetExtension(file.Name).Equals(".mdl", StringComparison.OrdinalIgnoreCase);
    }

    private bool CanSaveSelectedBspTexturesAs(string? formatId)
    {
        return !IsBusy &&
               ImageFormatConverter.TryParseFormat(formatId, out _) &&
               _selectedItems is [var item] &&
               item.Node is ArchiveFileNode file &&
               Path.GetExtension(file.Name).Equals(".bsp", StringComparison.OrdinalIgnoreCase);
    }

    private bool CanSaveSelectedWadTexturesAs(string? formatId)
    {
        return !IsBusy &&
               ImageFormatConverter.TryParseFormat(formatId, out _) &&
               _selectedItems is [var item] &&
               item.Node is ArchiveFileNode file &&
               Path.GetExtension(file.Name).Equals(".wad", StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshCurrentFolder()
    {
        RebuildFolderTree(_currentFolder ?? Document?.Root);
        StatusText = "Refreshed current folder.";
    }

    private void ShowAbout()
    {
        _messageBoxService.ShowAbout();
    }

    private void SetViewMode(ArchiveViewMode mode)
    {
        ActiveViewMode = mode;
        StatusText = $"View mode: {mode}";
        CommandManager.InvalidateRequerySuggested();
    }

    private void SetIconZoomLevel(int level)
    {
        var newLevel = Math.Clamp(level, 0, 2);
        if (newLevel == _iconZoomLevel)
        {
            return;
        }

        _iconZoomLevel = newLevel;
        OnPropertyChanged(nameof(LargeIconTileWidth));
        OnPropertyChanged(nameof(LargeIconPreviewWidth));
        OnPropertyChanged(nameof(LargeIconPreviewHeight));
        OnPropertyChanged(nameof(LargeIconGlyphSize));
        OnPropertyChanged(nameof(LargeFolderWidth));
        OnPropertyChanged(nameof(LargeFolderHeight));
        StatusText = $"Icon size: {newLevel + 1} of 3";
        CommandManager.InvalidateRequerySuggested();
    }

    private void LoadDocument(ArchiveDocument document)
    {
        _itemInfoWindowService.CloseAll();
        _contextTarget = null;
        _backHistory.Clear();
        _forwardHistory.Clear();
        _currentFolder = null;
        SelectedFolder = null;
        _undoHistory.Clear();
        _redoHistory.Clear();
        _currentRevision = 0;
        _savedRevision = 0;
        _nextRevision = 0;
        Document = document;
        SearchText = string.Empty;
        RebuildFolderTree(document.Root);
        SetSelectedItems([]);
        OnPropertyChanged(nameof(WindowTitle));
    }

    private void RebuildFolderTree(ArchiveFolderNode? folderToSelect)
    {
        if (Document is null)
        {
            return;
        }

        _folderLookup.Clear();
        FolderRoots.Clear();

        var rootViewModel = BuildFolderTree(Document.Root, Document.DisplayName);
        rootViewModel.IsExpanded = true;
        FolderRoots.Add(rootViewModel);

        var selectedFolder = folderToSelect is not null && _folderLookup.TryGetValue(folderToSelect, out var selected)
            ? selected
            : rootViewModel;
        SelectFolderCore(selectedFolder);
        selectedFolder.IsSelected = true;
    }

    private FolderTreeNodeViewModel BuildFolderTree(ArchiveFolderNode folder, string displayName)
    {
        var viewModel = new FolderTreeNodeViewModel(folder, displayName);
        _folderLookup[folder] = viewModel;

        foreach (var childFolder in folder.Folders.OrderBy(child => child.Name, StringComparer.OrdinalIgnoreCase))
        {
            viewModel.Children.Add(BuildFolderTree(childFolder, childFolder.Name));
        }

        return viewModel;
    }

    private void RebuildCurrentItems(ArchiveNode? nodeToSelect = null)
    {
        CurrentItems.Clear();

        if (_currentFolder is null || Document is null)
        {
            OnPropertyChanged(nameof(SearchResultText));
            return;
        }

        var query = SearchText.Trim();
        var searchAllPaths = query.Length > 0;
        var nodes = searchAllPaths
            ? EnumerateDescendants(Document.Root)
            : _currentFolder.Children;
        var items = nodes
            .Select(node => new ArchiveItemViewModel(
                node,
                _iconService.GetGlyphForNode(node),
                ArchiveThumbnailService.CanCreateThumbnail(node)
                    ? () => _thumbnailService.GetThumbnail(node)
                    : null,
                searchAllPaths ? node.FullPath : null))
            .Where(item => !searchAllPaths || MatchesArchiveSearch(item, query))
            .OrderByDescending(item => item.IsFolder)
            .ThenBy(item => item.Node, Comparer<ArchiveNode>.Create(CompareNodes));
        foreach (var item in items)
        {
            CurrentItems.Add(item);
        }

        OnPropertyChanged(nameof(SearchResultText));

        var selectedItem = nodeToSelect is null
            ? null
            : CurrentItems.FirstOrDefault(item => ReferenceEquals(item.Node, nodeToSelect));
        SetSelectedItems(selectedItem is null ? [] : [selectedItem]);
        StatusText = searchAllPaths
            ? $"{CurrentItems.Count} search result(s) in {ArchiveDisplayName}"
            : $"{CurrentItems.Count} item(s) in {CurrentFolderPath}";
    }

    private static IEnumerable<ArchiveNode> EnumerateDescendants(ArchiveFolderNode root)
    {
        var pending = new Stack<ArchiveNode>(root.Children.Reverse());
        while (pending.TryPop(out var node))
        {
            yield return node;
            if (node is ArchiveFolderNode folder)
            {
                foreach (var child in folder.Children.Reverse())
                {
                    pending.Push(child);
                }
            }
        }
    }

    private static bool MatchesArchiveSearch(ArchiveItemViewModel item, string query)
    {
        var searchable = string.Join(
            " ",
            item.Name,
            Path.GetFileNameWithoutExtension(item.Name),
            item.TypeText,
            item.SearchPath,
            item.SearchableMetadata);
        var compactSearchable = CompactSearchText(searchable);

        return query
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(term =>
            {
                if (term.Contains('*') || term.Contains('?'))
                {
                    var pattern = "^" + Regex.Escape(term)
                        .Replace(@"\*", ".*", StringComparison.Ordinal)
                        .Replace(@"\?", ".", StringComparison.Ordinal) + "$";
                    return searchable
                        .Split([' ', '/', '\\'], StringSplitOptions.RemoveEmptyEntries)
                        .Append(item.SearchPath ?? string.Empty)
                        .Any(part => Regex.IsMatch(
                            part,
                            pattern,
                            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                            TimeSpan.FromMilliseconds(100)));
                }

                var compactTerm = CompactSearchText(term);
                return searchable.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                       compactTerm.Length > 0 &&
                       compactSearchable.Contains(compactTerm, StringComparison.Ordinal);
            });
    }

    private static string CompactSearchText(string value) =>
        new(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());

    private int CompareNodes(ArchiveNode left, ArchiveNode right)
    {
        var comparison = _sortColumn switch
        {
            ArchiveSortColumn.Type => CompareText(GetTypeText(left), GetTypeText(right)),
            ArchiveSortColumn.Details => CompareText(
                ArchiveMetadataInspector.Inspect(left).Summary,
                ArchiveMetadataInspector.Inspect(right).Summary),
            ArchiveSortColumn.Size => CompareValues(GetSize(left), GetSize(right)),
            ArchiveSortColumn.Modified => CompareValues(GetModified(left), GetModified(right)),
            _ => CompareText(left.Name, right.Name),
        };

        if (comparison == 0 && _sortColumn != ArchiveSortColumn.Name)
        {
            comparison = CompareText(left.Name, right.Name);
        }
        return comparison;
    }

    private int CompareText(string left, string right) => _sortDescending
        ? StringComparer.OrdinalIgnoreCase.Compare(right, left)
        : StringComparer.OrdinalIgnoreCase.Compare(left, right);

    private int CompareValues<T>(T left, T right) where T : IComparable<T> => _sortDescending
        ? right.CompareTo(left)
        : left.CompareTo(right);

    private string SortHeader(string title, ArchiveSortColumn column)
    {
        if (_sortColumn != column)
        {
            return title;
        }
        return $"{title} {(_sortDescending ? '▼' : '▲')}";
    }

    private static string GetTypeText(ArchiveNode node) => node switch
    {
        ArchiveFolderNode => "Folder",
        ArchiveFileNode file when string.IsNullOrWhiteSpace(file.Extension) => "File",
        ArchiveFileNode file => $"{file.Extension.TrimStart('.').ToUpperInvariant()} File",
        _ => "Item",
    };

    private static long GetSize(ArchiveNode node) => node is ArchiveFileNode file ? file.Size : 0;

    private static DateTime GetModified(ArchiveNode node) =>
        node is ArchiveFileNode file ? file.ModifiedUtc ?? DateTime.MinValue : DateTime.MinValue;

    private void RefreshAfterMutation(ArchiveNode? nodeToSelect = null)
    {
        if (Document is { } document)
        {
            _itemInfoWindowService.CloseMissingFrom(document.Root);
        }
        var status = StatusText;
        var currentFolder = _currentFolder;
        RebuildFolderTree(currentFolder);
        RebuildCurrentItems(nodeToSelect);
        StatusText = status;
    }

    private void MarkDirty(string status)
    {
        if (Document is null)
        {
            return;
        }

        Document.IsDirty = true;
        OnPropertyChanged(nameof(WindowTitle));
        StatusText = status;
        CommandManager.InvalidateRequerySuggested();
    }

    private ArchiveHistoryEntry CaptureHistory(string action)
    {
        var document = Document
            ?? throw new InvalidOperationException("No archive is open.");
        return new ArchiveHistoryEntry(
            action,
            ArchiveTreeEditor.CreateFolderSnapshot(document.Root),
            _currentRevision);
    }

    private void RecordMutation(ArchiveHistoryEntry history)
    {
        _undoHistory.Add(history);
        if (_undoHistory.Count > MaximumHistoryEntries)
        {
            _undoHistory.RemoveAt(0);
        }
        _redoHistory.Clear();
        _currentRevision = ++_nextRevision;
        CommandManager.InvalidateRequerySuggested();
    }

    private void Undo()
    {
        if (Document is null || _undoHistory.Count == 0)
        {
            return;
        }

        var target = TakeLast(_undoHistory);
        _redoHistory.Add(new ArchiveHistoryEntry(
            target.Action,
            ArchiveTreeEditor.CreateFolderSnapshot(Document.Root),
            _currentRevision));
        TrimHistory(_redoHistory);
        RestoreHistory(target);
        StatusText = $"Undid {target.Action.ToLowerInvariant()}.";
    }

    private void Redo()
    {
        if (Document is null || _redoHistory.Count == 0)
        {
            return;
        }

        var target = TakeLast(_redoHistory);
        _undoHistory.Add(new ArchiveHistoryEntry(
            target.Action,
            ArchiveTreeEditor.CreateFolderSnapshot(Document.Root),
            _currentRevision));
        TrimHistory(_undoHistory);
        RestoreHistory(target);
        StatusText = $"Redid {target.Action.ToLowerInvariant()}.";
    }

    private void RestoreHistory(ArchiveHistoryEntry entry)
    {
        if (Document is null)
        {
            return;
        }

        ArchiveTreeEditor.RestoreFolderSnapshot(Document.Root, entry.Root);
        _itemInfoWindowService.CloseMissingFrom(Document.Root);
        _currentRevision = entry.Revision;
        Document.IsDirty = _currentRevision != _savedRevision;
        if (_clipboardPayload is { } payload)
        {
            ClearClipboardPayload(payload);
        }
        _backHistory.Clear();
        _forwardHistory.Clear();
        _searchText = string.Empty;
        OnPropertyChanged(nameof(SearchText));
        OnPropertyChanged(nameof(IsSearchActive));
        RebuildFolderTree(Document.Root);
        SetSelectedItems([]);
        OnPropertyChanged(nameof(WindowTitle));
        CommandManager.InvalidateRequerySuggested();
    }

    private static T TakeLast<T>(List<T> items)
    {
        var index = items.Count - 1;
        var item = items[index];
        items.RemoveAt(index);
        return item;
    }

    private static void TrimHistory(List<ArchiveHistoryEntry> history)
    {
        if (history.Count > MaximumHistoryEntries)
        {
            history.RemoveAt(0);
        }
    }

    private void RecordRecentFile(string path)
    {
        _recentFilesService.Add(path);
        OnPropertyChanged(nameof(RecentFiles));
    }

    private void ReportTransferFailures(string title, IReadOnlyList<string> failures)
    {
        if (failures.Count == 0)
        {
            return;
        }

        var visibleFailures = failures.Take(5).ToList();
        if (failures.Count > visibleFailures.Count)
        {
            visibleFailures.Add($"...and {failures.Count - visibleFailures.Count} more.");
        }
        _messageBoxService.ShowError(title, string.Join(Environment.NewLine, visibleFailures));
    }

    private async Task ImportPathsAsync(
        IReadOnlyList<string> paths,
        ArchiveFolderNode? targetFolder = null)
    {
        var destination = targetFolder ?? _currentFolder;
        if (destination is null)
        {
            return;
        }

        var history = CaptureHistory(paths.Count == 1 ? "Add Item" : "Add Items");
        var imported = new List<ArchiveNode>();
        var failures = new List<string>();
        try
        {
            IsBusy = true;
            StatusText = paths.Count == 1 ? "Adding item..." : $"Adding {paths.Count} items...";
            foreach (var path in paths)
            {
                try
                {
                    var node = await Task.Run(() =>
                    {
                        var attributes = File.GetAttributes(path);
                        return attributes.HasFlag(FileAttributes.Directory)
                            ? (ArchiveNode)_fileTransferService.ImportDirectory(destination, path)
                            : _fileTransferService.ImportFile(destination, path);
                    }).ConfigureAwait(true);
                    imported.Add(node);
                }
                catch (Exception exception)
                {
                    failures.Add($"{Path.GetFileName(path)}: {exception.Message}");
                }
            }
        }
        finally
        {
            IsBusy = false;
        }

        if (imported.Count > 0)
        {
            RecordMutation(history);
            MarkDirty(imported.Count == 1 ? "Added 1 item." : $"Added {imported.Count} items.");
            RefreshAfterMutation(imported[0]);
        }
        ReportTransferFailures("Some Items Were Not Added", failures);
    }

    private static ArchiveDocument CreateEmptyDocument(string formatId)
    {
        return new ArchiveDocument
        {
            FormatId = formatId,
        };
    }

    private enum ArchiveSortColumn
    {
        Name,
        Type,
        Details,
        Size,
        Modified,
    }

    private sealed record ArchiveClipboardPayload(
        Guid Id,
        IReadOnlyList<ArchiveNode> Templates,
        IReadOnlyList<ArchiveNode> Originals,
        bool IsCut);

    private sealed record ArchiveHistoryEntry(
        string Action,
        ArchiveFolderNode Root,
        int Revision);
}
