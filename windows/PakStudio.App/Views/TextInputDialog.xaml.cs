using System.Windows;

namespace PakStudio.App.Views;

public partial class TextInputDialog : Window
{
    public TextInputDialog(string title, string message, string initialValue)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        ValueTextBox.Text = initialValue;
        Loaded += (_, _) =>
        {
            ValueTextBox.Focus();
            ValueTextBox.SelectAll();
        };
    }

    public string Value => ValueTextBox.Text;

    private void Accept_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
