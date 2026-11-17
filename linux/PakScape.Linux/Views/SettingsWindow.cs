using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using PakScape.Linux.Services;
using PakStudio.Core.Documents;
using Shapes = Avalonia.Controls.Shapes;

namespace PakScape.Linux.Views;

/// <summary>Preferences window mirroring the macOS settings pane, tab for tab.</summary>
public sealed class SettingsWindow : Window
{
    private const double RowLabelWidth = 150;
    private const double RowControlWidth = 230;
    private const double PageWidth = 540;

    private static readonly string[] AppearanceLabels = ["Automatic", "Light", "Dark"];
    private static readonly AppearancePreference[] AppearanceValues =
        [AppearancePreference.Automatic, AppearancePreference.Light, AppearancePreference.Dark];

    private static readonly string[] DefaultViewLabels = ["Details", "List", "Large Icons", "Small Icons"];
    private static readonly ArchiveViewMode[] DefaultViewValues =
    [
        ArchiveViewMode.Details,
        ArchiveViewMode.List,
        ArchiveViewMode.LargeIcons,
        ArchiveViewMode.SmallIcons,
    ];

    private static readonly string[] DefaultSortLabels = ["Name", "Type", "Size"];
    private static readonly DefaultSortPreference[] DefaultSortValues =
        [DefaultSortPreference.Name, DefaultSortPreference.Type, DefaultSortPreference.Size];

    private static readonly string[] SortOrderLabels = ["Ascending", "Descending"];

    private readonly PakScapeSettings _settings = PakScapeSettings.Current;
    private readonly List<SettingsTab> _tabs = [];
    private readonly Button _associateButton;
    private readonly TextBlock _textSizeCaption;

    public SettingsWindow()
    {
        Width = 620;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        using (var iconStream = AssetLoader.Open(new Uri("avares://PakScape/Assets/pakscape.png")))
        {
            Icon = new WindowIcon(iconStream);
        }

        _textSizeCaption = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            FontWeight = FontWeight.Medium,
            Opacity = 0.72,
            Text = TextSizeCaption(_settings.TextSize),
        };

        _associateButton = new Button
        {
            Content = AssociateButtonText(),
            IsEnabled = !DesktopIntegrationService.IsAssociated,
        };
        _associateButton.Click += (_, _) =>
        {
            DesktopIntegrationService.Associate();
            _associateButton.Content = AssociateButtonText();
            _associateButton.IsEnabled = !DesktopIntegrationService.IsAssociated;
        };

