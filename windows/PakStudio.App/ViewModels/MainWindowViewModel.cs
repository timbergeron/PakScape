using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using PakStudio.App.Commands;
using PakStudio.App.Services;
using PakStudio.Core.Documents;
using PakStudio.Core.Interfaces;
using PakStudio.Core.Nodes;
using PakStudio.Core.Operations;

namespace PakStudio.App.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
{
    private const string ClipboardFormat = "PakScape.ArchiveClipboardId";

    private readonly IArchiveService _archiveService;
    private readonly IArchiveFileTransferService _fileTransferService;
    private readonly IFileDialogService _fileDialogService;
    private readonly IMessageBoxService _messageBoxService;
    private readonly ITextInputService _textInputService;
    private readonly IRecentFilesService _recentFilesService;
    private readonly IIconService _iconService;
    private readonly ArchiveThumbnailService _thumbnailService;
    private readonly Dictionary<ArchiveFolderNode, FolderTreeNodeViewModel> _folderLookup = [];
    private readonly Stack<ArchiveFolderNode> _backHistory = [];
    private readonly Stack<ArchiveFolderNode> _forwardHistory = [];

    private ArchiveDocument? _document;
    private FolderTreeNodeViewModel? _selectedFolder;
    private ArchiveItemViewModel? _selectedItem;
    private IReadOnlyList<ArchiveItemViewModel> _selectedItems = [];
    private ArchiveFolderNode? _currentFolder;
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

