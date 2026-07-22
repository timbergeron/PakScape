using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using PakScape.Linux.Models;
using PakScape.Linux.ViewModels;
using PakStudio.Core.Interfaces;

namespace PakScape.Linux.Views;

public partial class MainWindow : Window
{
    private const double FolderPaneCollapseThreshold = 120;
    private const double FolderPaneReopenWidth = 220;

    private static readonly DataFormat<byte[]> ArchiveClipboardFormat =
        DataFormat.CreateBytesApplicationFormat("org.pakscape.archive-clipboard-id");
    private MainWindowViewModel? _viewModel;
    private IArchiveFileTransferService? _fileTransferService;
    private string? _startupPath;
    private bool _closeConfirmed;
    private bool _isCloseConfirmationPending;
    private PreviewWindow? _previewWindow;
    private PointerPressedEventArgs? _dragTriggerEvent;
    private Point? _dragStartPoint;
    private Control? _dragSource;
    private bool _isStartingDrag;
    private bool _isSynchronizingSelection;
    private bool _folderPaneWasCollapsedAtDragStart;
    private double _lastExpandedFolderPaneWidth = 280;

    public MainWindow()
    {
        InitializeComponent();
        ArchiveGrid.AddHandler(
            InputElement.KeyDownEvent,
            OnArchiveGridKeyDown,
            RoutingStrategies.Tunnel);
        ArchiveGrid.AddHandler(
            InputElement.PointerPressedEvent,
            OnArchiveGridPointerPressed,
            RoutingStrategies.Tunnel);
        ArchiveGrid.AddHandler(
            InputElement.PointerMovedEvent,
            OnArchiveGridPointerMoved,
            RoutingStrategies.Tunnel);
        ArchiveGrid.AddHandler(
            InputElement.PointerReleasedEvent,
            OnArchiveGridPointerReleased,
            RoutingStrategies.Tunnel);
        FolderSplitter.DragStarted += OnFolderSplitterDragStarted;
        FolderSplitter.DragCompleted += OnFolderSplitterDragCompleted;
    }

    public void Configure(
        MainWindowViewModel viewModel,
        IArchiveFileTransferService fileTransferService,
        string? startupPath)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(fileTransferService);
        if (_viewModel is not null)
        {
            throw new InvalidOperationException("The main window has already been configured.");
        }

        _viewModel = viewModel;
        _fileTransferService = fileTransferService;
        _startupPath = startupPath;
        DataContext = viewModel;

