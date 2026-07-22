using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PakScape.Linux.Models;
using PakScape.Linux.Services;
using PakStudio.Core.Documents;
using PakStudio.Core.Interfaces;
using PakStudio.Core.Nodes;
using PakStudio.Core.Operations;

namespace PakScape.Linux.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly IArchiveService _archiveService;
    private readonly IArchiveFileTransferService _fileTransferService;
    private readonly IUserInteractionService _interactionService;
    private readonly IRecentFilesService _recentFilesService;
    private readonly ArchiveThumbnailService _thumbnailService;
    private readonly Dictionary<ArchiveFolderNode, FolderNodeViewModel> _folderLookup = [];
    private readonly Stack<ArchiveFolderNode> _backHistory = [];
    private readonly Stack<ArchiveFolderNode> _forwardHistory = [];
    private ArchiveDocument? _document;
    private FolderNodeViewModel? _selectedFolder;
    private ArchiveItemViewModel? _selectedItem;
    private List<ArchiveItemViewModel> _selectedItems = [];
    private ArchiveFolderNode? _currentFolder;
    private string _searchText = string.Empty;
    private string _statusText = "Ready";
    private string _selectionStatus = "0 selected";
    private bool _isBusy;
    private ArchiveClipboardPayload? _clipboardPayload;
    private ArchiveClipboardPayload? _pendingClipboardPayload;
    private IReadOnlyList<string> _clipboardExportedPaths = [];
    private IReadOnlyList<string> _pendingClipboardExportedPaths = [];
    private ArchiveViewMode _activeViewMode = ArchiveViewMode.Details;

    public MainWindowViewModel(
        IArchiveService archiveService,
        IArchiveFileTransferService fileTransferService,
        IUserInteractionService interactionService,
        IRecentFilesService recentFilesService,
        ArchiveThumbnailService thumbnailService)
    {
        _archiveService = archiveService;
        _fileTransferService = fileTransferService;
        _interactionService = interactionService;
        _recentFilesService = recentFilesService;
        _thumbnailService = thumbnailService;
    }

    public event EventHandler? CloseRequested;

    public ObservableCollection<FolderNodeViewModel> FolderRoots { get; } = [];

    public ObservableCollection<ArchiveItemViewModel> CurrentItems { get; } = [];

    public ObservableCollection<string> RecentFiles { get; } = [];

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
            }
        }
    }

    public FolderNodeViewModel? SelectedFolder
    {
        get => _selectedFolder;
        set
        {
            if (value is null)
            {
                SetProperty(ref _selectedFolder, null);
            }
            else if (!IsBusy)
            {
                NavigateToFolder(value.Folder);
            }
        }
    }

    public ArchiveItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set => SetProperty(ref _selectedItem, value);
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

    public string SelectionStatus
    {
        get => _selectionStatus;
        private set => SetProperty(ref _selectionStatus, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                NotifyNavigationStateChanged();
            }
        }
    }

    public bool CanGoBack => _backHistory.Count > 0 && !IsBusy;

    public bool CanGoForward => _forwardHistory.Count > 0 && !IsBusy;

    public ArchiveViewMode ActiveViewMode
    {
        get => _activeViewMode;
        private set
        {
            if (SetProperty(ref _activeViewMode, value))
            {
                OnPropertyChanged(nameof(IsLargeIconsView));
                OnPropertyChanged(nameof(IsSmallIconsView));
                OnPropertyChanged(nameof(IsListView));
                OnPropertyChanged(nameof(IsDetailsView));
            }
        }
    }

    public bool IsLargeIconsView => ActiveViewMode == ArchiveViewMode.LargeIcons;

    public bool IsSmallIconsView => ActiveViewMode == ArchiveViewMode.SmallIcons;

    public bool IsListView => ActiveViewMode == ArchiveViewMode.List;

    public bool IsDetailsView => ActiveViewMode == ArchiveViewMode.Details;

    public string WindowTitle
    {
        get
        {
            if (Document is null)
            {
                return "PakScape";
            }

            var dirtyMarker = Document.IsDirty ? " •" : string.Empty;
            return $"{Document.DisplayName}{dirtyMarker} — PakScape";
        }
    }

    public string CurrentFolderPath => _currentFolder?.FullPath ?? "/";

    public async Task InitializeAsync(string? archivePath)
    {
        RefreshRecentFiles();
        LoadDocument(CreateEmptyDocument("pak"));

        if (!string.IsNullOrWhiteSpace(archivePath))
        {
            await OpenPathAsync(archivePath, confirmReplacement: false);
        }
        else
        {
            StatusText = "Ready. Open an archive or add files to a new one.";
        }
    }

    public void SetSelectedItems(IEnumerable<ArchiveItemViewModel> items)
    {
        _selectedItems = items.Distinct().ToList();
        SelectedItem = _selectedItems.Count > 0 ? _selectedItems[0] : null;
        SelectionStatus = _selectedItems.Count switch
        {
            0 => $"{CurrentItems.Count} item(s)",
            1 => $"1 selected: {_selectedItems[0].Name}",
            _ => $"{_selectedItems.Count} selected",
        };
    }

    public IReadOnlyList<ArchiveNode> SelectedNodes =>
        _selectedItems.Select(item => item.Node).ToList();

    public async Task AddDroppedPathsAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count > 0 && CanModifyCurrentFolder())
        {
            await ImportPathsAsync(paths);
        }
    }

    public IReadOnlyList<string> CopySelection(bool isCut)
    {
        var nodes = _selectedItems.Select(item => item.Node).ToList();
        if (nodes.Count == 0 || IsBusy)
        {
            return [];
        }

        try
        {
            CancelPendingClipboardTransfer();
            _pendingClipboardPayload = new ArchiveClipboardPayload(
                Guid.NewGuid(),
                ArchiveTreeEditor.CreateSnapshot(nodes),
                nodes,
                isCut);
        }
        catch (Exception exception)
        {
            _pendingClipboardPayload = null;
            StatusText = $"{(isCut ? "Cut" : "Copy")} failed: {exception.Message}";
            return [];
        }

        try
        {
            _pendingClipboardExportedPaths =
                _fileTransferService.ExportToTemporaryLocation(nodes);
            return _pendingClipboardExportedPaths;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The in-process archive snapshot still supports paste when a temporary
            // file cannot represent an archive item on the host file system.
            StatusText += $" External clipboard export unavailable: {exception.Message}";
            return [];
        }
    }

    public IReadOnlyList<string> PrepareSelectedItemsForTransfer()
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

    public bool HasInternalClipboard => _clipboardPayload is not null;

    public byte[]? InternalClipboardId => _clipboardPayload?.Id.ToByteArray();

    public byte[]? PendingClipboardId => _pendingClipboardPayload?.Id.ToByteArray();

    public void CommitClipboardTransfer()
    {
        if (_pendingClipboardPayload is not { } payload)
        {
            return;
        }

        _fileTransferService.ReleaseTemporaryLocation(_clipboardExportedPaths);
        _clipboardPayload = payload;
        _clipboardExportedPaths = _pendingClipboardExportedPaths;
        _pendingClipboardPayload = null;
        _pendingClipboardExportedPaths = [];
        StatusText = payload.IsCut
            ? $"Cut {payload.Originals.Count} item(s)."
            : $"Copied {payload.Originals.Count} item(s).";
    }

    public void CancelPendingClipboardTransfer()
    {
        _fileTransferService.ReleaseTemporaryLocation(_pendingClipboardExportedPaths);
        _pendingClipboardExportedPaths = [];
        _pendingClipboardPayload = null;
    }

    public async Task<bool> PasteInternalClipboardAsync()
    {
        if (_clipboardPayload is not { } payload || _currentFolder is null || IsBusy)
        {
            return false;
        }

        try
        {
            if (payload.IsCut && payload.Originals.All(node => ReferenceEquals(node.Parent, _currentFolder)))
            {
                ClearInternalClipboard();
                StatusText = "The cut items are already in this folder.";
                return true;
            }

            var inserted = payload.IsCut
                ? ArchiveTreeEditor.MoveTo(payload.Originals, _currentFolder)
                : ArchiveTreeEditor.CopyTo(payload.Templates, _currentFolder);
            if (inserted.Count == 0)
            {
                return false;
            }

            if (payload.IsCut)
            {
                ClearInternalClipboard();
            }
            MarkDirty(payload.IsCut
                ? $"Moved {inserted.Count} item(s)."
                : $"Pasted {inserted.Count} item(s).");
            RefreshAfterMutation(inserted[0]);
            return payload.IsCut;
        }
        catch (Exception exception)
        {
            await _interactionService.ShowErrorAsync("Paste failed", exception.Message);
            return false;
        }
    }

    public void ClearInternalClipboard()
    {
        _fileTransferService.ReleaseTemporaryLocation(_clipboardExportedPaths);
        _clipboardExportedPaths = [];
        _clipboardPayload = null;
    }

    public async Task OpenItemAsync(ArchiveItemViewModel? item)
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
            try
            {
                _fileTransferService.OpenWithDefaultApplication(file);
                StatusText = $"Opened {file.Name} in the default application.";
            }
            catch (Exception exception)
            {
                StatusText = "Could not open the selected file.";
                await _interactionService.ShowErrorAsync("Open failed", exception.Message);
            }
        }
    }

    public async Task<bool> CanCloseAsync()
    {
        if (IsBusy)
        {
            await _interactionService.ShowInfoAsync(
                "Operation in progress",
                "Wait for the current archive operation to finish before closing PakScape.");
            return false;
        }

        return await ConfirmDocumentReplacementAsync();
    }

    [RelayCommand]
    private async Task NewAsync()
    {
        await CreateNewArchiveAsync("pak");
    }

    [RelayCommand]
    private async Task NewPk3Async()
    {
        await CreateNewArchiveAsync("pk3");
    }

    [RelayCommand]
    private async Task OpenAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var path = await _interactionService.PickArchiveToOpenAsync();
        if (!string.IsNullOrWhiteSpace(path))
        {
            await OpenPathAsync(path, confirmReplacement: true);
        }
    }

    [RelayCommand]
    private async Task OpenRecentAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || IsBusy)
        {
            return;
        }

        if (!File.Exists(path))
        {
            await _interactionService.ShowErrorAsync(
                "File not found",
                $"The recent archive no longer exists:\n{path}");
            return;
        }

        await OpenPathAsync(path, confirmReplacement: true);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!IsBusy)
        {
            _ = await SaveDocumentAsync(saveAs: false);
        }
    }

    [RelayCommand]
    private async Task SaveAsAsync()
    {
        if (!IsBusy)
        {
            _ = await SaveDocumentAsync(saveAs: true);
        }
    }

    [RelayCommand]
    private void Exit()
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task NewFolderAsync()
    {
        if (!CanModifyCurrentFolder() || _currentFolder is null)
        {
            return;
        }

        var initialName = ArchiveTreeEditor.GetAvailableName(
            _currentFolder,
            "New Folder",
            preserveExtension: false);
        var name = (await _interactionService.PromptAsync(
            "New folder",
            "Enter a name for the folder:",
            initialName))?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        try
        {
            var folder = ArchiveTreeEditor.CreateFolder(_currentFolder, name);
            MarkDirty($"Created folder '{folder.Name}'.");
            RefreshAfterMutation(folder);
        }
        catch (Exception exception)
        {
            await _interactionService.ShowErrorAsync("Create folder failed", exception.Message);
        }
    }

    [RelayCommand]
    private async Task AddFilesAsync()
    {
        if (!CanModifyCurrentFolder())
        {
            return;
        }

        var paths = await _interactionService.PickFilesToAddAsync();
        if (paths.Count > 0)
        {
            await ImportPathsAsync(paths);
        }
    }

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        if (!CanModifyCurrentFolder())
        {
            return;
        }

        var path = await _interactionService.PickFolderToAddAsync();
        if (!string.IsNullOrWhiteSpace(path))
        {
            await ImportPathsAsync([path]);
        }
    }

    [RelayCommand]
    private async Task RenameAsync()
    {
        if (_selectedItems.Count != 1 || IsBusy)
        {
            return;
        }

        var item = _selectedItems[0];
        var name = (await _interactionService.PromptAsync(
            "Rename item",
            $"Enter a new name for '{item.Name}':",
            item.Name))?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        try
        {
            ArchiveTreeEditor.Rename(item.Node, name);
            MarkDirty($"Renamed item to '{name}'.");
            RefreshAfterMutation(item.Node);
        }
        catch (Exception exception)
        {
            await _interactionService.ShowErrorAsync("Rename failed", exception.Message);
        }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        var items = _selectedItems.ToList();
        if (items.Count == 0 || IsBusy)
        {
            return;
        }

        var description = items.Count == 1
            ? $"'{items[0].Name}'"
            : $"these {items.Count} items";
        var confirmed = await _interactionService.ConfirmAsync(
            items.Count == 1 ? "Delete item" : "Delete items",
            $"Delete {description} from this archive? Folder contents will also be removed.\n\nThis cannot be undone.",
            "Delete");
        if (!confirmed)
        {
            return;
        }

        try
        {
            foreach (var item in items)
            {
                ArchiveTreeEditor.Remove(item.Node);
            }
            MarkDirty(items.Count == 1 ? $"Deleted '{items[0].Name}'." : $"Deleted {items.Count} items.");
            RefreshAfterMutation();
        }
        catch (Exception exception)
        {
            await _interactionService.ShowErrorAsync("Delete failed", exception.Message);
        }
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        var items = _selectedItems.ToList();
        if (items.Count == 0 || IsBusy)
        {
            return;
        }

        var directory = await _interactionService.PickExportDirectoryAsync();
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        IsBusy = true;
        var outputs = new List<string>();
        var failures = new List<string>();
        try
        {
            StatusText = items.Count == 1
                ? $"Exporting {items[0].Name}..."
                : $"Exporting {items.Count} items...";
            foreach (var item in items)
            {
                try
                {
                    outputs.Add(await Task.Run(() =>
                        _fileTransferService.Export(item.Node, directory)));
                }
                catch (Exception exception)
                {
                    failures.Add($"{item.Name}: {exception.Message}");
                }
            }

            StatusText = outputs.Count == 1
                ? $"Exported to {outputs[0]}"
                : $"Exported {outputs.Count} items to {directory}";
        }
        finally
        {
            IsBusy = false;
        }

        await ReportFailuresAsync("Some items were not exported", failures);
    }

    [RelayCommand]
    private async Task OpenSelectedAsync()
    {
        if (_selectedItems.Count == 1)
        {
            await OpenItemAsync(_selectedItems[0]);
        }
    }

    [RelayCommand]
    private void Up()
    {
        if (!IsBusy && _currentFolder?.Parent is { } parent)
        {
            NavigateToFolder(parent);
        }
    }

    [RelayCommand]
    private void Back()
    {
        while (_backHistory.TryPop(out var folder))
        {
            if (!_folderLookup.TryGetValue(folder, out var folderViewModel))
            {
                continue;
            }
            if (_currentFolder is { } current)
            {
                _forwardHistory.Push(current);
            }
            SelectFolderCore(folderViewModel);
            NotifyNavigationStateChanged();
            return;
        }
    }

    [RelayCommand]
    private void Forward()
    {
        while (_forwardHistory.TryPop(out var folder))
        {
            if (!_folderLookup.TryGetValue(folder, out var folderViewModel))
            {
                continue;
            }
            if (_currentFolder is { } current)
            {
                _backHistory.Push(current);
            }
            SelectFolderCore(folderViewModel);
            NotifyNavigationStateChanged();
            return;
        }
    }

    [RelayCommand]
    private void Refresh()
    {
        if (!IsBusy)
        {
            RebuildFolderTree(_currentFolder ?? Document?.Root);
            StatusText = "Refreshed current folder.";
        }
    }

    [RelayCommand]
    private void ShowLargeIcons() => SetViewMode(ArchiveViewMode.LargeIcons);

    [RelayCommand]
    private void ShowSmallIcons() => SetViewMode(ArchiveViewMode.SmallIcons);

    [RelayCommand]
    private void ShowList() => SetViewMode(ArchiveViewMode.List);

    [RelayCommand]
    private void ShowDetails() => SetViewMode(ArchiveViewMode.Details);

    [RelayCommand]
    private async Task AboutAsync()
    {
        await _interactionService.ShowAboutAsync();
    }

    private async Task CreateNewArchiveAsync(string formatId)
    {
        if (IsBusy || !await ConfirmDocumentReplacementAsync())
        {
            return;
        }

        LoadDocument(CreateEmptyDocument(formatId));
        StatusText = $"Created a new empty {formatId.ToUpperInvariant()} archive.";
    }

    private async Task OpenPathAsync(string path, bool confirmReplacement)
    {
        if (confirmReplacement && !await ConfirmDocumentReplacementAsync())
        {
            return;
        }

        try
        {
            IsBusy = true;
            StatusText = "Opening archive...";
            var document = await _archiveService.OpenAsync(path);
            LoadDocument(document);
            RecordRecentFile(path);
            StatusText = $"Opened {Path.GetFileName(path)}";
        }
        catch (Exception exception)
        {
            StatusText = "Open failed.";
            await _interactionService.ShowErrorAsync("Open failed", exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
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
            var extension = Document.FormatId.Equals("pk3", StringComparison.OrdinalIgnoreCase)
                ? ".pk3"
                : ".pak";
            var suggestedName = string.IsNullOrWhiteSpace(Document.FilePath)
                ? $"Untitled{extension}"
                : Path.GetFileName(Document.FilePath);
            path = await _interactionService.PickArchiveSavePathAsync(
                suggestedName,
                Document.FormatId);
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            IsBusy = true;
            StatusText = "Saving archive...";
            await _archiveService.SaveAsync(Document, path);
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
            await _interactionService.ShowErrorAsync("Save failed", exception.Message);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<bool> ConfirmDocumentReplacementAsync()
    {
        if (Document?.IsDirty != true)
        {
            return true;
        }

        return await _interactionService.ConfirmSaveChangesAsync(Document.DisplayName) switch
        {
            SaveChangesDecision.Discard => true,
            SaveChangesDecision.Cancel => false,
            SaveChangesDecision.Save => await SaveDocumentAsync(saveAs: false),
            _ => false,
        };
    }

    private void LoadDocument(ArchiveDocument document)
    {
        _backHistory.Clear();
        _forwardHistory.Clear();
        _currentFolder = null;
        _selectedFolder = null;
        CurrentItems.Clear();
        SetSelectedItems([]);
        _thumbnailService.Reset();
        NotifyNavigationStateChanged();
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

        var rootViewModel = BuildFolderTree(
            Document.Root,
            Document.DisplayName,
            isExpanded: true);
        FolderRoots.Add(rootViewModel);
        var selected = folderToSelect is not null && _folderLookup.TryGetValue(folderToSelect, out var match)
            ? match
            : rootViewModel;
        SelectFolderCore(selected);
    }

    private FolderNodeViewModel BuildFolderTree(
        ArchiveFolderNode folder,
        string displayName,
        bool isExpanded = false)
    {
        var viewModel = new FolderNodeViewModel(folder, displayName, isExpanded);
        _folderLookup[folder] = viewModel;
        foreach (var child in folder.Folders.OrderBy(
                     candidate => candidate.Name,
                     StringComparer.OrdinalIgnoreCase))
        {
            viewModel.Children.Add(BuildFolderTree(child, child.Name));
        }
        return viewModel;
    }

    private void SelectFolderCore(FolderNodeViewModel folder)
    {
        _selectedFolder = folder;
        OnPropertyChanged(nameof(SelectedFolder));
        _currentFolder = folder.Folder;
        OnPropertyChanged(nameof(CurrentFolderPath));
        RebuildCurrentItems();
    }

    private void RebuildCurrentItems(ArchiveNode? nodeToSelect = null)
    {
        CurrentItems.Clear();
        if (_currentFolder is null)
        {
            OnPropertyChanged(nameof(SearchResultText));
            return;
        }

        var items = _currentFolder.Folders.Cast<ArchiveNode>()
            .Concat(_currentFolder.Files)
            .Select(node => new ArchiveItemViewModel(
                node,
                ArchiveThumbnailService.CanCreateThumbnail(node)
                    ? () => _thumbnailService.GetThumbnail(node)
                    : null))
            .Where(item => string.IsNullOrWhiteSpace(SearchText) ||
                           item.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                           item.TypeText.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.IsFolder)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            CurrentItems.Add(item);
        }

        OnPropertyChanged(nameof(SearchResultText));

        var selectedItem = nodeToSelect is null
            ? null
            : CurrentItems.FirstOrDefault(item => ReferenceEquals(item.Node, nodeToSelect));
        SetSelectedItems(selectedItem is null ? [] : [selectedItem]);
        StatusText = string.IsNullOrWhiteSpace(SearchText)
            ? $"{CurrentItems.Count} item(s) in {CurrentFolderPath}"
            : $"{CurrentItems.Count} matching item(s)";
    }

    private void RefreshAfterMutation(ArchiveNode? nodeToSelect = null)
    {
        var status = StatusText;
        var currentFolder = _currentFolder;
        CurrentItems.Clear();
        SetSelectedItems([]);
        _thumbnailService.Reset();
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
    }

    private void NavigateToFolder(ArchiveFolderNode folder)
    {
        if (ReferenceEquals(folder, _currentFolder) ||
            !_folderLookup.TryGetValue(folder, out var folderViewModel))
        {
            return;
        }
        if (_currentFolder is { } current)
        {
            _backHistory.Push(current);
            _forwardHistory.Clear();
        }
        SelectFolderCore(folderViewModel);
        NotifyNavigationStateChanged();
    }

    private bool CanModifyCurrentFolder()
    {
        return Document is not null && _currentFolder is not null && !IsBusy;
    }

    private void NotifyNavigationStateChanged()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }

    private void SetViewMode(ArchiveViewMode mode)
    {
        ActiveViewMode = mode;
        StatusText = $"View mode: {mode}";
    }

    private void RecordRecentFile(string path)
    {
        _recentFilesService.Add(path);
        RefreshRecentFiles();
    }

    private void RefreshRecentFiles()
    {
        RecentFiles.Clear();
        foreach (var path in _recentFilesService.GetRecentFiles())
        {
            RecentFiles.Add(path);
        }
    }

    private async Task ImportPathsAsync(IReadOnlyList<string> paths)
    {
        if (_currentFolder is null)
        {
            return;
        }

        var destination = _currentFolder;
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
                    });
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
            MarkDirty(imported.Count == 1 ? "Added 1 item." : $"Added {imported.Count} items.");
            RefreshAfterMutation(imported[0]);
        }
        await ReportFailuresAsync("Some items were not added", failures);
    }

    private async Task ReportFailuresAsync(string title, List<string> failures)
    {
        if (failures.Count == 0)
        {
            return;
        }

        var visible = failures.Take(5).ToList();
        if (failures.Count > visible.Count)
        {
            visible.Add($"...and {failures.Count - visible.Count} more.");
        }
        await _interactionService.ShowErrorAsync(title, string.Join(Environment.NewLine, visible));
    }

    private static ArchiveDocument CreateEmptyDocument(string formatId)
    {
        return new ArchiveDocument { FormatId = formatId };
    }

    private sealed record ArchiveClipboardPayload(
        Guid Id,
        IReadOnlyList<ArchiveNode> Templates,
        IReadOnlyList<ArchiveNode> Originals,
        bool IsCut);
}
