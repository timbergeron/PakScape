using System.Windows;
using System.Windows.Input;
using PakStudio.App.ViewModels;

namespace PakStudio.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        DataContext = new SettingsViewModel();
    }

    private void CloseWindow_OnExecuted(object sender, ExecutedRoutedEventArgs e) => Close();
}