        Opened += OnOpened;
        Closing += OnClosing;
        viewModel.CloseRequested += OnCloseRequested;
        viewModel.RecentFiles.CollectionChanged += OnRecentFilesChanged;
        RebuildRecentFilesMenu();
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        Opened -= OnOpened;
        await ViewModel.InitializeAsync(_startupPath);
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed)
        {
            return;
        }

        e.Cancel = true;
        if (_isCloseConfirmationPending)
        {
            return;
        }

        _isCloseConfirmationPending = true;
        try
        {
            if (await ViewModel.CanCloseAsync())
            {
                _closeConfirmed = true;
                Close();
            }
        }
        finally
        {
            _isCloseConfirmationPending = false;
        }
    }

    private void OnCloseRequested(object? sender, EventArgs e)
    {
        Close();
    }

    private void OnFolderTreeContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container is TreeViewItem item)
        {
            item.Classes.Set(
                "root-folder",
                item.DataContext is FolderNodeViewModel { Folder.Parent: null });
        }
    }

    private void OnClearSearchClick(object? sender, RoutedEventArgs e)
    {
        ViewModel.SearchText = string.Empty;
        ArchiveSearchBox.Focus();
    }

    private void OnFolderPaneToggleClick(object? sender, RoutedEventArgs e)
    {
        if (FolderPaneColumn.ActualWidth < 1)
        {
            FolderPaneColumn.Width = new GridLength(
                Math.Max(FolderPaneReopenWidth, _lastExpandedFolderPaneWidth));
            SetFolderPaneChrome(collapsed: false);
            return;
        }

        _lastExpandedFolderPaneWidth = Math.Max(
            FolderPaneReopenWidth,
            FolderPaneColumn.ActualWidth);
        CollapseFolderPane();
    }

    private void OnFolderSplitterDragStarted(object? sender, VectorEventArgs e)
    {
        _folderPaneWasCollapsedAtDragStart = FolderPaneColumn.ActualWidth < 1;
    }

    private void OnFolderSplitterDragCompleted(object? sender, VectorEventArgs e)
    {
        var width = FolderPaneColumn.ActualWidth;
        if (_folderPaneWasCollapsedAtDragStart)
        {
            if (width >= 1 && width < FolderPaneReopenWidth)
            {
                FolderPaneColumn.Width = new GridLength(FolderPaneReopenWidth);
                _lastExpandedFolderPaneWidth = FolderPaneReopenWidth;
                SetFolderPaneChrome(collapsed: false);
            }
            else if (width >= FolderPaneReopenWidth)
            {
                _lastExpandedFolderPaneWidth = width;
                SetFolderPaneChrome(collapsed: false);
            }
        }
        else if (width < FolderPaneCollapseThreshold)
        {
            CollapseFolderPane();
        }
        else
        {
            _lastExpandedFolderPaneWidth = width;
            SetFolderPaneChrome(collapsed: false);
        }

        _folderPaneWasCollapsedAtDragStart = false;
    }

    private void CollapseFolderPane()
    {
        FolderPaneColumn.Width = new GridLength(0);
        SetFolderPaneChrome(collapsed: true);
    }

    private void SetFolderPaneChrome(bool collapsed)
    {
        FolderSplitterColumn.Width = collapsed ? new GridLength(0) : new GridLength(6);
        FolderSplitter.IsVisible = !collapsed;
        ContentPaneBorder.CornerRadius = collapsed
            ? new CornerRadius(0)
            : new CornerRadius(12, 0, 0, 0);
        ContentPaneBorder.BorderThickness = collapsed
            ? new Thickness(0, 1, 0, 0)
            : new Thickness(1, 1, 0, 0);
    }

    private void OnRecentFilesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebuildRecentFilesMenu();
    }

    private void RebuildRecentFilesMenu()
    {
        if (RecentMenu is null)
        {
            return;
        }

        if (ViewModel.RecentFiles.Count == 0)
        {
            RecentMenu.ItemsSource = new List<MenuItem>
            {
                new MenuItem { Header = "No recent archives", IsEnabled = false },
            };
            return;
        }

        RecentMenu.ItemsSource = ViewModel.RecentFiles.Select(path => new MenuItem
        {
            Header = path,
            Command = ViewModel.OpenRecentCommand,
            CommandParameter = path,
        }).ToList();
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ArchiveGrid.IsVisible)
        {
            UpdateSelection(ArchiveGrid.SelectedItems.OfType<ArchiveItemViewModel>());
        }
    }

    private void OnAlternateSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { IsVisible: true } listBox)
        {
            UpdateSelection(
                listBox.SelectedItems?.OfType<ArchiveItemViewModel>() ?? []);
        }
    }

    private void UpdateSelection(IEnumerable<ArchiveItemViewModel> selectedItems)
    {
        if (_isSynchronizingSelection)
        {
            return;
        }

        var items = selectedItems.Distinct().ToList();
        _isSynchronizingSelection = true;
        try
        {
            ViewModel.SetSelectedItems(items);
            ReplaceSelection(ArchiveGrid.SelectedItems, items);
            ReplaceSelection(LargeIconsList.SelectedItems, items);
            ReplaceSelection(SmallIconsList.SelectedItems, items);
            ReplaceSelection(ArchiveList.SelectedItems, items);
        }
        finally
        {
            _isSynchronizingSelection = false;
        }
    }

    private static void ReplaceSelection(
        System.Collections.IList? selection,
        IReadOnlyList<ArchiveItemViewModel> items)
    {
        if (selection is null)
        {
            return;
        }
        selection.Clear();
        foreach (var item in items)
        {
            selection.Add(item);
        }
    }

    private async void OnItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ArchiveGrid.SelectedItem is ArchiveItemViewModel item)
        {
            await ViewModel.OpenItemAsync(item);
            e.Handled = true;
        }
    }

    private async void OnAlternateItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: ArchiveItemViewModel item })
        {
            await ViewModel.OpenItemAsync(item);
            e.Handled = true;
        }
    }

    private async void OnArchiveGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.None)
        {
            ToggleQuickPreview();
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers != KeyModifiers.Control)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.X:
                await CopyToClipboardAsync(isCut: true);
                e.Handled = true;
                break;
            case Key.C:
                await CopyToClipboardAsync(isCut: false);
                e.Handled = true;
                break;
            case Key.V:
                await PasteFromClipboardAsync();
                e.Handled = true;
                break;
            case Key.A:
                SelectAllVisibleItems(sender);
                e.Handled = true;
                break;
        }
    }

    private async void OnCutClick(object? sender, RoutedEventArgs e) =>
        await CopyToClipboardAsync(isCut: true);

    private async void OnCopyClick(object? sender, RoutedEventArgs e) =>
        await CopyToClipboardAsync(isCut: false);

    private async void OnPasteClick(object? sender, RoutedEventArgs e) =>
        await PasteFromClipboardAsync();

    private void OnSelectAllClick(object? sender, RoutedEventArgs e)
    {
        SelectAllVisibleItems(null);
    }

    private async Task CopyToClipboardAsync(bool isCut)
    {
        var paths = ViewModel.CopySelection(isCut);
        var clipboardId = ViewModel.PendingClipboardId;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null || clipboardId is null)
        {
            ViewModel.CancelPendingClipboardTransfer();
            return;
        }

        DataTransfer? transfer = null;
        try
        {
            transfer = await CreateFileTransferAsync(paths);
            transfer.Add(DataTransferItem.Create(ArchiveClipboardFormat, clipboardId));
            await clipboard.SetDataAsync(transfer);
            transfer = null;
            ViewModel.CommitClipboardTransfer();
        }
        catch (Exception exception)
        {
            if (transfer is not null)
            {
                ((IDataTransfer)transfer).Dispose();
            }
            ViewModel.CancelPendingClipboardTransfer();
            await ShowTransferErrorAsync(isCut ? "Unable to cut selection" : "Unable to copy selection", exception);
        }
    }

    private async Task PasteFromClipboardAsync()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            return;
        }

        try
        {
            using var transfer = await clipboard.TryGetDataAsync();
            if (transfer is null)
            {
                return;
            }

            var clipboardId = await transfer.TryGetValueAsync(ArchiveClipboardFormat);
            if (ViewModel.HasInternalClipboard &&
                clipboardId is not null &&
                ViewModel.InternalClipboardId is { } ownedId &&
                clipboardId.SequenceEqual(ownedId))
            {
                if (await ViewModel.PasteInternalClipboardAsync())
                {
                    await clipboard.ClearAsync();
                }
                return;
            }

            ViewModel.ClearInternalClipboard();
            var paths = ((await transfer.TryGetFilesAsync()) ?? [])
                .Select(item => item.TryGetLocalPath())
                .Where(path => path is not null)
                .Cast<string>()
                .ToList();
            await ViewModel.AddDroppedPathsAsync(paths);
        }
        catch (Exception exception)
        {
            await ShowTransferErrorAsync("Unable to paste", exception);
        }
    }

    private void OnArchiveGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control source &&
            e.Source is Visual visual &&
            (visual is DataGridRow or ListBoxItem ||
             visual.FindAncestorOfType<DataGridRow>() is not null ||
             visual.FindAncestorOfType<ListBoxItem>() is not null) &&
            e.GetCurrentPoint(source).Properties.IsLeftButtonPressed)
        {
            _dragTriggerEvent = e;
            _dragStartPoint = e.GetPosition(source);
            _dragSource = source;
        }
    }

    private async void OnArchiveGridPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isStartingDrag || _dragTriggerEvent is null || _dragStartPoint is not { } start || _dragSource is not { } source)
        {
            return;
        }
        if (!e.GetCurrentPoint(source).Properties.IsLeftButtonPressed)
        {
            ClearDragStart();
            return;
        }

        var current = e.GetPosition(source);
        if (Math.Abs(current.X - start.X) < 4 && Math.Abs(current.Y - start.Y) < 4)
        {
            return;
        }

        var triggerEvent = _dragTriggerEvent;
        ClearDragStart();
        _isStartingDrag = true;
        IReadOnlyList<string> paths = [];
        try
        {
            paths = ViewModel.PrepareSelectedItemsForTransfer();
            if (paths.Count == 0)
            {
                return;
            }
            var transfer = await CreateFileTransferAsync(paths);
            if (transfer.Items.Count == 0)
            {
                ((IDataTransfer)transfer).Dispose();
                return;
            }
            await DragDrop.DoDragDropAsync(triggerEvent, transfer, DragDropEffects.Copy);
            e.Handled = true;
        }
        catch (Exception exception)
        {
            await ShowTransferErrorAsync("Unable to drag selection", exception);
        }
        finally
        {
            ViewModel.ReleaseTemporaryTransfer(paths);
            _isStartingDrag = false;
        }
    }

    private void OnArchiveGridPointerReleased(object? sender, PointerReleasedEventArgs e) =>
        ClearDragStart();

    private void ClearDragStart()
    {
        _dragTriggerEvent = null;
        _dragStartPoint = null;
        _dragSource = null;
    }

    private async Task<DataTransfer> CreateFileTransferAsync(IReadOnlyList<string> paths)
    {
        var transfer = new DataTransfer();
        try
        {
            foreach (var path in paths)
            {
                IStorageItem? item = Directory.Exists(path)
                    ? await StorageProvider.TryGetFolderFromPathAsync(path)
                    : await StorageProvider.TryGetFileFromPathAsync(path);
                if (item is not null)
                {
                    transfer.Add(DataTransferItem.CreateFile(item));
                }
            }
            return transfer;
        }
        catch
        {
            ((IDataTransfer)transfer).Dispose();
            throw;
        }
    }

    private async Task ShowTransferErrorAsync(string title, Exception exception)
    {
        var dialog = new MessageDialogWindow(title, exception.Message, MessageDialogButtons.Ok);
        await dialog.ShowDialog<MessageDialogResult>(this);
    }

    private void SelectAllVisibleItems(object? sender)
    {
        if (sender is ListBox sourceList)
        {
            sourceList.SelectAll();
            sourceList.Focus();
            return;
        }
        if (LargeIconsList.IsVisible)
        {
            LargeIconsList.SelectAll();
            LargeIconsList.Focus();
        }
        else if (SmallIconsList.IsVisible)
        {
            SmallIconsList.SelectAll();
            SmallIconsList.Focus();
        }
        else if (ArchiveList.IsVisible)
        {
            ArchiveList.SelectAll();
            ArchiveList.Focus();
        }
        else
        {
            ArchiveGrid.SelectAll();
            ArchiveGrid.Focus();
        }
    }

    private void OnQuickPreviewClick(object? sender, RoutedEventArgs e)
    {
        ToggleQuickPreview();
    }

    private void ToggleQuickPreview()
    {
        if (_previewWindow is { IsVisible: true })
        {
            _previewWindow.Close();
            return;
        }

        var nodes = ViewModel.SelectedNodes;
        if (nodes.Count == 0)
        {
            return;
        }

        try
        {
            var previewWindow = new PreviewWindow(nodes, FileTransferService);
            previewWindow.Closed += (_, _) =>
            {
                if (ReferenceEquals(_previewWindow, previewWindow))
                {
                    _previewWindow = null;
                }
            };
            _previewWindow = previewWindow;
            try
            {
                previewWindow.Show(this);
            }
            catch
            {
                _previewWindow = null;
                throw;
            }
        }
        catch (Exception exception)
        {
            var dialog = new MessageDialogWindow(
                "Unable to preview selection",
                exception.Message,
                MessageDialogButtons.Ok);
            _ = dialog.ShowDialog<MessageDialogResult>(this);
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Formats.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var paths = e.DataTransfer.TryGetFiles()?
            .Select(item => item.TryGetLocalPath())
            .Where(path => path is not null)
            .Cast<string>()
            .ToList() ?? [];
        e.Handled = true;
        await ViewModel.AddDroppedPathsAsync(paths);
    }

    private MainWindowViewModel ViewModel => _viewModel
        ?? throw new InvalidOperationException("The main window has not been configured.");

    private IArchiveFileTransferService FileTransferService => _fileTransferService
        ?? throw new InvalidOperationException("The main window has not been configured.");

    private ColumnDefinition FolderPaneColumn => ContentLayoutGrid.ColumnDefinitions[0];

    private ColumnDefinition FolderSplitterColumn => ContentLayoutGrid.ColumnDefinitions[1];
}