        var pages = new Grid { Width = PageWidth, HorizontalAlignment = HorizontalAlignment.Center };
        var tabStrip = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 10,
            Margin = new Thickness(0, 0, 0, 8),
        };

        AddTab(tabStrip, pages, "General", SlidersIcon(), BuildGeneralPage());
        AddTab(tabStrip, pages, "Archive", BoxIcon(), BuildArchivePage());
        AddTab(tabStrip, pages, "Preview", EyeIcon(), BuildPreviewPage());
        AddTab(tabStrip, pages, "Files", FolderIcon(), BuildFilesPage());

        Content = new StackPanel
        {
            Margin = new Thickness(24, 6, 24, 16),
            Children = { tabStrip, pages },
        };

        SelectTab(_tabs[0]);
        ActualThemeVariantChanged += (_, _) => RefreshTabColors();
    }

    private void AddTab(Panel tabStrip, Panel pages, string title, Shapes.Path icon, Control page)
    {
        var label = new TextBlock
        {
            Text = title,
            HorizontalAlignment = HorizontalAlignment.Center,
            FontSize = 12,
            FontWeight = FontWeight.Medium,
        };
        var button = new Button
        {
            Width = 85,
            Height = 63,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Transparent,
            CornerRadius = new CornerRadius(11),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = new StackPanel
            {
                Spacing = 4,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children = { icon, label },
            },
        };

        page.IsVisible = false;
        var tab = new SettingsTab(title, button, icon, label, page);
        button.Click += (_, _) => SelectTab(tab);
        _tabs.Add(tab);
        tabStrip.Children.Add(button);
        pages.Children.Add(page);
    }

    private void SelectTab(SettingsTab selected)
    {
        foreach (var tab in _tabs)
        {
            var isSelected = ReferenceEquals(tab, selected);
            tab.Page.IsVisible = isSelected;
            tab.Button.Classes.Set("selected", isSelected);
        }

        Title = selected.Title;
        RefreshTabColors();
    }

    /// <summary>Tab colours are assigned directly so they survive a theme change.</summary>
    private void RefreshTabColors()
    {
        var accent = FindBrush("BrandGreenBrush", Brushes.Green);
        var muted = FindBrush("MutedForegroundBrush", Brushes.Gray);
        var border = FindBrush("PanelBorderBrush", Brushes.Transparent);
        var hover = FindBrush("MenuHoverBrush", Brushes.Transparent);

        foreach (var tab in _tabs)
        {
            var isSelected = tab.Button.Classes.Contains("selected");
            var foreground = isSelected ? accent : muted;
            tab.Icon.Stroke = foreground;
            tab.Label.Foreground = foreground;
            tab.Button.Background = isSelected ? hover : Brushes.Transparent;
            tab.Button.BorderBrush = isSelected ? border : Brushes.Transparent;
        }
    }

    private StackPanel BuildGeneralPage() => new StackPanel
    {
        Margin = new Thickness(16, 8),
        Spacing = 8,
        Children =
        {
            PickerRow(
                "Appearance",
                AppearanceLabels,
                Array.IndexOf(AppearanceValues, _settings.Appearance),
                index => _settings.Appearance = AppearanceValues[index]),
            PickerRow(
                "Default view",
                DefaultViewLabels,
                Math.Max(0, Array.IndexOf(DefaultViewValues, _settings.DefaultView)),
                index => _settings.DefaultView = DefaultViewValues[index]),
            PickerRow(
                "Default sort",
                DefaultSortLabels,
                Array.IndexOf(DefaultSortValues, _settings.DefaultSort),
                index => _settings.DefaultSort = DefaultSortValues[index]),
            PickerRow(
                "Sort order",
                SortOrderLabels,
                _settings.DefaultSortAscending ? 0 : 1,
                index => _settings.DefaultSortAscending = index == 0),
            BuildTextSizeRow(),
            Note("The default view applies to newly opened archive windows. PakScape follows the desktop appearance unless you choose a specific mode."),
        },
    };

    private StackPanel BuildArchivePage() => new StackPanel
    {
        Margin = new Thickness(16, 8),
        Spacing = 10,
        Children =
        {
            Toggle(
                "Confirm before deleting archive items",
                _settings.ConfirmDeletion,
                value => _settings.ConfirmDeletion = value),
            Toggle(
                "Confirm before overwriting exported files",
                _settings.ConfirmOverwrite,
                value => _settings.ConfirmOverwrite = value),
            Toggle(
                "Create a backup before saving an archive",
                _settings.BackupBeforeSave,
                value => _settings.BackupBeforeSave = value),
        },
    };

    private StackPanel BuildPreviewPage() => new StackPanel
    {
        Margin = new Thickness(16, 8),
        Spacing = 10,
        Children =
        {
            Toggle(
                "Open Quick Preview when selecting an item",
                _settings.QuickPreviewOnSelection,
                value => _settings.QuickPreviewOnSelection = value),
            Toggle(
                "Animate models when they open",
                _settings.AnimateModels,
                value => _settings.AnimateModels = value),
            Toggle(
                "Show item markers on BSP level previews",
                _settings.ShowBspMarkers,
                value => _settings.ShowBspMarkers = value),
        },
    };

    private StackPanel BuildFilesPage()
    {
        var associationRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(_associateButton, Dock.Right);
        associationRow.Children.Add(_associateButton);
        associationRow.Children.Add(new TextBlock
        {
            Text = "PAK/PK3 file association",
            VerticalAlignment = VerticalAlignment.Center,
        });

        var openScriptsButton = new Button
        {
            Content = "Open Scripts Folder…",
            Padding = new Thickness(14, 6),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        openScriptsButton.Click += (_, _) => DesktopIntegrationService.OpenScriptsFolder();

        return new StackPanel
        {
            Margin = new Thickness(16, 8),
            Spacing = 10,
            Children =
            {
                associationRow,
                Toggle(
                    "Enable PakScape file manager actions",
                    _settings.FileManagerActionsEnabled,
                    value =>
                    {
                        _settings.FileManagerActionsEnabled = value;
                        DesktopIntegrationService.UpdateFileManagerActions(value);
                    }),
                Note("Right-click a PAK/PK3 archive to extract it, or a folder to pack it, from your file manager's Scripts menu. PakScape will ask where to save the result."),
                openScriptsButton,
            },
        };
    }

    private StackPanel BuildTextSizeRow()
    {
        var slider = new Slider
        {
            Minimum = PakScapeSettings.MinimumTextSize,
            Maximum = PakScapeSettings.MaximumTextSize,
            TickFrequency = 1,
            IsSnapToTickEnabled = true,
            Value = _settings.TextSize,
            VerticalAlignment = VerticalAlignment.Center,
        };
        slider.ValueChanged += (_, _) =>
        {
            _settings.TextSize = slider.Value;
            _textSizeCaption.Text = TextSizeCaption(_settings.TextSize);
        };

        var track = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Children =
            {
                new TextBlock
                {
                    Text = "A",
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0),
                },
                slider,
                new TextBlock
                {
                    Text = "A",
                    FontSize = 20,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0),
                },
            },
        };
        Grid.SetColumn(track.Children[1], 1);
        Grid.SetColumn(track.Children[2], 2);

        var dots = new UniformGrid
        {
            Columns = 8,
            Rows = 1,
            Margin = new Thickness(20, 2, 20, 0),
        };
        for (var index = 0; index < 8; index++)
        {
            dots.Children.Add(new Shapes.Ellipse
            {
                Width = 3,
                Height = 3,
                Fill = FindBrush("MutedForegroundBrush", Brushes.Gray),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
        }

        return Row(
            "Text size",
            new StackPanel
            {
                Width = RowControlWidth,
                Spacing = 2,
                Children = { track, dots, _textSizeCaption },
            },
            VerticalAlignment.Top);
    }

    private static StackPanel PickerRow(
        string label,
        string[] options,
        int selectedIndex,
        Action<int> onChanged)
    {
        var picker = new ComboBox
        {
            Width = RowControlWidth,
            ItemsSource = options,
            SelectedIndex = Math.Max(0, selectedIndex),
        };
        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedIndex >= 0)
            {
                onChanged(picker.SelectedIndex);
            }
        };

        return Row(label, picker);
    }

    private static StackPanel Row(
        string label,
        Control control,
        VerticalAlignment labelAlignment = VerticalAlignment.Center) => new()
    {
        Orientation = Orientation.Horizontal,
        HorizontalAlignment = HorizontalAlignment.Center,
        Spacing = 18,
        Children =
        {
            new TextBlock
            {
                Text = label,
                Width = RowLabelWidth,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = labelAlignment,
                Margin = labelAlignment == VerticalAlignment.Top
                    ? new Thickness(0, 2, 0, 0)
                    : default,
            },
            control,
        },
    };

    private static CheckBox Toggle(string text, bool isChecked, Action<bool> onChanged)
    {
        var toggle = new CheckBox { Content = text, IsChecked = isChecked };
        toggle.IsCheckedChanged += (_, _) => onChanged(toggle.IsChecked == true);
        return toggle;
    }

    private Border Note(string text) => new()
    {
        Background = FindBrush("MenuHoverBrush", Brushes.Transparent),
        CornerRadius = new CornerRadius(14),
        Padding = new Thickness(14, 12),
        Margin = new Thickness(0, 8, 0, 0),
        Child = new TextBlock
        {
            Text = text,
            FontSize = 12,
            Opacity = 0.72,
            TextWrapping = TextWrapping.Wrap,
        },
    };

    private static string TextSizeCaption(double textSize) =>
        Math.Abs(textSize - PakScapeSettings.DefaultTextSize) < 0.01
            ? "Default"
            : $"{textSize:0} pt";

    private static string AssociateButtonText() =>
        DesktopIntegrationService.IsAssociated ? "Associated" : "Associate with PakScape";

    private IBrush FindBrush(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush
            ? brush
            : fallback;

    private static Shapes.Path Glyph(string data) => new()
    {
        Width = 26,
        Height = 26,
        Data = Geometry.Parse(data),
        Fill = Brushes.Transparent,
        StrokeThickness = 1.4,
        StrokeJoin = PenLineJoin.Round,
        StrokeLineCap = PenLineCap.Round,
        Stretch = Stretch.Uniform,
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    private static Shapes.Path SlidersIcon() =>
        Glyph("M 2,5 H 20 M 2,12 H 20 M 2,19 H 20 " +
             "M 7,3 A 2,2 0 1 1 7,7 A 2,2 0 1 1 7,3 Z " +
             "M 15,10 A 2,2 0 1 1 15,14 A 2,2 0 1 1 15,10 Z " +
             "M 9,17 A 2,2 0 1 1 9,21 A 2,2 0 1 1 9,17 Z");

    private static Shapes.Path BoxIcon() =>
        Glyph("M 3,7 L 12,3 L 21,7 L 21,17 L 12,21 L 3,17 Z M 3,7 L 12,11 L 21,7 M 12,11 L 12,21");

    private static Shapes.Path EyeIcon() =>
        Glyph("M 2,12 C 6,5 18,5 22,12 C 18,19 6,19 2,12 Z " +
             "M 10,12 A 2,2 0 1 1 14,12 A 2,2 0 1 1 10,12 Z");

    private static Shapes.Path FolderIcon() =>
        Glyph("M 1,4 L 1,14 L 19,14 L 19,5 L 9,5 L 7,2 L 1,2 Z");

    private sealed record SettingsTab(
        string Title,
        Button Button,
        Shapes.Path Icon,
        TextBlock Label,
        Control Page);
}
