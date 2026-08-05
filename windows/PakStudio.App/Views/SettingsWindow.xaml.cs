using System.Windows;
using PakStudio.App.ViewModels;

namespace PakStudio.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        DataContext = new SettingsViewModel();
    }
}
