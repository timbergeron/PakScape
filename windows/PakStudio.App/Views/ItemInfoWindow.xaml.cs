using System.Windows;
using System.Windows.Input;
using PakStudio.App.ViewModels;

namespace PakStudio.App.Views;

public partial class ItemInfoWindow : Window
{
    public ItemInfoWindow(ItemInfoViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
    }

    private void DoneButton_OnClick(object sender, RoutedEventArgs e) => Close();

    private void Window_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }
}
