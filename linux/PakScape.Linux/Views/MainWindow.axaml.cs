using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PakScape.Linux.Models;
using PakScape.Linux.ViewModels;

namespace PakScape.Linux.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _viewModel;
    private string? _startupPath;
    private bool _closeConfirmed;
    private PreviewWindow? _previewWindow;

    public MainWindow()
    {
        InitializeComponent();
        ArchiveGrid.AddHandler(
            InputElement.KeyDownEvent,
            OnArchiveGridKeyDown,
            RoutingStrategies.Tunnel);
    }

    public void Configure(MainWindowViewModel viewModel, string? startupPath)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        if (_viewModel is not null)
        {
            throw new InvalidOperationException("The main window has already been configured.");
        }

        _viewModel = viewModel;
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
        if (await ViewModel.CanCloseAsync())
        {
            _closeConfirmed = true;
            Close();
        }
    }

    private void OnCloseRequested(object? sender, EventArgs e)
    {
        Close();
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
        ViewModel.SetSelectedItems(
            ArchiveGrid.SelectedItems.OfType<ArchiveItemViewModel>());
    }

    private async void OnItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ArchiveGrid.SelectedItem is ArchiveItemViewModel item)
        {
            await ViewModel.OpenItemAsync(item);
            e.Handled = true;
        }
    }

    private void OnArchiveGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.None)
        {
            ToggleQuickPreview();
            e.Handled = true;
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

        var nodes = ArchiveGrid.SelectedItems
            .OfType<ArchiveItemViewModel>()
            .Select(item => item.Node)
            .ToList();
        if (nodes.Count == 0)
        {
            return;
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
}
