using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using PakStudio.Core.Nodes;
using PakStudio.Core.Preview;

namespace PakScape.Linux.Views;

public sealed class ItemInfoWindow : Window
{
    public ItemInfoWindow(ArchiveNode node, string archiveName)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveName);
        var name = node.Name.Length == 0 ? archiveName : node.Name;
        Title = $"{name} Info";
        Width = 500;
        Height = 540;
        MinWidth = 420;
        MinHeight = 320;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var details = new StackPanel { Spacing = 8 };
        details.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 20,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        details.Children.Add(new TextBlock { Text = TypeText(node), Opacity = 0.72 });
        details.Children.Add(new Separator { Margin = new Thickness(0, 10) });
        AddDetail(details, "Archive", archiveName);
        AddDetail(details, "Path", node.FullPath);

        if (node is ArchiveFileNode file)
        {
            AddDetail(details, "Size", ExactSize(file.Size));
            if (file.ModifiedUtc is { } modified)
            {
                AddDetail(details, "Modified", modified.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
            }
        }
        else if (node is ArchiveFolderNode folder)
        {
            var summary = Summarize(folder);
            AddDetail(details, "Size", ExactSize(summary.Bytes));
            AddDetail(details, "Contents",
                $"{summary.Files:N0} {(summary.Files == 1 ? "file" : "files")}, " +
                $"{summary.Folders:N0} {(summary.Folders == 1 ? "folder" : "folders")}");
        }

        var formatDetails = ArchiveMetadataInspector.Inspect(node).Details;
        if (formatDetails.Count > 0)
        {
            details.Children.Add(new Separator { Margin = new Thickness(0, 10) });
            details.Children.Add(new TextBlock
            {
                Text = "Format Details",
                FontSize = 15,
                FontWeight = FontWeight.SemiBold,
            });
            foreach (var detail in formatDetails)
            {
                AddDetail(details, detail.Label, detail.Value);
            }
        }

        var done = new Button
        {
            Content = "Done",
            MinWidth = 88,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        done.Click += (_, _) => Close();
        var layout = new Grid
        {
            Margin = new Thickness(24, 22, 24, 18),
            RowDefinitions = new RowDefinitions("*,Auto"),
        };
        layout.Children.Add(new ScrollViewer
        {
            Content = details,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        });
        Grid.SetRow(done, 1);
        layout.Children.Add(done);
        Content = layout;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        };
    }

    private static void AddDetail(Panel panel, string label, string value)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("120,*") };
        grid.Children.Add(new TextBlock
        {
            Text = $"{label}:",
            Opacity = 0.72,
            Margin = new Thickness(0, 2, 14, 2),
        });
        var text = new SelectableTextBlock
        {
            Text = value,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2),
        };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        panel.Children.Add(grid);
    }

    private static string TypeText(ArchiveNode node) => node switch
    {
        ArchiveFolderNode => "Folder",
        ArchiveFileNode file when string.IsNullOrWhiteSpace(file.Extension) => "File",
        ArchiveFileNode file => $"{file.Extension.TrimStart('.').ToUpperInvariant()} File",
        _ => "Item",
    };

    private static string ExactSize(long bytes) =>
        $"{ArchivePreviewBuilder.FormatSize(bytes)} ({bytes:N0} bytes)";

    private static (long Bytes, int Files, int Folders) Summarize(ArchiveFolderNode folder)
    {
        long bytes = 0;
        var files = 0;
        var folders = 0;
        var pending = new Stack<ArchiveFolderNode>();
        pending.Push(folder);
        while (pending.TryPop(out var current))
        {
            foreach (var file in current.Files)
            {
                bytes = bytes > long.MaxValue - file.Size ? long.MaxValue : bytes + file.Size;
                files = files == int.MaxValue ? int.MaxValue : files + 1;
            }
            foreach (var child in current.Folders)
            {
                folders = folders == int.MaxValue ? int.MaxValue : folders + 1;
                pending.Push(child);
            }
        }
        return (bytes, files, folders);
    }
}
