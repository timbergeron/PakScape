using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PakStudio.App.ViewModels;

namespace PakStudio.App.Views;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private PreviewWindow? _previewWindow;
    private bool _allowClose;
    private string? _startupArchivePath;

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
            MessageBox.Show(
                this,
                exception.Message,
                "Unable to Preview Selection",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
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
        if (await _viewModel.CanCloseAsync().ConfigureAwait(true))
        {
            _allowClose = true;
            Close();
        }
    }
}
