using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PakScape.Linux.Models;
using PakScape.Linux.Services;
using PakStudio.Core.Documents;
using PakStudio.Core.Interfaces;
using PakStudio.Core.Models;
using PakStudio.Core.Nodes;
using PakStudio.Core.Operations;
using PakStudio.Core.Playback;
using PakStudio.Core.Preview;

namespace PakScape.Linux.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private const int MaximumHistoryEntries = 100;

    private readonly IArchiveService _archiveService;
    private readonly IArchiveFileTransferService _fileTransferService;
    private readonly IUserInteractionService _interactionService;
    private readonly IRecentFilesService _recentFilesService;
    private readonly ArchiveThumbnailService _thumbnailService;
    private readonly Dictionary<ArchiveFolderNode, FolderNodeViewModel> _folderLookup = [];
    private readonly Stack<ArchiveFolderNode> _backHistory = [];
    private readonly Stack<ArchiveFolderNode> _forwardHistory = [];
    private readonly List<ArchiveHistoryEntry> _undoHistory = [];
    private readonly List<ArchiveHistoryEntry> _redoHistory = [];
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
    private ArchiveNode? _contextTarget;
    private int _iconZoomLevel = 1;
    private int _nextRevision;
    private int _currentRevision;
    private int _savedRevision;

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

    public event EventHandler? GetInfoRequested;

    public event EventHandler? RenameRequested;

    public event Action<string>? NewWindowRequested;

    public event Action<string>? OpenWindowRequested;

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
                OpenPakFolderCommand.NotifyCanExecuteChanged();
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

    public string SearchPlaceholder => $"Search all paths in {ArchiveDisplayName}";

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
                ZoomInIconsCommand.NotifyCanExecuteChanged();
                ZoomOutIconsCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsLargeIconsView => ActiveViewMode == ArchiveViewMode.LargeIcons;

    public bool IsSmallIconsView => ActiveViewMode == ArchiveViewMode.SmallIcons;

    public bool IsListView => ActiveViewMode == ArchiveViewMode.List;

    public bool IsDetailsView => ActiveViewMode == ArchiveViewMode.Details;

    public double LargeIconCardWidth => _iconZoomLevel switch { 0 => 104, 2 => 164, _ => 128 };

    public double LargeIconPreviewWidth => _iconZoomLevel switch { 0 => 64, 2 => 120, _ => 88 };

    public double LargeIconPreviewHeight => _iconZoomLevel switch { 0 => 54, 2 => 96, _ => 72 };

    public double LargeIconFontSize => _iconZoomLevel switch { 0 => 32, 2 => 54, _ => 42 };

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

    public bool HasModelSkinSaveOptions =>
        _selectedItems is [var item] &&
        item.Node is ArchiveFileNode file &&
        Path.GetExtension(file.Name).Equals(".mdl", StringComparison.OrdinalIgnoreCase);

    public bool HasImageSaveOptions =>
        _selectedItems is [var item] &&
        item.Node is ArchiveFileNode file &&
        ImageFormatConverter.IsSupportedSource(file.Name);

    public bool HasBspTextureSaveOptions =>
        _selectedItems is [var item] &&
        item.Node is ArchiveFileNode file &&
        Path.GetExtension(file.Name).Equals(".bsp", StringComparison.OrdinalIgnoreCase);

    public bool HasWadTextureSaveOptions =>
        _selectedItems is [var item] &&
        item.Node is ArchiveFileNode file &&
        Path.GetExtension(file.Name).Equals(".wad", StringComparison.OrdinalIgnoreCase);

    public bool HasSkyboxPreview =>
        _selectedItems is [var item] && SkyboxFaceSet.Find(item.Node) is not null;

    public async Task InitializeAsync(string? archivePath, string initialFormatId = "pak")
    {
        RefreshRecentFiles();
        LoadDocument(CreateEmptyDocument(initialFormatId));

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
        SaveImageAsCommand.NotifyCanExecuteChanged();
        SaveModelSkinAsCommand.NotifyCanExecuteChanged();
        SaveBspTexturesAsCommand.NotifyCanExecuteChanged();
        SaveWadTexturesAsCommand.NotifyCanExecuteChanged();
        PlayDemoInBrowserCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        GetInfoCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasModelSkinSaveOptions));
        OnPropertyChanged(nameof(HasImageSaveOptions));
        OnPropertyChanged(nameof(HasBspTextureSaveOptions));
        OnPropertyChanged(nameof(HasWadTextureSaveOptions));
        OnPropertyChanged(nameof(HasSkyboxPreview));
    }

    public IReadOnlyList<ArchiveNode> SelectedNodes =>
        _selectedItems.Select(item => item.Node).ToList();

    public IReadOnlyList<ArchiveNode> InfoNodes =>
        _selectedItems.Count > 0
            ? SelectedNodes
            : _currentFolder is { } folder ? [folder] : [];

    public void SetContextTarget(ArchiveNode? node) => _contextTarget = node;

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
            var history = CaptureHistory(payload.IsCut ? "Move" : "Paste");
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
            RecordMutation(history);
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
            // A demo has a better destination than whichever application claims .dem.
            if (IsPlayableDemo(file))
            {
                await LaunchDemoInBrowserAsync(file);
                return;
            }

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
    private void New() => NewWindowRequested?.Invoke("pak");

    [RelayCommand]
    private void NewPk3() => NewWindowRequested?.Invoke("pk3");

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
            OpenWindowRequested?.Invoke(path);
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

        OpenWindowRequested?.Invoke(path);
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

    [RelayCommand(CanExecute = nameof(CanOpenPakFolder))]
    private async Task OpenPakFolderAsync()
    {
        if (!CanOpenPakFolder() || Document?.FilePath is not { } archivePath)
        {
            return;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(archivePath));
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "xdg-open",
                UseShellExecute = false,
                ArgumentList = { directory },
            });
            if (process is null)
            {
                throw new InvalidOperationException("The desktop file manager could not be started.");
            }
            StatusText = $"Opened {directory}";
        }
        catch (Exception exception)
        {
            await _interactionService.ShowErrorAsync("Open PAK Folder failed", exception.Message);
        }
    }

    private bool CanOpenPakFolder()
    {
        if (IsBusy || Document?.FilePath is not { } path)
        {
            return false;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        return !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory);
    }

    [RelayCommand]
    private void Exit()
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
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

    private bool CanUndo() => _undoHistory.Count > 0 && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRedo))]
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

    private bool CanRedo() => _redoHistory.Count > 0 && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanGetInfo))]
    private void GetInfo() => GetInfoRequested?.Invoke(this, EventArgs.Empty);

    private bool CanGetInfo() => !IsBusy && InfoNodes.Count > 0;

    [RelayCommand]
    private async Task ContextNewFolderAsync() =>
        await CreateFolderInAsync(ResolveContextDestination());

    [RelayCommand]
    private async Task ContextAddFilesAsync() =>
        await AddFilesToAsync(ResolveContextDestination());

    [RelayCommand]
    private async Task ContextAddFolderAsync() =>
        await AddFolderToAsync(ResolveContextDestination());

    [RelayCommand]
    private async Task NewFolderAsync() => await CreateFolderInAsync(_currentFolder);

    private async Task CreateFolderInAsync(ArchiveFolderNode? destination)
    {
        if (!CanModifyCurrentFolder() || destination is null)
        {
            return;
        }

        var initialName = ArchiveTreeEditor.GetAvailableName(
            destination,
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
            var history = CaptureHistory("Create Folder");
            var folder = ArchiveTreeEditor.CreateFolder(destination, name);
            RecordMutation(history);
            MarkDirty($"Created folder '{folder.Name}'.");
            if (!ReferenceEquals(destination, _currentFolder))
            {
                NavigateToFolder(destination);
            }
            RefreshAfterMutation(folder);
        }
        catch (Exception exception)
        {
            await _interactionService.ShowErrorAsync("Create folder failed", exception.Message);
        }
    }

    [RelayCommand]
    private async Task AddFilesAsync() => await AddFilesToAsync(_currentFolder);

    private async Task AddFilesToAsync(ArchiveFolderNode? destination)
    {
        if (!CanModifyCurrentFolder() || destination is null)
        {
            return;
        }

        var paths = await _interactionService.PickFilesToAddAsync();
        if (paths.Count > 0)
        {
            await ImportPathsAsync(paths, destination);
        }
    }

    [RelayCommand]
    private async Task AddFolderAsync() => await AddFolderToAsync(_currentFolder);

    private async Task AddFolderToAsync(ArchiveFolderNode? destination)
    {
        if (!CanModifyCurrentFolder() || destination is null)
        {
            return;
        }

        var path = await _interactionService.PickFolderToAddAsync();
        if (!string.IsNullOrWhiteSpace(path))
        {
            await ImportPathsAsync([path], destination);
        }
    }

    [RelayCommand]
    private void Rename()
    {
        if (_selectedItems.Count != 1 || IsBusy)
        {
            return;
        }

        var item = _selectedItems[0];
        item.BeginRenaming();
        RenameRequested?.Invoke(this, EventArgs.Empty);
    }

    public async Task<bool> CommitItemRenameAsync(ArchiveItemViewModel item, string proposedName)
    {
        var name = proposedName.Trim();
        if (string.IsNullOrEmpty(name))
        {
            await _interactionService.ShowErrorAsync("Rename failed", "The name cannot be empty.");
            return false;
        }

        try
        {
            var history = CaptureHistory("Rename");
            ArchiveTreeEditor.Rename(item.Node, name);
            RecordMutation(history);
            MarkDirty($"Renamed item to '{name}'.");
            item.EndRenaming();
            RefreshAfterMutation(item.Node);
            return true;
        }
        catch (Exception exception)
        {
            await _interactionService.ShowErrorAsync("Rename failed", exception.Message);
            return false;
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
            $"Delete {description} from this archive? Folder contents will also be removed.",
            "Delete");
        if (!confirmed)
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

    [RelayCommand(CanExecute = nameof(CanSaveImageAs))]
    private async Task SaveImageAsAsync(string? formatId)
    {
        if (!ImageFormatConverter.TryParseFormat(formatId, out var format) ||
            _selectedItems is not [var item] ||
            item.Node is not ArchiveFileNode file ||
            !ImageFormatConverter.IsSupportedSource(file.Name))
        {
            return;
        }

        var extension = ImageFormatConverter.ExtensionFor(format);
        var suggestedName = Path.GetFileNameWithoutExtension(file.Name) + extension;
        var outputPath = await _interactionService.PickImageSavePathAsync(
            suggestedName,
            extension.TrimStart('.'));
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return;
        }

        try
        {
            IsBusy = true;
            StatusText = $"Converting {file.Name}...";
            var converted = await Task.Run(() =>
                ImageFormatConverter.Convert(file.Name, file.Data, format));
            await File.WriteAllBytesAsync(outputPath, converted);
            StatusText = $"Saved {Path.GetFileName(outputPath)}";
        }
        catch (Exception exception)
        {
            StatusText = "Image conversion failed.";
            await _interactionService.ShowErrorAsync("Save Image As failed", exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSaveImageAs(string? formatId)
    {
        return !IsBusy &&
               ImageFormatConverter.TryParseFormat(formatId, out _) &&
               _selectedItems is [var item] &&
               item.Node is ArchiveFileNode file &&
               ImageFormatConverter.IsSupportedSource(file.Name);
    }

    [RelayCommand(CanExecute = nameof(CanSaveModelSkinAs))]
    private async Task SaveModelSkinAsAsync(string? formatId)
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
            });

            var extension = ImageFormatConverter.ExtensionFor(format);
            var baseName = Path.GetFileNameWithoutExtension(file.Name);
            if (convertedSkins.Count == 1)
            {
                var outputPath = await _interactionService.PickImageSavePathAsync(
                    baseName + "_skin" + extension,
                    extension.TrimStart('.'));
                if (string.IsNullOrWhiteSpace(outputPath))
                {
                    return;
                }
                await File.WriteAllBytesAsync(outputPath, convertedSkins[0]);
                StatusText = $"Saved {Path.GetFileName(outputPath)}";
            }
            else
            {
                var directory = await _interactionService.PickExportDirectoryAsync();
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
                    await File.WriteAllBytesAsync(paths[skinIndex], convertedSkins[skinIndex]);
                }
                StatusText = $"Saved {convertedSkins.Count} skins to {directory}";
            }
        }
        catch (Exception exception)
        {
            StatusText = "Model skin export failed.";
            await _interactionService.ShowErrorAsync("Save Model Skins As failed", exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSaveModelSkinAs(string? formatId)
    {
        return !IsBusy &&
               ImageFormatConverter.TryParseFormat(formatId, out _) &&
               _selectedItems is [var item] &&
               item.Node is ArchiveFileNode file &&
               Path.GetExtension(file.Name).Equals(".mdl", StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand(CanExecute = nameof(CanSaveBspTexturesAs))]
    private async Task SaveBspTexturesAsAsync(string? formatId)
    {
        if (!ImageFormatConverter.TryParseFormat(formatId, out var format) ||
            _selectedItems is not [var item] ||
            item.Node is not ArchiveFileNode file ||
            !Path.GetExtension(file.Name).Equals(".bsp", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var directory = await _interactionService.PickExportDirectoryAsync();
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
            });

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
                await File.WriteAllBytesAsync(paths[index], textures[index].Data);
            }
            StatusText = $"Saved {textures.Count} textures to {directory}";
        }
        catch (Exception exception)
        {
            StatusText = "BSP texture export failed.";
            await _interactionService.ShowErrorAsync(
                "Save BSP Textures As failed", exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSaveBspTexturesAs(string? formatId)
    {
        return !IsBusy &&
               ImageFormatConverter.TryParseFormat(formatId, out _) &&
               _selectedItems is [var item] &&
               item.Node is ArchiveFileNode file &&
               Path.GetExtension(file.Name).Equals(".bsp", StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand(CanExecute = nameof(CanSaveWadTexturesAs))]
    private async Task SaveWadTexturesAsAsync(string? formatId)
    {
        if (!ImageFormatConverter.TryParseFormat(formatId, out var format) ||
            _selectedItems is not [var item] ||
            item.Node is not ArchiveFileNode file ||
            !Path.GetExtension(file.Name).Equals(".wad", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var directory = await _interactionService.PickExportDirectoryAsync();
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
            });

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
                await File.WriteAllBytesAsync(paths[index], textures[index].Data);
            }
            StatusText = $"Saved {textures.Count} textures to {directory}";
        }
        catch (Exception exception)
        {
            StatusText = "WAD texture export failed.";
            await _interactionService.ShowErrorAsync(
                "Save WAD Textures As failed", exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSaveWadTexturesAs(string? formatId)
    {
        return !IsBusy &&
               ImageFormatConverter.TryParseFormat(formatId, out _) &&
               _selectedItems is [var item] &&
               item.Node is ArchiveFileNode file &&
               Path.GetExtension(file.Name).Equals(".wad", StringComparison.OrdinalIgnoreCase);
    }

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

    [RelayCommand(CanExecute = nameof(CanPlayDemoInBrowser))]
    private async Task PlayDemoInBrowserAsync()
    {
        if (_selectedItems is [var item] && item.Node is ArchiveFileNode file)
        {
            await LaunchDemoInBrowserAsync(file);
        }
    }

    /// <summary>
    /// Publishes the demo on a loopback socket and opens the web player on it. The demo is
    /// never uploaded: the browser fetches it back from this machine.
    /// </summary>
    private async Task LaunchDemoInBrowserAsync(ArchiveFileNode file)
    {
        try
        {
            var summary = QuakeDemoInspector.Inspect(file.Data);
            var uri = DemoPlaybackHandoff.BuildLaunchUri(
                new DemoPlaybackAsset(file.Name, file.Data),
                ArchivePackages(summary),
                summary,
                LoopbackAssetServer.Shared);

            Process.Start(new ProcessStartInfo("xdg-open", uri.AbsoluteUri) { UseShellExecute = false });
            StatusText = $"Opened {file.Name} in your browser.";
        }
        catch (Exception exception)
        {
            await ReportFailuresAsync("Play Demo Failed", [exception.Message]);
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

        if (new FileInfo(path).Length > DemoPlaybackHandoff.MaximumSessionBytes)
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

    [RelayCommand(CanExecute = nameof(CanZoomInIcons))]
    private void ZoomInIcons() => SetIconZoomLevel(_iconZoomLevel + 1);

    private bool CanZoomInIcons() =>
        ActiveViewMode == ArchiveViewMode.LargeIcons && _iconZoomLevel < 2 && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanZoomOutIcons))]
    private void ZoomOutIcons() => SetIconZoomLevel(_iconZoomLevel - 1);

    private bool CanZoomOutIcons() =>
        ActiveViewMode == ArchiveViewMode.LargeIcons && _iconZoomLevel > 0 && !IsBusy;

    private void SetIconZoomLevel(int level)
    {
        _iconZoomLevel = Math.Clamp(level, 0, 2);
        OnPropertyChanged(nameof(LargeIconCardWidth));
        OnPropertyChanged(nameof(LargeIconPreviewWidth));
        OnPropertyChanged(nameof(LargeIconPreviewHeight));
        OnPropertyChanged(nameof(LargeIconFontSize));
        ZoomInIconsCommand.NotifyCanExecuteChanged();
        ZoomOutIconsCommand.NotifyCanExecuteChanged();
        StatusText = $"Large icon size: {(_iconZoomLevel == 0 ? "Small" : _iconZoomLevel == 2 ? "Large" : "Medium")}";
    }

    [RelayCommand]
    private async Task AboutAsync()
    {
        await _interactionService.ShowAboutAsync();
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
            _savedRevision = _currentRevision;
            RecordRecentFile(path);
            RebuildFolderTree(_currentFolder ?? Document.Root);
            OnPropertyChanged(nameof(WindowTitle));
            OnPropertyChanged(nameof(ArchiveDisplayName));
            OnPropertyChanged(nameof(SearchPlaceholder));
            OpenPakFolderCommand.NotifyCanExecuteChanged();
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
        _undoHistory.Clear();
        _redoHistory.Clear();
        _currentRevision = 0;
        _savedRevision = 0;
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
                ArchiveThumbnailService.CanCreateThumbnail(node)
                    ? () => _thumbnailService.GetThumbnail(node)
                    : null))
            .Where(item => !searchAllPaths || MatchesArchiveSearch(item, query))
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
            item.Node.FullPath,
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
                        .Append(item.Node.FullPath)
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

    private ArchiveFolderNode? ResolveContextDestination() =>
        _contextTarget as ArchiveFolderNode ?? _currentFolder;

    private void NotifyNavigationStateChanged()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        SaveImageAsCommand.NotifyCanExecuteChanged();
        SaveModelSkinAsCommand.NotifyCanExecuteChanged();
        SaveBspTexturesAsCommand.NotifyCanExecuteChanged();
        SaveWadTexturesAsCommand.NotifyCanExecuteChanged();
        PlayDemoInBrowserCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        OpenPakFolderCommand.NotifyCanExecuteChanged();
        GetInfoCommand.NotifyCanExecuteChanged();
        ZoomInIconsCommand.NotifyCanExecuteChanged();
        ZoomOutIconsCommand.NotifyCanExecuteChanged();
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

    private async Task ImportPathsAsync(
        IReadOnlyList<string> paths,
        ArchiveFolderNode? requestedDestination = null)
    {
        var destination = requestedDestination ?? _currentFolder;
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
            RecordMutation(history);
            MarkDirty(imported.Count == 1 ? "Added 1 item." : $"Added {imported.Count} items.");
            if (!ReferenceEquals(destination, _currentFolder))
            {
                NavigateToFolder(destination);
            }
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
        TrimHistory(_undoHistory);
        _redoHistory.Clear();
        _currentRevision = ++_nextRevision;
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    private void RestoreHistory(ArchiveHistoryEntry entry)
    {
        if (Document is null)
        {
            return;
        }

        ArchiveTreeEditor.RestoreFolderSnapshot(Document.Root, entry.Root);
        _currentRevision = entry.Revision;
        Document.IsDirty = _currentRevision != _savedRevision;
        ClearInternalClipboard();
        _backHistory.Clear();
        _forwardHistory.Clear();
        _searchText = string.Empty;
        OnPropertyChanged(nameof(SearchText));
        OnPropertyChanged(nameof(IsSearchActive));
        RebuildFolderTree(Document.Root);
        SetSelectedItems([]);
        OnPropertyChanged(nameof(WindowTitle));
        NotifyNavigationStateChanged();
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
