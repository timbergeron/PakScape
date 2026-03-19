using System.Windows;
using System.Windows.Input;
using PakStudio.App.ViewModels;

namespace PakStudio.App.Views;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await _viewModel.InitializeAsync().ConfigureAwait(true);
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
        if (_viewModel.SelectedItem is not null)
        {
            _viewModel.OpenItem(_viewModel.SelectedItem);
        }
    }
}
