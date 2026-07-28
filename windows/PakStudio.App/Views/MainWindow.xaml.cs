using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PakStudio.App.ViewModels;
using PakStudio.Core.Interfaces;
using PakStudio.Core.Nodes;
using PakStudio.Core.Preview;

namespace PakStudio.App.Views;

public partial class MainWindow : Window
{
    private const double FolderPaneCollapseThreshold = 120;
    private const double FolderPaneReopenWidth = 220;

    private readonly MainWindowViewModel _viewModel;
    private readonly IArchiveFileTransferService _fileTransferService;
    private PreviewWindow? _previewWindow;
    private bool _allowClose;
    private bool _isCloseConfirmationPending;
    private string? _startupArchivePath;
    private string _startupFormatId = "pak";
    private Point? _dragStartPoint;
    private bool _isStartingDrag;
    private bool _folderPaneWasCollapsedAtDragStart;
    private bool _contextMenuTargetPrepared;
    private double _lastExpandedFolderPaneWidth = 280;

    public MainWindow(
        MainWindowViewModel viewModel,
        IArchiveFileTransferService fileTransferService)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _fileTransferService = fileTransferService;
        DataContext = _viewModel;
        _viewModel.RenameRequested += ViewModel_OnRenameRequested;
        _viewModel.CloseRequested += ViewModel_OnCloseRequested;
        Loaded += OnLoaded;
    }

    internal string? ArchivePath => _viewModel.Document?.FilePath;

    public void ConfigureStartupArchive(string? path, string formatId = "pak")
    {
        _startupArchivePath = path;
        _startupFormatId = formatId;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await _viewModel.InitializeAsync(_startupArchivePath, _startupFormatId).ConfigureAwait(true);
    }

    private void ViewModel_OnCloseRequested(object? sender, EventArgs e) => Close();

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

    private void FolderSplitter_OnDragStarted(object sender, DragStartedEventArgs e)
    {
        _folderPaneWasCollapsedAtDragStart = FolderPaneColumn.ActualWidth < 1;
    }

    private void FolderSplitter_OnDragCompleted(object sender, DragCompletedEventArgs e)
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
        FolderSplitter.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        ContentPaneBorder.CornerRadius = collapsed
            ? new CornerRadius(0)
            : new CornerRadius(12, 0, 0, 0);
        ContentPaneBorder.BorderThickness = collapsed
            ? new Thickness(0, 1, 0, 0)
            : new Thickness(1, 1, 0, 0);
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
        _viewModel.SetSelectedItems(ItemList.SelectedItems.Cast<ArchiveItemViewModel>());
    }

    private void ItemList_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
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
            element = VisualTreeHelper.GetParent(element);
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
        _dragStartPoint = ItemsControl.ContainerFromElement(
                ItemList,
                e.OriginalSource as DependencyObject) is ListViewItem
            ? e.GetPosition(ItemList)
            : null;
    }

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
                Close();
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
