using System.Collections.Specialized;
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PakScape.Linux.Models;
using PakScape.Linux.ViewModels;
using PakStudio.Core.Interfaces;
using PakStudio.Core.Nodes;
using PakStudio.Core.Preview;

namespace PakScape.Linux.Views;

public partial class MainWindow : Window
{
    private const double FolderPaneCollapseThreshold = 120;
    private const double FolderPaneReopenWidth = 220;
    private const double FolderPaneIndicatorDragThreshold = 4;
    private const double FolderPaneMinContentWidth = 260;
    private static readonly TimeSpan FolderPaneAnimationDuration = TimeSpan.FromMilliseconds(160);
    private static readonly TimeSpan FolderPaneAnimationInterval = TimeSpan.FromMilliseconds(16);
    private static readonly TimeSpan TypeSelectionResetInterval = TimeSpan.FromSeconds(1);

    /* No portable way to read the desktop double-click time; this matches the GTK default. */
    private static readonly TimeSpan RenameClickDelay = TimeSpan.FromMilliseconds(500);

    private static readonly DataFormat<byte[]> ArchiveClipboardFormat =
        DataFormat.CreateBytesApplicationFormat("org.pakscape.archive-clipboard-id");
    private MainWindowViewModel? _viewModel;
    private IArchiveFileTransferService? _fileTransferService;
    private string? _startupPath;
    private string _initialFormatId = "pak";
    private bool _closeConfirmed;
    private bool _isCloseConfirmationPending;
    private PreviewWindow? _previewWindow;
    private SkyboxPreviewWindow? _skyboxPreviewWindow;
    private PointerPressedEventArgs? _dragTriggerEvent;
    private Point? _dragStartPoint;
    private Control? _dragSource;
    private bool _isStartingDrag;
    private bool _isSynchronizingSelection;
    private bool _isFolderPaneIndicatorPressed;
    private bool _isDraggingFolderPaneIndicator;
    private bool _suppressFolderPaneIndicatorClick;
    private double _folderPaneIndicatorPressX;
    private DispatcherTimer? _folderPaneAnimationTimer;
    private DateTimeOffset _folderPaneAnimationStartedAt;
    private double _folderPaneAnimationFrom;
    private double _folderPaneAnimationTo;
    private bool _folderPaneAnimationCollapses;
    private bool _isCommittingRename;
    private double _lastExpandedFolderPaneWidth = 280;
    private string _typeSelectionBuffer = string.Empty;
    private DateTimeOffset _lastTypeSelectionInput;
    private readonly DispatcherTimer _renameClickTimer = new() { Interval = RenameClickDelay };
    private ArchiveItemViewModel? _renameClickCandidate;
    private ArchiveItemViewModel? _pendingRenameItem;
    private Control? _renameClickSource;
    private Point? _renameClickOrigin;
    private readonly Dictionary<ArchiveNode, ItemInfoWindow> _itemInfoWindows =
        new(ReferenceEqualityComparer.Instance);

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

        AddHandler(
            InputElement.PointerPressedEvent,
            OnWindowPointerPressed,
            RoutingStrategies.Tunnel);