    public MainWindowViewModel(
        IArchiveService archiveService,
        IArchiveFileTransferService fileTransferService,
        IFileDialogService fileDialogService,
        IMessageBoxService messageBoxService,
        ITextInputService textInputService,
        IRecentFilesService recentFilesService,
        IIconService iconService,
        ArchiveThumbnailService thumbnailService)
    {
        _archiveService = archiveService;
        _fileTransferService = fileTransferService;
        _fileDialogService = fileDialogService;
        _messageBoxService = messageBoxService;
        _textInputService = textInputService;
        _recentFilesService = recentFilesService;
        _iconService = iconService;
        _thumbnailService = thumbnailService;

        NewCommand = new AsyncRelayCommand(() => CreateNewArchiveAsync("pak"), () => !IsBusy);
        NewPk3Command = new AsyncRelayCommand(() => CreateNewArchiveAsync("pk3"), () => !IsBusy);
        OpenCommand = new AsyncRelayCommand(OpenAsync, () => !IsBusy);
        OpenRecentCommand = new AsyncRelayCommand<string>(OpenRecentAsync, path =>
            !IsBusy && !string.IsNullOrWhiteSpace(path));
        SaveCommand = new AsyncRelayCommand(SaveAsync, CanSave);
        SaveAsCommand = new AsyncRelayCommand(SaveAsAsync, () => Document is not null && !IsBusy);
        RefreshCommand = new RelayCommand(RefreshCurrentFolder, () => Document is not null && !IsBusy);
        ExitCommand = new RelayCommand(() => Application.Current.MainWindow?.Close());
        AboutCommand = new RelayCommand(ShowAbout);

        NewFolderCommand = new RelayCommand(CreateFolder, CanModifyCurrentFolder);
        AddFilesCommand = new AsyncRelayCommand(AddFilesAsync, CanModifyCurrentFolder);
        AddFolderCommand = new AsyncRelayCommand(AddFolderAsync, CanModifyCurrentFolder);
        RenameCommand = new RelayCommand(RenameSelectedItem, () => _selectedItems.Count == 1 && !IsBusy);
        DeleteCommand = new RelayCommand(DeleteSelectedItems, CanModifySelectedItems);
        ExportCommand = new AsyncRelayCommand(ExportSelectedItemsAsync, CanModifySelectedItems);
        OpenSelectedCommand = new RelayCommand(OpenSelectedItem, () => _selectedItems.Count == 1 && !IsBusy);
        UpCommand = new RelayCommand(NavigateUp, () => _currentFolder?.Parent is not null && !IsBusy);
        BackCommand = new RelayCommand(NavigateBack, CanNavigateBack);
        ForwardCommand = new RelayCommand(NavigateForward, CanNavigateForward);
        CutCommand = new RelayCommand(() => CopySelection(isCut: true), CanModifySelectedItems);
        CopyCommand = new RelayCommand(() => CopySelection(isCut: false), CanModifySelectedItems);
        PasteCommand = new AsyncRelayCommand(PasteAsync, CanPaste);

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

    public string SizeSortHeader => SortHeader("Size", ArchiveSortColumn.Size);

    public string ModifiedSortHeader => SortHeader("Modified", ArchiveSortColumn.Modified);

    public AsyncRelayCommand NewCommand { get; }

    public AsyncRelayCommand NewPk3Command { get; }

    public AsyncRelayCommand OpenCommand { get; }

    public AsyncRelayCommand<string> OpenRecentCommand { get; }

    public AsyncRelayCommand SaveCommand { get; }

    public AsyncRelayCommand SaveAsCommand { get; }

    public RelayCommand RefreshCommand { get; }

    public RelayCommand ExitCommand { get; }

    public RelayCommand AboutCommand { get; }

    public RelayCommand NewFolderCommand { get; }

    public AsyncRelayCommand AddFilesCommand { get; }

    public AsyncRelayCommand AddFolderCommand { get; }

    public RelayCommand RenameCommand { get; }

    public RelayCommand DeleteCommand { get; }

    public AsyncRelayCommand ExportCommand { get; }

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

    public async Task InitializeAsync(string? archivePath = null)
    {
        if (_isInitialized)
        {
            return;
        }

        _isInitialized = true;
        LoadDocument(CreateEmptyDocument("pak"));
        StatusText = "Ready. Open an archive or add files to a new one.";
        if (!string.IsNullOrWhiteSpace(archivePath))
        {
            await OpenPathAsync(archivePath, confirmReplacement: false).ConfigureAwait(true);
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

    private async Task CreateNewArchiveAsync(string formatId)
    {
        if (!await ConfirmDocumentReplacementAsync().ConfigureAwait(true))
        {
            return;
        }

        LoadDocument(CreateEmptyDocument(formatId));
        StatusText = $"Created a new empty {formatId.ToUpperInvariant()} archive.";
    }

    private async Task OpenAsync()
    {
        var path = _fileDialogService.PickArchiveToOpen();
        if (!string.IsNullOrWhiteSpace(path))
        {
            await OpenPathAsync(path, confirmReplacement: true).ConfigureAwait(true);
        }
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

        await OpenPathAsync(path, confirmReplacement: true).ConfigureAwait(true);
    }

    private async Task OpenPathAsync(string path, bool confirmReplacement)
    {
        if (confirmReplacement && !await ConfirmDocumentReplacementAsync().ConfigureAwait(true))
        {
            return;
        }

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
        if (_currentFolder is null)
        {
            return;
        }

        var initialName = ArchiveTreeEditor.GetAvailableName(
            _currentFolder,
            "New Folder",
            preserveExtension: false);
        var name = _textInputService.Prompt("New Folder", "Enter a name for the folder:", initialName)?.Trim();
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
            _messageBoxService.ShowError("Create Folder Failed", exception.Message);
        }
    }

    private async Task AddFilesAsync()
    {
        if (_currentFolder is null)
        {
            return;
        }

        var paths = _fileDialogService.PickFilesToAdd();
        if (paths.Count == 0)
        {
            return;
        }

        await ImportPathsAsync(paths).ConfigureAwait(true);
    }

    private async Task AddFolderAsync()
    {
        if (_currentFolder is null)
        {
            return;
        }

        var path = _fileDialogService.PickFolderToAdd();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        await ImportPathsAsync([path]).ConfigureAwait(true);
    }

    private void RenameSelectedItem()
    {
        var item = SelectedItem;
        if (item is null)
        {
            return;
        }

        var name = _textInputService.Prompt(
            "Rename Item",
            $"Enter a new name for '{item.Name}':",
            item.Name)?.Trim();
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
            _messageBoxService.ShowError("Rename Failed", exception.Message);
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
                $"Delete {description} from this archive? Folder contents will also be removed.\n\nThis cannot be undone."))
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

    private void OpenSelectedItem()
    {
        OpenItem(SelectedItem);
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

    private bool CanModifySelectedItems()
    {
        return _selectedItems.Count > 0 && !IsBusy;
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
    }

    private void LoadDocument(ArchiveDocument document)
    {
        _backHistory.Clear();
        _forwardHistory.Clear();
        _currentFolder = null;
        SelectedFolder = null;
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

        if (_currentFolder is null)
        {
            OnPropertyChanged(nameof(SearchResultText));
            return;
        }

        var children = _currentFolder.Children
            .Where(child => string.IsNullOrWhiteSpace(SearchText)
                || child.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(child => child is ArchiveFolderNode)
            .ThenBy(child => child, Comparer<ArchiveNode>.Create(CompareNodes));
        foreach (var child in children)
        {
            CurrentItems.Add(new ArchiveItemViewModel(
                child,
                _iconService.GetGlyphForNode(child),
                ArchiveThumbnailService.CanCreateThumbnail(child)
                    ? () => _thumbnailService.GetThumbnail(child)
                    : null));
        }

        OnPropertyChanged(nameof(SearchResultText));

        var selectedItem = nodeToSelect is null
            ? null
            : CurrentItems.FirstOrDefault(item => ReferenceEquals(item.Node, nodeToSelect));
        SetSelectedItems(selectedItem is null ? [] : [selectedItem]);
        StatusText = $"{CurrentItems.Count} item(s) in {CurrentFolderPath}";
    }

    private int CompareNodes(ArchiveNode left, ArchiveNode right)
    {
        var comparison = _sortColumn switch
        {
            ArchiveSortColumn.Type => CompareText(GetTypeText(left), GetTypeText(right)),
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
        Size,
        Modified,
    }

    private sealed record ArchiveClipboardPayload(
        Guid Id,
        IReadOnlyList<ArchiveNode> Templates,
        IReadOnlyList<ArchiveNode> Originals,
        bool IsCut);
}
