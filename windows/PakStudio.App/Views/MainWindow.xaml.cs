using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using PakStudio.App.Services;
using PakStudio.App.ViewModels;
using PakStudio.Core.Interfaces;
using PakStudio.Core.Nodes;
using PakStudio.Core.Preview;

namespace PakStudio.App.Views;

public partial class MainWindow : Window
{
    private const double FolderPaneCollapseThreshold = 120;
    private const double FolderPaneReopenWidth = 220;
    private const double FolderPaneIndicatorDragThreshold = 4;
    private const double FolderPaneMinContentWidth = 260;
    private static readonly TimeSpan FolderPaneAnimationDuration = TimeSpan.FromMilliseconds(160);
    private static readonly TimeSpan FolderPaneAnimationInterval = TimeSpan.FromMilliseconds(16);
    private const string DetailsColumnDragFormat = "PakScape.DetailsColumnKey";

    private static readonly Duration ColumnDragFadeInDuration = new(TimeSpan.FromMilliseconds(90));
    private static readonly Duration ColumnDragFadeOutDuration = new(TimeSpan.FromMilliseconds(140));
    private static readonly Duration ColumnDropIndicatorSlideDuration = new(TimeSpan.FromMilliseconds(140));

    private readonly MainWindowViewModel _viewModel;
    private readonly IArchiveFileTransferService _fileTransferService;
    private readonly DispatcherTimer _renameClickTimer;
    private PreviewWindow? _previewWindow;
    private bool _allowClose;
    private bool _isCloseConfirmationPending;
    private bool _initializeDocument = true;
    private string? _startupArchivePath;
    private string _startupFormatId = "pak";
    private Point? _dragStartPoint;
    private bool _isStartingDrag;
    private bool _isFolderPaneIndicatorPressed;
    private bool _isDraggingFolderPaneIndicator;
    private bool _suppressFolderPaneIndicatorClick;
    private double _folderPaneIndicatorPressX;
    private DispatcherTimer? _folderPaneAnimationTimer;
    private DateTimeOffset _folderPaneAnimationStartedAt;
    private double _folderPaneAnimationFrom;
    private double _folderPaneAnimationTo;
    private bool _folderPaneAnimationCollapses;
    private bool _contextMenuTargetPrepared;
    private double _lastExpandedFolderPaneWidth = 280;
    private Point? _columnHeaderDragStart;
    private string? _columnHeaderDragKey;
    private double _columnHeaderGrabOffset;
    private bool _columnDropIndicatorShown;
    private ArchiveItemViewModel? _renameClickCandidate;
    private ArchiveItemViewModel? _pendingRenameItem;
    private Point? _renameClickOrigin;