        /* Tunnel so the pressed handler sees the selection as it was before the click. */
        foreach (var itemsView in new Control[] { LargeIconsList, SmallIconsList, ArchiveList, ArchiveGrid })
        {
            itemsView.AddHandler(
                InputElement.PointerPressedEvent,
                OnArchiveItemRenameClickPressed,
                RoutingStrategies.Tunnel);
            itemsView.AddHandler(
                InputElement.PointerReleasedEvent,
                OnArchiveItemRenameClickReleased,
                RoutingStrategies.Tunnel);
        }
        _renameClickTimer.Tick += OnRenameClickTimerTick;
    }

    public void Configure(
        MainWindowViewModel viewModel,
        IArchiveFileTransferService fileTransferService,
        string? startupPath,
        string initialFormatId = "pak")
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
        _initialFormatId = initialFormatId;
        DataContext = viewModel;
        AttachArchiveContextMenus(viewModel);

        Opened += OnOpened;
        Closing += OnClosing;
        viewModel.CloseRequested += OnCloseRequested;
        viewModel.GetInfoRequested += OnGetInfoRequested;
        viewModel.RenameRequested += OnRenameRequested;
        viewModel.NewWindowRequested += OnNewWindowRequested;
        viewModel.OpenWindowRequested += OnOpenWindowRequested;
        viewModel.RecentFiles.CollectionChanged += OnRecentFilesChanged;
        RebuildRecentFilesMenu();
    }

    private void AttachArchiveContextMenus(MainWindowViewModel viewModel)
    {
        LargeIconsList.ContextMenu = CreateArchiveContextMenu(viewModel);
        SmallIconsList.ContextMenu = CreateArchiveContextMenu(viewModel);
        ArchiveList.ContextMenu = CreateArchiveContextMenu(viewModel);
        ArchiveGrid.ContextMenu = CreateArchiveContextMenu(viewModel);
    }

    private ContextMenu CreateArchiveContextMenu(MainWindowViewModel viewModel)
    {
        var quickPreview = new MenuItem { Header = "Quick Preview" };
        quickPreview.Click += OnQuickPreviewClick;
        var viewSkybox = new MenuItem { Header = "View Skybox" };
        viewSkybox.Click += OnViewSkyboxClick;
        var cut = new MenuItem { Header = "Cut" };
        cut.Click += OnCutClick;
        var copy = new MenuItem { Header = "Copy" };
        copy.Click += OnCopyClick;
        var paste = new MenuItem { Header = "Paste" };
        paste.Click += OnPasteClick;

        var saveImageAs = new MenuItem
        {
            Header = "Save As",
            ItemsSource = new[]
            {
                SaveImageMenuItem("LMP...", "lmp", viewModel),
                SaveImageMenuItem("JPEG...", "jpg", viewModel),
                SaveImageMenuItem("PNG...", "png", viewModel),
                SaveImageMenuItem("TGA...", "tga", viewModel),
            },
        };

        var saveSkinAs = new MenuItem
        {
            Header = "Save Model Skins As",
            ItemsSource = new[]
            {
                SaveModelSkinMenuItem("LMP...", "lmp", viewModel),
                SaveModelSkinMenuItem("JPEG...", "jpg", viewModel),
                SaveModelSkinMenuItem("PNG...", "png", viewModel),
                SaveModelSkinMenuItem("TGA...", "tga", viewModel),
            },
        };
        var saveBspTexturesAs = new MenuItem
        {
            Header = "Save BSP Textures As",
            ItemsSource = new[]
            {
                SaveBspTextureMenuItem("LMP...", "lmp", viewModel),
                SaveBspTextureMenuItem("JPEG...", "jpg", viewModel),
                SaveBspTextureMenuItem("PNG...", "png", viewModel),
                SaveBspTextureMenuItem("TGA...", "tga", viewModel),
            },
        };
        var saveWadTexturesAs = new MenuItem
        {
            Header = "Save WAD Textures As",
            ItemsSource = new[]
            {
                SaveWadTextureMenuItem("LMP...", "lmp", viewModel),
                SaveWadTextureMenuItem("JPEG...", "jpg", viewModel),
                SaveWadTextureMenuItem("PNG...", "png", viewModel),
                SaveWadTextureMenuItem("TGA...", "tga", viewModel),
            },
        };

        var contextMenu = new ContextMenu
        {
            ItemsSource = new object[]
            {
                new MenuItem { Header = "Open", Command = viewModel.OpenSelectedCommand },
                quickPreview,
                viewSkybox,
                new MenuItem
                {
                    Header = "Play Demo in Browser...",
                    Command = viewModel.PlayDemoInBrowserCommand,
                },
                new Separator(),
                cut,
                copy,
                paste,
                new Separator(),
                new MenuItem { Header = "Add Files...", Command = viewModel.ContextAddFilesCommand },
                new MenuItem { Header = "Add Folder...", Command = viewModel.ContextAddFolderCommand },
                new MenuItem { Header = "New Folder...", Command = viewModel.ContextNewFolderCommand },
                new Separator(),
                new MenuItem { Header = "Export...", Command = viewModel.ExportCommand },
                saveImageAs,
                saveSkinAs,
                saveBspTexturesAs,
                saveWadTexturesAs,
                new MenuItem { Header = "Rename...", Command = viewModel.RenameCommand },
                new MenuItem { Header = "Delete", Command = viewModel.DeleteCommand },
                new MenuItem { Header = "Get Info", Command = viewModel.GetInfoCommand },
            },
        };
        contextMenu.Opened += (_, _) =>
        {
            viewModel.SetContextTarget(
                viewModel.SelectedNodes.Count == 1 ? viewModel.SelectedNodes[0] : null);
            saveImageAs.IsVisible = viewModel.HasImageSaveOptions;
            saveSkinAs.IsVisible = viewModel.HasModelSkinSaveOptions;
            saveBspTexturesAs.IsVisible = viewModel.HasBspTextureSaveOptions;
            saveWadTexturesAs.IsVisible = viewModel.HasWadTextureSaveOptions;
            viewSkybox.IsVisible = viewModel.HasSkyboxPreview;
        };
        return contextMenu;
    }

    private static MenuItem SaveImageMenuItem(
        string header,
        string formatId,
        MainWindowViewModel viewModel) =>
        new()
        {
            Header = header,
            Command = viewModel.SaveImageAsCommand,
            CommandParameter = formatId,
        };

    private void OnViewSkyboxClick(object? sender, RoutedEventArgs e)
    {
        var faceSet = ViewModel.SelectedNodes.Count == 1
            ? SkyboxFaceSet.Find(ViewModel.SelectedNodes[0])
            : null;
        if (faceSet is null)
        {
            return;
        }
        try
        {
            _skyboxPreviewWindow?.Close();
            var window = new SkyboxPreviewWindow(faceSet);
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_skyboxPreviewWindow, window))
                {
                    _skyboxPreviewWindow = null;
                }
            };
            _skyboxPreviewWindow = window;
            window.Show(this);
        }
        catch (Exception exception)
        {
            var dialog = new MessageDialogWindow(
                "Unable to Preview Skybox",
                exception.Message,
                MessageDialogButtons.Ok);
            _ = dialog.ShowDialog<MessageDialogResult>(this);
        }
    }

    private void OnGetInfoRequested(object? sender, EventArgs e)
    {
        const int maximumWindows = 32;
        var nodes = ViewModel.InfoNodes
            .Distinct()
            .Take(maximumWindows + 1)
            .ToList();
        if (nodes.Count > maximumWindows)
        {
            var dialog = new MessageDialogWindow(
                "Too Many Get Info Windows",
                $"Select no more than {maximumWindows:N0} items at once.",
                MessageDialogButtons.Ok);
            _ = dialog.ShowDialog<MessageDialogResult>(this);
            return;
        }

        foreach (var node in nodes)
        {
            if (_itemInfoWindows.TryGetValue(node, out var existing))
            {
                existing.Activate();
                continue;
            }

            var window = new ItemInfoWindow(node, ViewModel.ArchiveDisplayName);
            window.Closed += (_, _) => _itemInfoWindows.Remove(node);
            _itemInfoWindows[node] = window;
            window.Show(this);
        }
    }

    private static void OnNewWindowRequested(string formatId)
    {
        if (Application.Current is App app)
        {
            app.OpenArchiveWindow(null, formatId);
        }
    }

    private static void OnOpenWindowRequested(string path)
    {
        if (Application.Current is App app)
        {
            app.OpenArchiveWindow(path);
        }
    }

    private void OnRenameRequested(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var editor = FindActiveInlineRenameEditor();
            if (editor is not null)
            {
                editor.Focus();
                editor.SelectAll();
            }
        });
    }

    private TextBox? FindActiveInlineRenameEditor() =>
        this.GetVisualDescendants()
            .OfType<TextBox>()
            .FirstOrDefault(textBox =>
                textBox.Classes.Contains("inline-rename") &&
                textBox.IsVisible &&
                textBox.DataContext is ArchiveItemViewModel { IsRenaming: true });

    /// <summary>Commits an open inline rename when the click lands anywhere else. Clicking
    /// another item already commits by moving focus, but empty list space and other chrome
    /// never take focus, which would otherwise leave the editor open.</summary>
    private async void OnWindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual visual &&
            (visual as TextBox ?? visual.FindAncestorOfType<TextBox>()) is { } clicked &&
            clicked.Classes.Contains("inline-rename"))
        {
            return;
        }

        if (FindActiveInlineRenameEditor() is { DataContext: ArchiveItemViewModel item } editor)
        {
            await CommitInlineRenameAsync(item, editor.Text ?? string.Empty);
        }
    }

    /* Finder/Explorer-style slow double click: clicking the name of an item that is
       already the lone selection starts an inline rename once the double-click
       window has passed without a second click. */
    private void OnArchiveItemRenameClickPressed(object? sender, PointerPressedEventArgs e)
    {
        CancelPendingRenameClick();
        _renameClickCandidate = null;
        if (sender is not Control source ||
            e.ClickCount != 1 ||
            e.KeyModifiers != KeyModifiers.None ||
            !e.GetCurrentPoint(source).Properties.IsLeftButtonPressed ||
            e.Source is not TextBlock { Tag: "NameText", DataContext: ArchiveItemViewModel item } nameText ||
            !IsPointOverRenderedText(nameText, e.GetPosition(nameText)) ||
            !IsSoleSelectedItem(source, item))
        {
            return;
        }

        _renameClickCandidate = item;
        _renameClickSource = source;
        _renameClickOrigin = e.GetPosition(source);
    }

    private void OnArchiveItemRenameClickReleased(object? sender, PointerReleasedEventArgs e)
    {
        var candidate = _renameClickCandidate;
        _renameClickCandidate = null;
        if (candidate is null ||
            sender is not Control source ||
            !ReferenceEquals(source, _renameClickSource) ||
            e.InitialPressMouseButton != MouseButton.Left ||
            e.KeyModifiers != KeyModifiers.None ||
            !IsSoleSelectedItem(source, candidate))
        {
            return;
        }

        /* A click that turned into a drag is not a rename request. */
        if (_renameClickOrigin is { } origin)
        {
            var current = e.GetPosition(source);
            if (Math.Abs(current.X - origin.X) >= 4 || Math.Abs(current.Y - origin.Y) >= 4)
            {
                return;
            }
        }

        _pendingRenameItem = candidate;
        _renameClickTimer.Start();
    }

    private void OnRenameClickTimerTick(object? sender, EventArgs e)
    {
        var item = _pendingRenameItem;
        var source = _renameClickSource;
        CancelPendingRenameClick();
        if (item is null || source is null || !IsSoleSelectedItem(source, item))
        {
            return;
        }

        ViewModel.RenameCommand.Execute(null);
    }

    /// <summary>Drops an armed rename. The candidate from the current press is left alone;
    /// it is re-validated against the selection when the button is released.</summary>
    private void CancelPendingRenameClick()
    {
        _renameClickTimer.Stop();
        _pendingRenameItem = null;
    }

    private static bool IsSoleSelectedItem(Control itemsView, ArchiveItemViewModel item)
    {
        var selection = itemsView switch
        {
            ListBox listBox => listBox.SelectedItems,
            DataGrid dataGrid => dataGrid.SelectedItems,
            _ => null,
        };
        return selection is { Count: 1 } && ReferenceEquals(selection[0], item);
    }

    /// <summary>Reports whether <paramref name="point"/> lands on drawn glyphs rather than
    /// the padding the label stretches over.</summary>
    private static bool IsPointOverRenderedText(TextBlock label, Point point)
    {
        var x = point.X - label.Padding.Left;
        var y = point.Y - label.Padding.Top;
        var lineTop = 0.0;
        foreach (var line in label.TextLayout.TextLines)
        {
            if (y >= lineTop &&
                y <= lineTop + line.Height &&
                x >= line.Start &&
                x <= line.Start + line.Width)
            {
                return true;
            }
            lineTop += line.Height;
        }
        return false;
    }

    private async void OnInlineRenameKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: ArchiveItemViewModel item } editor)
        {
            return;
        }
        if (e.Key == Key.Escape)
        {
            item.EndRenaming();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await CommitInlineRenameAsync(item, editor.Text ?? string.Empty);
        }
    }

    private async void OnInlineRenameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: ArchiveItemViewModel { IsRenaming: true } item } editor)
        {
            await CommitInlineRenameAsync(item, editor.Text ?? string.Empty);
        }
    }

    private async Task CommitInlineRenameAsync(ArchiveItemViewModel item, string name)
    {
        if (_isCommittingRename)
        {
            return;
        }
        _isCommittingRename = true;
        try
        {
            if (!await ViewModel.CommitItemRenameAsync(item, name))
            {
                item.BeginRenaming();
                OnRenameRequested(this, EventArgs.Empty);
            }
        }
        finally
        {
            _isCommittingRename = false;
        }
    }

    private static MenuItem SaveModelSkinMenuItem(
        string header,
        string formatId,
        MainWindowViewModel viewModel) =>
        new()
        {
            Header = header,
            Command = viewModel.SaveModelSkinAsCommand,
            CommandParameter = formatId,
        };

    private static MenuItem SaveBspTextureMenuItem(
        string header,
        string formatId,
        MainWindowViewModel viewModel) =>
        new()
        {
            Header = header,
            Command = viewModel.SaveBspTexturesAsCommand,
            CommandParameter = formatId,
        };

    private static MenuItem SaveWadTextureMenuItem(
        string header,
        string formatId,
        MainWindowViewModel viewModel) =>
        new()
        {
            Header = header,
            Command = viewModel.SaveWadTexturesAsCommand,
            CommandParameter = formatId,
        };

    private async void OnOpened(object? sender, EventArgs e)
    {
        Opened -= OnOpened;
        await ViewModel.InitializeAsync(_startupPath, _initialFormatId);
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
        if (_suppressFolderPaneIndicatorClick)
        {
            // The press became a drag, which already settled the pane width. A drag
            // released off the grip leaves the flag set, so the toolbar toggle clears
            // it without being swallowed.
            _suppressFolderPaneIndicatorClick = false;
            if (ReferenceEquals(sender, FolderPaneCollapsedIndicator))
            {
                return;
            }
        }

        if (FolderPaneColumn.ActualWidth < 1)
        {
            AnimateFolderPaneWidth(
                Math.Max(FolderPaneReopenWidth, _lastExpandedFolderPaneWidth),
                collapseWhenDone: false);
            return;
        }

        _lastExpandedFolderPaneWidth = Math.Max(
            FolderPaneReopenWidth,
            FolderPaneColumn.ActualWidth);
        CollapseFolderPane();
    }

    private void OnFolderSplitterDragStarted(object? sender, VectorEventArgs e) =>
        StopFolderPaneAnimation();

    private void OnFolderSplitterDragCompleted(object? sender, VectorEventArgs e) =>
        SettleFolderPaneWidth(FolderPaneColumn.ActualWidth);

    /// <summary>
    /// Decides where a released drag lands: past the collapse threshold it animates
    /// shut, short of a usable width it springs back open to that width, and anywhere
    /// wider it simply rests where it was dropped.
    /// </summary>
    private void SettleFolderPaneWidth(double width)
    {
        if (width < FolderPaneCollapseThreshold)
        {
            AnimateFolderPaneWidth(0, collapseWhenDone: true);
        }
        else if (width < FolderPaneReopenWidth)
        {
            AnimateFolderPaneWidth(FolderPaneReopenWidth, collapseWhenDone: false);
        }
        else
        {
            _lastExpandedFolderPaneWidth = width;
            SetFolderPaneChrome(collapsed: false);
        }
    }

    private void AnimateFolderPaneWidth(double target, bool collapseWhenDone)
    {
        StopFolderPaneAnimation();

        _folderPaneAnimationFrom = FolderPaneColumn.ActualWidth;
        _folderPaneAnimationTo = target;
        _folderPaneAnimationCollapses = collapseWhenDone;

        if (!collapseWhenDone)
        {
            // Bring the seam back before the pane grows into it; a collapse keeps its
            // chrome until the very end so the pane shrinks with the splitter attached.
            SetFolderPaneChrome(collapsed: false);
        }

        if (Math.Abs(_folderPaneAnimationFrom - target) < 1)
        {
            FinishFolderPaneAnimation();
            return;
        }

        _folderPaneAnimationStartedAt = DateTimeOffset.UtcNow;
        _folderPaneAnimationTimer = new DispatcherTimer { Interval = FolderPaneAnimationInterval };
        _folderPaneAnimationTimer.Tick += OnFolderPaneAnimationTick;
        _folderPaneAnimationTimer.Start();
    }

    private void OnFolderPaneAnimationTick(object? sender, EventArgs e)
    {
        var elapsed = (DateTimeOffset.UtcNow - _folderPaneAnimationStartedAt).TotalMilliseconds;
        var progress = Math.Clamp(elapsed / FolderPaneAnimationDuration.TotalMilliseconds, 0, 1);
        /* Ease out cubic, so the pane decelerates into its resting width. */
        var eased = 1 - Math.Pow(1 - progress, 3);
        FolderPaneColumn.Width = new GridLength(
            _folderPaneAnimationFrom + ((_folderPaneAnimationTo - _folderPaneAnimationFrom) * eased));

        if (progress >= 1)
        {
            FinishFolderPaneAnimation();
        }
    }

    private void FinishFolderPaneAnimation()
    {
        StopFolderPaneAnimation();
        FolderPaneColumn.Width = new GridLength(_folderPaneAnimationTo);
        if (_folderPaneAnimationCollapses)
        {
            SetFolderPaneChrome(collapsed: true);
            FolderPaneCollapsedIndicator.Opacity = FolderPaneCollapsedIndicator.IsPointerOver ? 1 : 0;
        }
        else
        {
            _lastExpandedFolderPaneWidth = _folderPaneAnimationTo;
            SetFolderPaneChrome(collapsed: false);
        }
    }

    private void StopFolderPaneAnimation()
    {
        if (_folderPaneAnimationTimer is null)
        {
            return;
        }

        _folderPaneAnimationTimer.Stop();
        _folderPaneAnimationTimer.Tick -= OnFolderPaneAnimationTick;
        _folderPaneAnimationTimer = null;
    }

    private void CollapseFolderPane() => AnimateFolderPaneWidth(0, collapseWhenDone: true);

    // A drag from the grip keeps the grip alive (hiding it would drop the pointer
    // capture mid-drag), so it opts out of the indicator half of the chrome.
    private void SetFolderPaneChrome(bool collapsed, bool updateIndicator = true)
    {
        FolderSplitterColumn.Width = collapsed ? new GridLength(0) : new GridLength(6);
        FolderSplitter.IsVisible = !collapsed;
        if (updateIndicator)
        {
            if (!collapsed)
            {
                FolderPaneCollapsedIndicator.Opacity = 0;
            }
            FolderPaneCollapsedIndicator.IsVisible = collapsed;
        }
        ContentPaneBorder.CornerRadius = collapsed
            ? new CornerRadius(0)
            : new CornerRadius(12, 0, 0, 0);
        ContentPaneBorder.BorderThickness = collapsed
            ? new Thickness(0, 1, 0, 0)
            : new Thickness(1, 1, 0, 0);
    }

    private void OnFolderPaneIndicatorPointerEntered(object? sender, PointerEventArgs e) =>
        FolderPaneCollapsedIndicator.Opacity = 1;

    private void OnFolderPaneIndicatorPointerExited(object? sender, PointerEventArgs e)
    {
        if (!_isDraggingFolderPaneIndicator)
        {
            FolderPaneCollapsedIndicator.Opacity = 0;
        }
    }

    private void OnFolderPaneIndicatorPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(FolderPaneCollapsedIndicator).Properties.IsLeftButtonPressed)
        {
            return;
        }

        StopFolderPaneAnimation();
        _isFolderPaneIndicatorPressed = true;
        _isDraggingFolderPaneIndicator = false;
        _suppressFolderPaneIndicatorClick = false;
        _folderPaneIndicatorPressX = e.GetPosition(ContentLayoutGrid).X;
        e.Pointer.Capture(FolderPaneCollapsedIndicator);
    }

    private void OnFolderPaneIndicatorPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isFolderPaneIndicatorPressed)
        {
            return;
        }

        var delta = e.GetPosition(ContentLayoutGrid).X - _folderPaneIndicatorPressX;
        if (!_isDraggingFolderPaneIndicator)
        {
            if (Math.Abs(delta) < FolderPaneIndicatorDragThreshold)
            {
                return;
            }

            _isDraggingFolderPaneIndicator = true;
            _suppressFolderPaneIndicatorClick = true;
            // Show the seam while dragging; the grip stays put until the drag settles.
            SetFolderPaneChrome(collapsed: false, updateIndicator: false);
        }

        FolderPaneColumn.Width = new GridLength(ClampDraggedFolderPaneWidth(delta));
    }

    private void OnFolderPaneIndicatorPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isFolderPaneIndicatorPressed)
        {
            return;
        }

        _isFolderPaneIndicatorPressed = false;
        e.Pointer.Capture(null);

        if (!_isDraggingFolderPaneIndicator)
        {
            return;
        }

        _isDraggingFolderPaneIndicator = false;
        SettleFolderPaneWidth(FolderPaneColumn.ActualWidth);
    }

    private double ClampDraggedFolderPaneWidth(double width)
    {
        var available = Math.Max(0, ContentLayoutGrid.Bounds.Width - FolderPaneMinContentWidth);
        return Math.Clamp(width, 0, available);
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
            await OpenOrPreviewAsync(item);
            e.Handled = true;
        }
    }

    private async void OnAlternateItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: ArchiveItemViewModel item })
        {
            await OpenOrPreviewAsync(item);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Preview-native assets stay in PakScape; anything else goes to its own app.
    /// </summary>
    private async Task OpenOrPreviewAsync(ArchiveItemViewModel item)
    {
        if (ArchivePreviewBuilder.OpensInQuickPreview(item.Node))
        {
            ShowQuickPreview([item.Node]);
            return;
        }

        await ViewModel.OpenItemAsync(item);
    }

    private async void OnArchiveGridKeyDown(object? sender, KeyEventArgs e)
    {
        CancelPendingRenameClick();
        if (e.Source is TextBox)
        {
            return;
        }

        if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.None)
        {
            ToggleQuickPreview();
            e.Handled = true;
            return;
        }

        if (TryTypeSelect(sender, e))
        {
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

    private bool TryTypeSelect(object? sender, KeyEventArgs e)
    {
        const KeyModifiers shortcutModifiers =
            KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta;
        if ((e.KeyModifiers & shortcutModifiers) != KeyModifiers.None ||
            string.IsNullOrEmpty(e.KeySymbol) ||
            e.KeySymbol.Any(character =>
                char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            return false;
        }

        var input = e.KeySymbol.ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;
        if (now - _lastTypeSelectionInput > TypeSelectionResetInterval)
        {
            _typeSelectionBuffer = string.Empty;
        }
        else if (_typeSelectionBuffer.Length == 1 &&
                 string.Equals(_typeSelectionBuffer, input, StringComparison.Ordinal))
        {
            // Repeatedly pressing one key cycles through every matching item.
            _typeSelectionBuffer = string.Empty;
        }

        _typeSelectionBuffer += input;
        _lastTypeSelectionInput = now;

        var items = ViewModel.CurrentItems;
        if (items.Count == 0)
        {
            return true;
        }

        var selectedIndex = ViewModel.SelectedItem is { } selected
            ? items.IndexOf(selected)
            : -1;
        var matchIndex = FindTypeSelectionMatch(items, selectedIndex + 1);
        if (matchIndex < 0)
        {
            return true;
        }

        var match = items[matchIndex];
        UpdateSelection([match]);
        ScrollSelectionIntoView(sender, match);
        return true;
    }

    private int FindTypeSelectionMatch(
        ObservableCollection<ArchiveItemViewModel> items,
        int startIndex)
    {
        for (var offset = 0; offset < items.Count; offset++)
        {
            var index = (startIndex + offset) % items.Count;
            if (items[index].Name.StartsWith(
                    _typeSelectionBuffer,
                    StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private void ScrollSelectionIntoView(object? sender, ArchiveItemViewModel item)
    {
        if (sender is ListBox sourceList)
        {
            sourceList.ScrollIntoView(item);
            return;
        }

        if (ArchiveGrid.IsVisible)
        {
            ArchiveGrid.ScrollIntoView(item, null);
        }
        else if (LargeIconsList.IsVisible)
        {
            LargeIconsList.ScrollIntoView(item);
        }
        else if (SmallIconsList.IsVisible)
        {
            SmallIconsList.ScrollIntoView(item);
        }
        else
        {
            ArchiveList.ScrollIntoView(item);
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
        if (sender is Control selectionSource &&
            e.Source is Visual selectionVisual &&
            e.GetCurrentPoint(selectionSource).Properties.IsRightButtonPressed)
        {
            var dataGridRow = selectionVisual as DataGridRow ??
                selectionVisual.FindAncestorOfType<DataGridRow>();
            var listBoxItem = selectionVisual as ListBoxItem ??
                selectionVisual.FindAncestorOfType<ListBoxItem>();

            if (dataGridRow is { IsSelected: false } && selectionSource is DataGrid dataGrid)
            {
                dataGrid.SelectedItems.Clear();
                dataGridRow.IsSelected = true;
            }
            else if (listBoxItem is { IsSelected: false } && selectionSource is ListBox listBox)
            {
                listBox.SelectedItems?.Clear();
                listBoxItem.IsSelected = true;
            }
            return;
        }

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
        CancelPendingRenameClick();
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

        ShowQuickPreview(ViewModel.SelectedNodes);
    }

    private void ShowQuickPreview(IReadOnlyList<ArchiveNode> nodes)
    {
        if (nodes.Count == 0)
        {
            return;
        }

        /* Replace whatever is already being previewed. */
        if (_previewWindow is { IsVisible: true })
        {
            _previewWindow.Close();
        }

        try
        {
            var previewWindow = new PreviewWindow(nodes);
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
