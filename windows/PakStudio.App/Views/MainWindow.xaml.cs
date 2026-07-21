using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using PakStudio.App.ViewModels;

namespace PakStudio.App.Views;

public partial class MainWindow : Window
{
    private const double FolderPaneCollapseThreshold = 120;
    private const double FolderPaneReopenWidth = 220;

    private readonly MainWindowViewModel _viewModel;
    private PreviewWindow? _previewWindow;
    private bool _allowClose;
    private bool _isCloseConfirmationPending;
    private string? _startupArchivePath;
    private Point? _dragStartPoint;
    private bool _isStartingDrag;
    private bool _folderPaneWasCollapsedAtDragStart;
    private double _lastExpandedFolderPaneWidth = 280;

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
        Loaded += OnLoaded;
    }

    public void ConfigureStartupArchive(string? path)
    {
        _startupArchivePath = path;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await _viewModel.InitializeAsync(_startupArchivePath).ConfigureAwait(true);
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
        if (ItemsControl.ContainerFromElement(ItemList, e.OriginalSource as DependencyObject) is ListViewItem item &&
            item.DataContext is ArchiveItemViewModel archiveItem)
        {
            _viewModel.OpenItem(archiveItem);
        }
    }

    private void ItemList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _viewModel.SetSelectedItems(ItemList.SelectedItems.Cast<ArchiveItemViewModel>());
    }

    private void ItemList_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None)
        {
            ToggleQuickPreview();
            e.Handled = true;
        }
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

        var nodes = ItemList.SelectedItems
            .Cast<ArchiveItemViewModel>()
            .Select(item => item.Node)
            .ToList();
        if (nodes.Count == 0)
        {
            return;
        }

        try
        {
            var previewWindow = new PreviewWindow(nodes) { Owner = this };
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
            ShowWarning("Unable to Preview Selection", exception.Message);
        }
    }

    private void ItemList_OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(ItemList, e.OriginalSource as DependencyObject) is ListViewItem item)
        {
            if (!item.IsSelected)
            {
                ItemList.SelectedItems.Clear();
            }
            item.IsSelected = true;
            item.Focus();
        }
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
        e.CanExecute = _viewModel.CutCommand.CanExecute(null);
    }

    private void Cut_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        _viewModel.CutCommand.Execute(null);
        e.Handled = true;
    }

    private void Copy_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = _viewModel.CopyCommand.CanExecute(null);
    }

    private void Copy_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        _viewModel.CopyCommand.Execute(null);
        e.Handled = true;
    }

    private void Paste_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = _viewModel.PasteCommand.CanExecute(null);
    }

    private void Paste_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        _viewModel.PasteCommand.Execute(null);
        e.Handled = true;
    }

    private void SelectAll_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = ItemList.Items.Count > 0 && !_viewModel.IsBusy;
    }

    private void SelectAll_Executed(object sender, ExecutedRoutedEventArgs e)
    {
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
}