    public MainWindow(
        MainWindowViewModel viewModel,
        IArchiveFileTransferService fileTransferService)
    {
        InitializeComponent();
        _renameClickTimer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(GetDoubleClickTime()),
        };
        _renameClickTimer.Tick += RenameClickTimer_OnTick;
        _viewModel = viewModel;
        _fileTransferService = fileTransferService;
        DataContext = _viewModel;
        _viewModel.RenameRequested += ViewModel_OnRenameRequested;
        _viewModel.CloseRequested += ViewModel_OnCloseRequested;
        PreviewMouseDown += MainWindow_OnPreviewMouseDown;
        Loaded += OnLoaded;
    }

    internal string? ArchivePath => _viewModel.Document?.FilePath;

    public void ConfigureStartupArchive(
        string? path,
        string formatId = "pak",
        bool initializeDocument = true)
    {
        _startupArchivePath = path;
        _startupFormatId = formatId;
        _initializeDocument = initializeDocument;
    }

    public void OpenArchiveDialog()
    {
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() => _viewModel.OpenCommand.Execute(null)));
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await _viewModel.InitializeAsync(
                _startupArchivePath,
                _startupFormatId,
                _initializeDocument)
            .ConfigureAwait(true);
    }

    private void ViewModel_OnCloseRequested(object? sender, EventArgs e) => Close();

    private void MinimizeWindow_OnClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
        e.Handled = true;
    }

    private void MaximizeWindow_OnClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Maximized;
        e.Handled = true;
    }

    private void RestoreWindow_OnClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Normal;
        e.Handled = true;
    }

    private void CloseWindow_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
        e.Handled = true;
    }

    private void FolderTree_OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FolderTreeNodeViewModel folder)
        {
            _viewModel.SelectFolder(folder);
        }
    }

    private void ClearSearch_OnClick(object sender, RoutedEventArgs e)
    {
        _viewModel.SearchText = string.Empty;
        ArchiveSearchBox.Focus();
    }

    private void FolderPaneToggle_OnClick(object sender, RoutedEventArgs e)
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

    private void FolderSplitter_OnDragStarted(object sender, DragStartedEventArgs e) =>
        StopFolderPaneAnimation();

    private void FolderSplitter_OnDragCompleted(object sender, DragCompletedEventArgs e) =>
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
        _folderPaneAnimationTimer.Tick += FolderPaneAnimation_OnTick;
        _folderPaneAnimationTimer.Start();
    }

    private void FolderPaneAnimation_OnTick(object? sender, EventArgs e)
    {
        var elapsed = (DateTimeOffset.UtcNow - _folderPaneAnimationStartedAt).TotalMilliseconds;
        var progress = Math.Clamp(elapsed / FolderPaneAnimationDuration.TotalMilliseconds, 0, 1);
        // Ease out cubic, so the pane decelerates into its resting width.
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
        _folderPaneAnimationTimer.Tick -= FolderPaneAnimation_OnTick;
        _folderPaneAnimationTimer = null;
    }

    private void CollapseFolderPane() => AnimateFolderPaneWidth(0, collapseWhenDone: true);

    // A drag from the grip keeps the grip alive (hiding it would drop the mouse
    // capture mid-drag), so it opts out of the indicator half of the chrome.
    private void SetFolderPaneChrome(bool collapsed, bool updateIndicator = true)
    {
        FolderSplitterColumn.Width = collapsed ? new GridLength(0) : new GridLength(6);
        FolderSplitter.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        if (updateIndicator)
        {
            FolderPaneCollapsedIndicator.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        }
        ContentPaneBorder.CornerRadius = collapsed
            ? new CornerRadius(0)
            : new CornerRadius(12, 0, 0, 0);
        ContentPaneBorder.BorderThickness = collapsed
            ? new Thickness(0, 1, 0, 0)
            : new Thickness(1, 1, 0, 0);
    }

    private void FolderPaneIndicator_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        StopFolderPaneAnimation();
        _isFolderPaneIndicatorPressed = true;
        _isDraggingFolderPaneIndicator = false;
        _suppressFolderPaneIndicatorClick = false;
        _folderPaneIndicatorPressX = e.GetPosition(ContentLayoutGrid).X;
        FolderPaneCollapsedIndicator.CaptureMouse();
    }

    private void FolderPaneIndicator_OnMouseMove(object sender, MouseEventArgs e)
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

    private void FolderPaneIndicator_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isFolderPaneIndicatorPressed)
        {
            return;
        }

        _isFolderPaneIndicatorPressed = false;
        FolderPaneCollapsedIndicator.ReleaseMouseCapture();

        if (!_isDraggingFolderPaneIndicator)
        {
            return;
        }

        _isDraggingFolderPaneIndicator = false;
        SettleFolderPaneWidth(FolderPaneColumn.ActualWidth);
    }

    private double ClampDraggedFolderPaneWidth(double width)
    {
        var available = Math.Max(0, ContentLayoutGrid.ActualWidth - FolderPaneMinContentWidth);
        return Math.Clamp(width, 0, available);
    }

    private void ItemList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindVisualAncestor<TextBox>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        if (ItemsControl.ContainerFromElement(ItemList, e.OriginalSource as DependencyObject) is ListViewItem item &&
            item.DataContext is ArchiveItemViewModel archiveItem)
        {
            /* Preview-native assets stay in PakScape; anything else goes to its own app. */
            if (ArchivePreviewBuilder.OpensInQuickPreview(archiveItem.Node))
            {
                ShowQuickPreview([archiveItem.Node]);
                return;
            }

            _viewModel.OpenItem(archiveItem);
        }
    }

    private void ItemList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CancelPendingRenameClick();
        _viewModel.SetSelectedItems(ItemList.SelectedItems.Cast<ArchiveItemViewModel>());
        PreviewSelectionIfPreferred();
    }

    /// <summary>Opens Quick Preview for the new selection when that preference is on.</summary>
    private void PreviewSelectionIfPreferred()
    {
        if (!PakScapeSettings.Current.QuickPreviewOnSelection ||
            _previewWindow is { IsVisible: true })
        {
            return;
        }

        var nodes = ItemList.SelectedItems
            .Cast<ArchiveItemViewModel>()
            .Select(item => item.Node)
            .ToList();
        if (nodes.Count == 0 || !ArchivePreviewBuilder.OpensInQuickPreview(nodes[0]))
        {
            return;
        }

        ShowQuickPreview(nodes);

        /* Keep the keyboard on the item list so arrow keys keep browsing the archive. */
        Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                Activate();
                _ = ItemList.Focus();
            }));
    }

    private void ItemList_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        CancelPendingRenameClick();
        if (FindVisualAncestor<TextBox>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None)
        {
            ToggleQuickPreview();
            e.Handled = true;
        }
    }

    private void ViewModel_OnRenameRequested(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(BeginInlineRename));
    }

    private void BeginInlineRename()
    {
        var item = _viewModel.SelectedItem;
        if (item is null)
        {
            return;
        }

        foreach (var other in _viewModel.CurrentItems.Where(candidate => !ReferenceEquals(candidate, item)))
        {
            other.EndRenaming();
        }
        item.BeginRenaming();
        CommandManager.InvalidateRequerySuggested();
        ItemList.ScrollIntoView(item);
        ItemList.UpdateLayout();

        if (ItemList.ItemContainerGenerator.ContainerFromItem(item) is not ListViewItem container ||
            FindInlineRenameTextBox(container) is not { } editor)
        {
            item.EndRenaming();
            CommandManager.InvalidateRequerySuggested();
            return;
        }

        editor.Focus();
        var extensionLength = item.Node is ArchiveFileNode
            ? Path.GetExtension(item.EditName).Length
            : 0;
        editor.Select(0, Math.Max(0, item.EditName.Length - extensionLength));
    }

    private void ArchiveName_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_viewModel.CanRenameArchive)
        {
            return;
        }

        _viewModel.BeginArchiveRename();
        e.Handled = true;

        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(FocusArchiveNameEditor));
    }

    private void FocusArchiveNameEditor()
    {
        if (!_viewModel.IsRenamingArchive)
        {
            return;
        }

        ArchiveNameEditor.Focus();
        var name = ArchiveNameEditor.Text;
        var extensionLength = Path.GetExtension(name).Length;
        ArchiveNameEditor.Select(0, Math.Max(0, name.Length - extensionLength));
    }

    private void ArchiveNameEditor_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                CommitArchiveRename();
                e.Handled = true;
                break;
            case Key.Escape:
                _viewModel.CancelArchiveRename();
                ItemList.Focus();
                e.Handled = true;
                break;
        }
    }

    private void ArchiveNameEditor_OnLostKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        CommitArchiveRename();
    }

    private void CommitArchiveRename()
    {
        if (!_viewModel.IsRenamingArchive)
        {
            return;
        }

        _viewModel.CommitArchiveRename(ArchiveNameEditor.Text);
        if (ArchiveNameEditor.IsKeyboardFocusWithin)
        {
            ItemList.Focus();
        }
    }

    /// <summary>Commits an open inline rename when the click lands anywhere else. Clicking
    /// another item already commits by moving keyboard focus, but empty list space and
    /// other chrome never take focus, which would otherwise leave the editor open.</summary>
    private void MainWindow_OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (FindVisualAncestor<TextBox>(e.OriginalSource as DependencyObject) is { Tag: "InlineRename" })
        {
            return;
        }

        if (_viewModel.CurrentItems.FirstOrDefault(item => item.IsRenaming) is not { } renaming)
        {
            return;
        }

        renaming.EndRenaming();
        CommandManager.InvalidateRequerySuggested();
        _viewModel.CommitItemRename(renaming, renaming.EditName);
    }

    private void InlineRenameTextBox_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox editor ||
            editor.DataContext is not ArchiveItemViewModel item)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                CommitInlineRename(editor, item);
                e.Handled = true;
                break;
            case Key.Escape:
                item.EndRenaming();
                CommandManager.InvalidateRequerySuggested();
                ItemList.Focus();
                e.Handled = true;
                break;
        }
    }

    private void InlineRenameTextBox_OnLostKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox editor &&
            editor.DataContext is ArchiveItemViewModel { IsRenaming: true } item)
        {
            CommitInlineRename(editor, item);
        }
    }

    private void CommitInlineRename(TextBox editor, ArchiveItemViewModel item)
    {
        if (!item.IsRenaming)
        {
            return;
        }

        item.EndRenaming();
        CommandManager.InvalidateRequerySuggested();
        _viewModel.CommitItemRename(item, editor.Text);
        ItemList.Focus();
    }

    private static TextBox? FindInlineRenameTextBox(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is TextBox { Tag: "InlineRename" } editor)
            {
                return editor;
            }
            if (FindInlineRenameTextBox(child) is { } nested)
            {
                return nested;
            }
        }
        return null;
    }

    private static T? FindVisualAncestor<T>(DependencyObject? element)
        where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T match)
            {
                return match;
            }

            // Text input can originate from a Run/other ContentElement rather
            // than a Visual. VisualTreeHelper.GetParent throws for those nodes,
            // so switch to the corresponding content-tree parent operation.
            element = element switch
            {
                Visual or Visual3D => VisualTreeHelper.GetParent(element),
                ContentElement content => ContentOperations.GetParent(content),
                _ => null,
            };
        }
        return null;
    }

    private void QuickPreview_OnClick(object sender, RoutedEventArgs e)
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

        // Demos have a dedicated interactive destination. Match double-click/Open
        // so Space takes a playable demo straight into the browser player.
        if (_viewModel.PlayDemoInBrowserCommand.CanExecute(null))
        {
            _viewModel.PlayDemoInBrowserCommand.Execute(null);
            return;
        }

        ShowQuickPreview(ItemList.SelectedItems
            .Cast<ArchiveItemViewModel>()
            .Select(item => item.Node)
            .ToList());
    }

    private void ShowQuickPreview(IReadOnlyList<ArchiveNode> nodes)
    {
        ShowPreview(nodes, showSkybox: false);
    }

    private void ViewSkybox_OnClick(object sender, RoutedEventArgs e)
    {
        var nodes = ItemList.SelectedItems
            .Cast<ArchiveItemViewModel>()
            .Select(item => item.Node)
            .Take(1)
            .ToList();
        if (nodes.Count == 1)
        {
            ShowPreview(nodes, showSkybox: true);
        }
    }

    private void ShowPreview(IReadOnlyList<ArchiveNode> nodes, bool showSkybox)
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
            var previewWindow = new PreviewWindow(nodes, showSkybox) { Owner = this };
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
                previewWindow.Show();
            }
            catch
            {
                _previewWindow = null;
                throw;
            }
        }
        catch (Exception exception)
        {
            ShowWarning(
                showSkybox ? "Unable to Preview Skybox" : "Unable to Preview Selection",
                exception.Message);
        }
    }

    private void ItemList_OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        CancelPendingRenameClick();
        _contextMenuTargetPrepared = true;
        if (ItemsControl.ContainerFromElement(
                ItemList,
                e.OriginalSource as DependencyObject) is ListViewItem item &&
            item.DataContext is ArchiveItemViewModel archiveItem)
        {
            if (!item.IsSelected)
            {
                ItemList.SelectedItems.Clear();
            }
            item.IsSelected = true;
            item.Focus();
            _viewModel.SetContextTarget(archiveItem.Node);
        }
        else
        {
            ItemList.SelectedItems.Clear();
            _viewModel.SetContextTarget(null);
        }
    }

    private void ItemContextMenu_OnOpened(object sender, RoutedEventArgs e)
    {
        if (!_contextMenuTargetPrepared)
        {
            var target = ItemList.SelectedItems.Count == 1
                ? (ItemList.SelectedItems[0] as ArchiveItemViewModel)?.Node
                : null;
            _viewModel.SetContextTarget(target);
        }
    }

    private void ItemContextMenu_OnClosed(object sender, RoutedEventArgs e)
    {
        _contextMenuTargetPrepared = false;
        _viewModel.SetContextTarget(null);
    }

    private void ItemList_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        CancelPendingRenameClick();
        _renameClickCandidate = null;
        var container = ItemsControl.ContainerFromElement(
            ItemList,
            e.OriginalSource as DependencyObject) as ListViewItem;
        _dragStartPoint = container is not null ? e.GetPosition(ItemList) : null;

        /* Explorer-style slow double click: clicking the name of an item that is
           already the lone selection starts an inline rename. Selection has not
           been applied yet on the preview pass, so IsSelected is the prior state. */
        if (e.ClickCount == 1 &&
            Keyboard.Modifiers == ModifierKeys.None &&
            e.OriginalSource is TextBlock { Tag: "NameText" } label &&
            IsPointOverRenderedText(label, e.GetPosition(label)) &&
            container is { IsSelected: true, DataContext: ArchiveItemViewModel candidate } &&
            ItemList.SelectedItems.Count == 1)
        {
            _renameClickCandidate = candidate;
            _renameClickOrigin = e.GetPosition(ItemList);
        }
    }

    private void ItemList_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var candidate = _renameClickCandidate;
        _renameClickCandidate = null;
        if (candidate is null ||
            e.ClickCount != 1 ||
            Keyboard.Modifiers != ModifierKeys.None ||
            !IsSoleSelectedItem(candidate) ||
            !_viewModel.RenameCommand.CanExecute(null))
        {
            return;
        }

        /* A click that turned into a drag is not a rename request. */
        if (_renameClickOrigin is { } origin)
        {
            var current = e.GetPosition(ItemList);
            if (Math.Abs(current.X - origin.X) >= SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(current.Y - origin.Y) >= SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }
        }

        _pendingRenameItem = candidate;
        _renameClickTimer.Start();
    }

    private void ItemList_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e) =>
        CancelPendingRenameClick();

    private void RenameClickTimer_OnTick(object? sender, EventArgs e)
    {
        var item = _pendingRenameItem;
        CancelPendingRenameClick();
        if (item is null ||
            Mouse.LeftButton == MouseButtonState.Pressed ||
            !IsSoleSelectedItem(item) ||
            !_viewModel.RenameCommand.CanExecute(null))
        {
            return;
        }

        _viewModel.RenameCommand.Execute(null);
    }

    /// <summary>Reports whether <paramref name="point"/> lands on drawn text rather than the
    /// empty layout width the label stretches over.</summary>
    private static bool IsPointOverRenderedText(TextBlock label, Point point)
    {
        var margin = label.Margin;
        var textWidth = label.DesiredSize.Width - margin.Left - margin.Right;
        var textHeight = label.DesiredSize.Height - margin.Top - margin.Bottom;
        if (textWidth <= 0 || textHeight <= 0)
        {
            return false;
        }

        /* Text is drawn at the top of the layout slot, offset horizontally by its alignment. */
        var left = label.TextAlignment switch
        {
            TextAlignment.Center => Math.Max(0, (label.ActualWidth - textWidth) / 2),
            TextAlignment.Right => Math.Max(0, label.ActualWidth - textWidth),
            _ => 0,
        };
        return point.X >= left &&
            point.X <= left + textWidth &&
            point.Y >= 0 &&
            point.Y <= textHeight;
    }

    private bool IsSoleSelectedItem(ArchiveItemViewModel item) =>
        ItemList.SelectedItems.Count == 1 && ReferenceEquals(ItemList.SelectedItems[0], item);

    /// <summary>Drops an armed rename. The candidate from the current press is left alone;
    /// it is re-validated against the selection when the button is released.</summary>
    private void CancelPendingRenameClick()
    {
        _renameClickTimer.Stop();
        _pendingRenameItem = null;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    private void ItemList_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_isStartingDrag || e.LeftButton != MouseButtonState.Pressed || _dragStartPoint is not { } start)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                _dragStartPoint = null;
            }
            return;
        }

        var current = e.GetPosition(ItemList);
        if (Math.Abs(current.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _dragStartPoint = null;
        _isStartingDrag = true;
        CancelPendingRenameClick();
        IReadOnlyList<string> paths = [];
        try
        {
            paths = _viewModel.PrepareSelectedItemsForDrag();
            if (paths.Count == 0)
            {
                return;
            }

            var data = new DataObject(DataFormats.FileDrop, paths.ToArray());
            DragDrop.DoDragDrop(ItemList, data, DragDropEffects.Copy);
            e.Handled = true;
        }
        catch (Exception exception)
        {
            ShowWarning("Unable to Drag Selection", exception.Message);
        }
        finally
        {
            _viewModel.ReleaseTemporaryTransfer(paths);
            _isStartingDrag = false;
        }
    }

    private void SortHeader_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string columnName })
        {
            _viewModel.SortBy(columnName);
        }
    }

    private void DetailsColumnHeader_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Button { Tag: string columnKey } header)
        {
            _columnHeaderDragStart = e.GetPosition(DetailsHeaderColumns);
            _columnHeaderGrabOffset = e.GetPosition(header).X;
            _columnHeaderDragKey = columnKey;
        }
    }

    private void DetailsColumnHeader_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) =>
        ResetColumnHeaderDrag();

    private void DetailsColumnHeader_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed ||
            sender is not Button header ||
            _columnHeaderDragStart is not { } start ||
            _columnHeaderDragKey is not { } columnKey)
        {
            return;
        }

        var current = e.GetPosition(DetailsHeaderColumns);
        if (Math.Abs(current.X - start.X) < SystemParameters.MinimumHorizontalDragDistance)
        {
            return;
        }

        var grabOffset = _columnHeaderGrabOffset;
        ResetColumnHeaderDrag();
        try
        {
            ShowColumnDragGhost(header, grabOffset, current.X);
            DragDrop.DoDragDrop(
                header,
                new DataObject(DetailsColumnDragFormat, columnKey),
                DragDropEffects.Move);
        }
        finally
        {
            EndColumnDragVisuals();
            /* The header button captured the mouse on press; the drag consumed its release. */
            header.ReleaseMouseCapture();
        }
    }

    /// <summary>Explorer keeps the plain arrow under the ghost instead of the OLE move cursor.</summary>
    private void DetailsColumnHeader_OnGiveFeedback(object sender, GiveFeedbackEventArgs e)
    {
        if (DetailsColumnDragGhost.Visibility != Visibility.Visible)
        {
            return;
        }

        e.UseDefaultCursors = false;
        Mouse.SetCursor(Cursors.Arrow);
        e.Handled = true;
    }

    private void DetailsHeader_OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DetailsColumnDragFormat))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var x = e.GetPosition(DetailsHeaderColumns).X;
        MoveColumnDragGhost(x);
        ShowColumnDropIndicator(ColumnInsertionIndex(x));
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    /// <summary>Only a real exit hides the marker; moving between header buttons bubbles a
    /// DragLeave here too.</summary>
    private void DetailsHeader_OnDragLeave(object sender, DragEventArgs e)
    {
        var position = e.GetPosition(DetailsHeaderColumns);
        if (position.X < 0 ||
            position.Y < 0 ||
            position.X > DetailsHeaderColumns.ActualWidth ||
            position.Y > DetailsHeaderColumns.ActualHeight)
        {
            HideColumnDropIndicator();
        }
    }

    private void DetailsHeader_OnDrop(object sender, DragEventArgs e)
    {
        HideColumnDropIndicator();
        if (e.Data.GetData(DetailsColumnDragFormat) is string columnKey)
        {
            _viewModel.MoveDetailsColumn(columnKey, ColumnInsertionIndex(e.GetPosition(DetailsHeaderColumns).X));
        }
        e.Handled = true;
    }

    private void DetailsHeader_OnResizeCompleted(object sender, DragCompletedEventArgs e) =>
        _viewModel.SaveDetailsColumnLayout();

    private void ResetColumnHeaderDrag()
    {
        _columnHeaderDragStart = null;
        _columnHeaderDragKey = null;
    }

    /// <summary>Lifts a translucent copy of the header out from under the cursor.</summary>
    private void ShowColumnDragGhost(Button header, double grabOffset, double pointerX)
    {
        _columnHeaderGrabOffset = grabOffset;
        DetailsColumnDragGhostSurface.Width = header.ActualWidth;
        DetailsColumnDragGhostSurface.Height = header.ActualHeight;
        DetailsColumnDragGhostSurface.Fill = new VisualBrush(header)
        {
            Stretch = Stretch.None,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top,
        };
        DetailsColumnDragGhostTransform.BeginAnimation(TranslateTransform.XProperty, null);
        DetailsColumnDragGhostTransform.X = pointerX - grabOffset;
        DetailsColumnDragGhost.Visibility = Visibility.Visible;
        DetailsColumnDragGhost.BeginAnimation(OpacityProperty, FadeTo(0.85, ColumnDragFadeInDuration));
    }

    private void MoveColumnDragGhost(double pointerX)
    {
        if (DetailsColumnDragGhost.Visibility != Visibility.Visible)
        {
            return;
        }

        var maximum = Math.Max(0, DetailsHeaderColumns.ActualWidth - DetailsColumnDragGhost.ActualWidth);
        DetailsColumnDragGhostTransform.X = Math.Clamp(pointerX - _columnHeaderGrabOffset, 0, maximum);
    }

    private void EndColumnDragVisuals()
    {
        HideColumnDropIndicator();
        if (DetailsColumnDragGhost.Visibility != Visibility.Visible)
        {
            return;
        }

        var fade = FadeTo(0, ColumnDragFadeOutDuration);
        fade.Completed += (_, _) =>
        {
            DetailsColumnDragGhost.Visibility = Visibility.Collapsed;
            DetailsColumnDragGhostSurface.Fill = null;
        };
        DetailsColumnDragGhost.BeginAnimation(OpacityProperty, fade);
    }

    private static DoubleAnimation FadeTo(double target, Duration duration) => new(target, duration)
    {
        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        FillBehavior = FillBehavior.HoldEnd,
    };

    /// <summary>Display position the dragged column would take if dropped at <paramref name="x"/>.</summary>
    private int ColumnInsertionIndex(double x)
    {
        var offset = 0.0;
        var columns = DetailsHeaderColumns.ColumnDefinitions;
        for (var index = 0; index < columns.Count; index++)
        {
            var width = columns[index].ActualWidth;
            if (x < offset + (width / 2))
            {
                return index;
            }
            offset += width;
        }
        return columns.Count;
    }

    private void ShowColumnDropIndicator(int insertionIndex)
    {
        var offset = 0.0;
        var columns = DetailsHeaderColumns.ColumnDefinitions;
        for (var index = 0; index < insertionIndex && index < columns.Count; index++)
        {
            offset += columns[index].ActualWidth;
        }

        var edge = Math.Max(0, Math.Min(offset, DetailsHeaderColumns.ActualWidth - DetailsColumnDropIndicator.Width));
        if (_columnDropIndicatorShown)
        {
            /* Slide between slots rather than jumping, the way the WinUI drop marker moves. */
            if (Math.Abs(DetailsColumnDropIndicatorTransform.X - edge) > 0.5)
            {
                DetailsColumnDropIndicatorTransform.BeginAnimation(
                    TranslateTransform.XProperty,
                    new DoubleAnimation(edge, ColumnDropIndicatorSlideDuration)
                    {
                        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                        FillBehavior = FillBehavior.HoldEnd,
                    });
            }
            return;
        }

        _columnDropIndicatorShown = true;
        DetailsColumnDropIndicatorTransform.BeginAnimation(TranslateTransform.XProperty, null);
        DetailsColumnDropIndicatorTransform.X = edge;
        DetailsColumnDropIndicator.Visibility = Visibility.Visible;
        DetailsColumnDropIndicator.BeginAnimation(OpacityProperty, FadeTo(1, ColumnDragFadeInDuration));
    }

    private void HideColumnDropIndicator()
    {
        if (!_columnDropIndicatorShown)
        {
            return;
        }

        _columnDropIndicatorShown = false;
        var fade = FadeTo(0, ColumnDragFadeOutDuration);
        fade.Completed += (_, _) =>
        {
            /* A drag that came straight back keeps the marker on screen. */
            if (!_columnDropIndicatorShown)
            {
                DetailsColumnDropIndicator.Visibility = Visibility.Collapsed;
            }
        };
        DetailsColumnDropIndicator.BeginAnimation(OpacityProperty, fade);
    }

    private void Cut_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        if (FocusedTextBox() is { } editor)
        {
            e.CanExecute = !editor.IsReadOnly && editor.SelectionLength > 0;
            return;
        }
        e.CanExecute = _viewModel.CutCommand.CanExecute(null);
    }

    private void Cut_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (FocusedTextBox() is { } editor)
        {
            editor.Cut();
            e.Handled = true;
            return;
        }
        _viewModel.CutCommand.Execute(null);
        e.Handled = true;
    }

    private void Copy_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        if (FocusedTextBox() is { } editor)
        {
            e.CanExecute = editor.SelectionLength > 0;
            return;
        }
        e.CanExecute = _viewModel.CopyCommand.CanExecute(null);
    }

    private void Copy_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (FocusedTextBox() is { } editor)
        {
            editor.Copy();
            e.Handled = true;
            return;
        }
        _viewModel.CopyCommand.Execute(null);
        e.Handled = true;
    }

    private void Paste_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        if (FocusedTextBox() is { } editor)
        {
            e.CanExecute = !editor.IsReadOnly && ClipboardHasText();
            return;
        }
        e.CanExecute = _viewModel.PasteCommand.CanExecute(null);
    }

    private void Paste_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (FocusedTextBox() is { } editor)
        {
            editor.Paste();
            e.Handled = true;
            return;
        }
        _viewModel.PasteCommand.Execute(null);
        e.Handled = true;
    }

    private void SelectAll_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        if (FocusedTextBox() is { } editor)
        {
            e.CanExecute = editor.Text.Length > 0;
            return;
        }
        e.CanExecute = ItemList.Items.Count > 0 && !_viewModel.IsBusy;
    }

    private void SelectAll_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (FocusedTextBox() is { } editor)
        {
            editor.SelectAll();
            e.Handled = true;
            return;
        }
        ItemList.SelectAll();
        ItemList.Focus();
        e.Handled = true;
    }

    private void ItemList_OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && !_viewModel.IsBusy
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void ItemList_OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            await _viewModel.AddDroppedPathsAsync(paths).ConfigureAwait(true);
        }
        e.Handled = true;
    }

    private async void Window_OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
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
            if (await _viewModel.CanCloseAsync().ConfigureAwait(true))
            {
                _allowClose = true;
                /* Let the current Closing event return before starting a new close. */
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(Close));
            }
        }
        finally
        {
            _isCloseConfirmationPending = false;
        }
    }

    private void ShowWarning(string title, string message)
    {
        var dialog = new MessageDialogWindow(title, message, MessageDialogButtons.Ok)
        {
            Owner = this,
        };
        _ = dialog.ShowDialogResult();
    }

    private static TextBox? FocusedTextBox() =>
        Keyboard.FocusedElement as TextBox;

    private static bool ClipboardHasText()
    {
        try
        {
            return Clipboard.ContainsText();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }
}
