using System.Runtime.InteropServices;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using PakScape.Linux.Controls;
using PakStudio.Core.Audio;
using PakStudio.Core.Nodes;
using PakStudio.Core.Preview;

namespace PakScape.Linux.Views;

public sealed class PreviewWindow : Window, IDisposable
{
    private readonly IReadOnlyList<ArchiveNode> _nodes;
    private readonly DispatcherTimer _audioProgressTimer;
    private readonly TextBlock _titleText;
    private readonly TextBlock _subtitleText;
    private readonly Border _imagePanel;
    private readonly Image _imagePreview;
    private readonly Border _bspMarkerOptionsPanel;
    private readonly CheckBox _showBspArmors;
    private readonly CheckBox _showBspMegaHealth;
    private readonly CheckBox _showBspPowerups;
    private readonly CheckBox _showBspMajorWeapons;
    private readonly CheckBox _showBspFlags;
    private readonly TextBox _textPreview;
    private readonly Grid _audioPanel;
    private readonly Button _audioPlayPauseButton;
    private readonly Slider _audioProgressSlider;
    private readonly TextBlock _audioTimeText;
    private readonly TextBlock _audioStatusText;
    private readonly Grid _metadataPanel;
    private readonly Grid _modelPanel;
    private readonly TextBlock _metadataTypeText;
    private readonly TextBlock _metadataSizeText;
    private readonly TextBlock _metadataMessageText;
    private readonly TextBlock _positionText;
    private readonly Button _previousButton;
    private readonly Button _nextButton;
    private NativeAudioPlayer? _audioPlayer;
    private ModelPreviewControl? _modelPreview;
    private CancellationTokenSource? _previewCancellationSource;
    private ArchivePreview? _activePreview;
    private Bitmap? _currentImage;
    private int _index;
    private int _previewGeneration;
    private bool _isAudioPlaying;
    private bool _isUpdatingAudioProgress;
    private bool _isClosed;
    private bool _isDisposed;

    public PreviewWindow(IReadOnlyList<ArchiveNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArchivePreviewBuilder.ValidateSelection(nodes);
        if (nodes.Count == 0)
        {
            throw new ArchivePreviewException("Select an item to preview.");
        }

        _nodes = nodes;
        _audioProgressTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _audioProgressTimer.Tick += OnAudioProgressTick;
        Title = "Quick Preview";
        Width = 860;
        Height = 640;
        MinWidth = 520;
        MinHeight = 360;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _titleText = new TextBlock
        {
            FontSize = 19,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _subtitleText = new TextBlock
        {
            Margin = new Thickness(0, 3, 0, 0),
            Opacity = 0.68,
        };

        var closeButton = new Button
        {
            Content = "✕",
            Width = 34,
            Height = 30,
            Margin = new Thickness(12, 0, 0, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        closeButton.Classes.Add("preview-icon-button");
        ToolTip.SetTip(closeButton, "Close preview (Space or Escape)");
        closeButton.Click += (_, _) => Close();

        var header = new Grid
        {
            Margin = new Thickness(4, 0, 4, 12),
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
            },
        };
        header.Children.Add(new StackPanel
        {
            Children = { _titleText, _subtitleText },
        });
        Grid.SetColumn(closeButton, 1);
        header.Children.Add(closeButton);

        _imagePreview = new Image
        {
            Margin = new Thickness(16),
            Stretch = Stretch.Uniform,
        };
        _showBspArmors = CreateBspMarkerCheckBox("Armor");
        _showBspMegaHealth = CreateBspMarkerCheckBox("Megahealth");
        _showBspPowerups = CreateBspMarkerCheckBox("Powerups");
        _showBspMajorWeapons = CreateBspMarkerCheckBox("Weapons");
        _showBspFlags = CreateBspMarkerCheckBox("Flags", isLast: true);
        _bspMarkerOptionsPanel = new Border
        {
            Margin = new Thickness(12),
            Padding = new Thickness(10, 7),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = new SolidColorBrush(Color.Parse("#D9202329")),
            BorderBrush = new SolidColorBrush(Color.Parse("#664C515B")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            IsVisible = false,
            Child = new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Show:",
                        Margin = new Thickness(0, 0, 12, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = Brushes.White,
                    },
                    _showBspArmors,
                    _showBspMegaHealth,
                    _showBspPowerups,
                    _showBspMajorWeapons,
                    _showBspFlags,
                },
            },
        };
        _imagePanel = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#181B20")),
            Child = new Grid
            {
                Children = { _imagePreview, _bspMarkerOptionsPanel },
            },
            IsVisible = false,
        };

        _textPreview = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            AcceptsTab = true,
            FontFamily = new FontFamily("monospace"),
            FontSize = 13,
            TextWrapping = TextWrapping.NoWrap,
            Padding = new Thickness(14),
            IsVisible = false,
        };
        _textPreview.SetValue(
            ScrollViewer.HorizontalScrollBarVisibilityProperty,
            Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        _textPreview.SetValue(
            ScrollViewer.VerticalScrollBarVisibilityProperty,
            Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);

        _audioPlayPauseButton = new Button
        {
            Content = "▶",
            Width = 36,
            Height = 36,
            Padding = new Thickness(0),
            IsEnabled = false,
            FontSize = 16,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        _audioPlayPauseButton.Classes.Add("preview-icon-button");
        _audioPlayPauseButton.Click += OnAudioPlayPauseClick;
        _audioProgressSlider = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            Value = 0,
            Margin = new Thickness(12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = false,
        };
        _audioProgressSlider.Classes.Add("audio-progress");
        _audioProgressSlider.ValueChanged += OnAudioProgressValueChanged;
        _audioTimeText = new TextBlock
        {
            Text = "0:00 / --:--",
            MinWidth = 76,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
            Opacity = 0.68,
        };
        _audioStatusText = new TextBlock
        {
            Text = "Loading audio…",
            Margin = new Thickness(0, 18, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Opacity = 0.68,
        };
        var audioControls = new Grid
        {
            Width = 400,
            MaxWidth = 400,
            Margin = new Thickness(0, 16, 0, 0),
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
            },
            Children =
            {
                _audioPlayPauseButton,
                _audioProgressSlider,
                _audioTimeText,
            },
        };
        Grid.SetColumn(_audioProgressSlider, 1);
        Grid.SetColumn(_audioTimeText, 2);
        var audioArtworkIcon = new Avalonia.Controls.Shapes.Path
        {
            Width = 48,
            Height = 48,
            Data = Geometry.Parse("M 2,8 L 6,8 L 11,3 L 11,17 L 6,12 L 2,12 Z M 14,7 C 16,9 16,11 14,13 M 17,4 C 21,8 21,12 17,16"),
            Fill = Brushes.Transparent,
            StrokeThickness = 1.5,
            Stretch = Stretch.Uniform,
        };
        audioArtworkIcon.Classes.Add("preview-muted-icon");
        var audioArtwork = new Border
        {
            Width = 96,
            Height = 96,
            HorizontalAlignment = HorizontalAlignment.Center,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(48),
            Child = audioArtworkIcon,
        };
        audioArtwork.Classes.Add("audio-artwork");
        _audioPanel = new Grid
        {
            Margin = new Thickness(30),
            IsVisible = false,
            Children =
            {
                new StackPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        audioArtwork,
                        _audioStatusText,
                        audioControls,
                    },
                },
            },
        };

        _metadataTypeText = new TextBlock
        {
            Margin = new Thickness(0, 18, 0, 0),
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _metadataSizeText = new TextBlock
        {
            Margin = new Thickness(0, 6, 0, 0),
            Opacity = 0.68,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _metadataMessageText = new TextBlock
        {
            Margin = new Thickness(0, 18, 0, 0),
            MaxWidth = 520,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.68,
        };
        _metadataPanel = new Grid
        {
            Margin = new Thickness(30),
            IsVisible = false,
            Children =
            {
                new StackPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "📄",
                            FontSize = 72,
                            HorizontalAlignment = HorizontalAlignment.Center,
                        },
                        _metadataTypeText,
                        _metadataSizeText,
                        _metadataMessageText,
                    },
                },
            },
        };

        _modelPanel = new Grid
        {
            IsVisible = false,
        };

        var contentGrid = new Grid
        {
            Children = { _imagePanel, _modelPanel, _textPreview, _audioPanel, _metadataPanel },
        };
        var contentBorder = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            ClipToBounds = true,
            Child = contentGrid,
        };
        contentBorder.Classes.Add("preview-surface");

        _previousButton = new Button
        {
            Content = "←",
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        _previousButton.Classes.Add("preview-icon-button");
        ToolTip.SetTip(_previousButton, "Previous item");
        _previousButton.Click += (_, _) => Move(-1);
        _nextButton = new Button
        {
            Content = "→",
            Margin = new Thickness(4, 0, 0, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        _nextButton.Classes.Add("preview-icon-button");
        ToolTip.SetTip(_nextButton, "Next item");
        _nextButton.Click += (_, _) => Move(1);
        _positionText = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.68,
        };

        var navigation = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { _previousButton, _nextButton },
        };
        var closeHint = new TextBlock
        {
            Text = "Space or Esc to close",
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.68,
        };
        var footer = new Grid
        {
            Margin = new Thickness(4, 12, 4, 0),
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
            },
            Children = { navigation, _positionText, closeHint },
        };
        Grid.SetColumn(_positionText, 1);
        Grid.SetColumn(closeHint, 2);

        var root = new Grid
        {
            Margin = new Thickness(16),
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star),
                new RowDefinition(GridLength.Auto),
            },
            Children = { header, contentBorder, footer },
        };
        Grid.SetRow(contentBorder, 1);
        Grid.SetRow(footer, 2);
        Content = root;

        AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Closed += OnClosed;
        _ = ShowCurrentPreviewAsync();
    }

    private async Task ShowCurrentPreviewAsync()
    {
        _previewCancellationSource?.Cancel();
        _previewCancellationSource?.Dispose();
        _previewCancellationSource = new CancellationTokenSource();
        var cancellationToken = _previewCancellationSource.Token;
        var generation = ++_previewGeneration;
        ResetContent();
        ArchivePreview preview;
        try
        {
            preview = ArchivePreviewBuilder.Build(
                _nodes[_index],
                bspOptions: CurrentBspOptions());
        }
        catch (Exception exception)
        {
            preview = new ArchivePreview(
                _nodes[_index].Name,
                "Preview unavailable",
                0,
                ArchivePreviewKind.Metadata,
                Message: exception.Message);
        }
        _activePreview = preview;

        Title = $"{preview.Title} — Quick Preview";
        _titleText.Text = preview.Title;
        _subtitleText.Text = $"{preview.TypeDescription} • {ArchivePreviewBuilder.FormatSize(preview.Size)}";
        var metadata = ArchiveMetadataInspector.Inspect(_nodes[_index]);
        if (!string.IsNullOrWhiteSpace(metadata.Summary))
        {
            _subtitleText.Text += $" • {metadata.Summary}";
        }
        _positionText.Text = _nodes.Count == 1 ? "1 of 1" : $"{_index + 1:N0} of {_nodes.Count:N0}";
        _previousButton.IsEnabled = _nodes.Count > 1;
        _nextButton.IsEnabled = _nodes.Count > 1;

        switch (preview.Kind)
        {
            case ArchivePreviewKind.Text:
                _textPreview.Text = preview.Text ?? string.Empty;
                _textPreview.IsVisible = true;
                if (!string.IsNullOrWhiteSpace(preview.Message))
                {
                    _subtitleText.Text += $" • {preview.Message}";
                }
                break;
            case ArchivePreviewKind.Audio:
                await ShowAudioAsync(preview, generation, cancellationToken);
                break;
            case ArchivePreviewKind.EncodedImage:
                if (TryLoadEncodedImage(
                        preview.EncodedImage,
                        preview.ImageWidth,
                        preview.ImageHeight,
                        out var encodedImage))
                {
                    SetImage(encodedImage);
                }
                else
                {
                    ShowMetadata(preview with { Message = "The native image decoder could not read this file." });
                }
                break;
            case ArchivePreviewKind.Bitmap when preview.Bitmap is { } bitmap:
                SetImage(CreateBitmap(bitmap));
                _bspMarkerOptionsPanel.IsVisible = IsBspLevelPreview(preview);
                break;
            case ArchivePreviewKind.Model when preview.Model is { } model:
                ShowModel(preview, model);
                break;
            default:
                ShowMetadata(preview);
                break;
        }
    }

    private async Task ShowAudioAsync(
        ArchivePreview preview,
        int generation,
        CancellationToken cancellationToken)
    {
        NativeAudioPlayer? player = null;
        try
        {
            var audioData = preview.EncodedAudio
                ?? throw new InvalidOperationException("The audio preview contains no playable data.");
            _audioPanel.IsVisible = true;
            _audioStatusText.Text = "Loading audio…";
            player = await Task.Run(
                () => NativeAudioPlayer.Create(audioData, Path.GetExtension(preview.Title)),
                cancellationToken);
            if (_isClosed || generation != _previewGeneration)
            {
                player.Dispose();
                return;
            }

            _audioPlayer = player;
            _audioProgressSlider.Maximum = Math.Max(player.DurationSeconds, 1);
            _audioProgressSlider.IsEnabled = true;
            _audioPlayPauseButton.IsEnabled = true;
            _audioStatusText.Text = $"{FormatAudioTime(TimeSpan.FromSeconds(player.DurationSeconds))} audio";
            UpdateAudioProgress(0, player);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            player?.Dispose();
            if (!_isClosed && generation == _previewGeneration)
            {
                _audioPanel.IsVisible = false;
                ShowMetadata(preview with
                {
                    Message = $"Audio preview unavailable: {exception.Message}",
                });
            }
        }
    }

    private void ShowModel(ArchivePreview preview, PreviewModel model)
    {
        try
        {
            var control = ModelPreviewControl.Create(model, preview.Title);
            _modelPreview = control;
            _modelPanel.Children.Add(control);
            _modelPanel.IsVisible = true;
            _subtitleText.Text += $" • {control.StatusLine}";
            control.Focus();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ResetModelPreview();

            /* Fall back to whatever flat preview the file can still produce. */
            var fallback = ArchivePreviewBuilder.Build(_nodes[_index], includeInteractiveModels: false);
            if (fallback.Kind == ArchivePreviewKind.Bitmap && fallback.Bitmap is { } bitmap)
            {
                SetImage(CreateBitmap(bitmap));
                _subtitleText.Text += $" • {exception.Message}";
                return;
            }

            ShowMetadata(preview with { Message = exception.Message });
        }
    }

    private void ResetModelPreview()
    {
        var control = _modelPreview;
        _modelPreview = null;
        _modelPanel.Children.Clear();
        _modelPanel.IsVisible = false;
        control?.Dispose();
    }

    private void ShowMetadata(ArchivePreview preview)
    {
        var metadata = ArchiveMetadataInspector.Inspect(_nodes[_index]);
        _metadataTypeText.Text = preview.TypeDescription;
        _metadataSizeText.Text = ArchivePreviewBuilder.FormatSize(preview.Size);
        var message = preview.Message == "No rich preview is available for this file type."
            ? null
            : preview.Message;
        _metadataMessageText.Text = string.Join(
            Environment.NewLine + Environment.NewLine,
            new[] { metadata.DisplayText, message }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        _metadataPanel.IsVisible = true;
    }

    private void ResetContent()
    {
        ResetAudioPlayback();
        ResetModelPreview();
        DisposeCurrentImage();
        _textPreview.Text = string.Empty;
        _imagePanel.IsVisible = false;
        _bspMarkerOptionsPanel.IsVisible = false;
        _textPreview.IsVisible = false;
        _audioPanel.IsVisible = false;
        _metadataPanel.IsVisible = false;
    }

    private void ResetAudioPlayback()
    {
        _audioProgressTimer.Stop();
        _isAudioPlaying = false;

        var player = _audioPlayer;
        _audioPlayer = null;
        player?.Dispose();

        _audioPlayPauseButton.IsEnabled = false;
        _audioProgressSlider.IsEnabled = false;
        _isUpdatingAudioProgress = true;
        _audioProgressSlider.Maximum = 1;
        _audioProgressSlider.Value = 0;
        _isUpdatingAudioProgress = false;
        _audioTimeText.Text = "0:00 / --:--";
        _audioStatusText.Text = "Loading audio…";
        UpdateAudioPlayPauseButton();
    }

    private void UpdateAudioProgress(double seconds, NativeAudioPlayer player)
    {
        if (!ReferenceEquals(player, _audioPlayer))
        {
            return;
        }

        var clampedSeconds = Math.Clamp(seconds, 0, player.DurationSeconds);
        _isUpdatingAudioProgress = true;
        _audioProgressSlider.Value = clampedSeconds;
        _isUpdatingAudioProgress = false;
        _audioTimeText.Text =
            $"{FormatAudioTime(TimeSpan.FromSeconds(clampedSeconds))} / " +
            FormatAudioTime(TimeSpan.FromSeconds(player.DurationSeconds));
    }

    private void UpdateAudioPlayPauseButton()
    {
        _audioPlayPauseButton.Content = _isAudioPlaying ? "Ⅱ" : "▶";
        ToolTip.SetTip(
            _audioPlayPauseButton,
            _isAudioPlaying ? "Pause audio" : "Play audio");
    }

    private static string FormatAudioTime(TimeSpan value) =>
        value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : value.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    private void SetImage(Bitmap bitmap)
    {
        _currentImage = bitmap;
        _imagePreview.Source = bitmap;
        _imagePanel.IsVisible = true;
    }

    private CheckBox CreateBspMarkerCheckBox(string label, bool isLast = false)
    {
        var checkBox = new CheckBox
        {
            Content = label,
            Margin = isLast ? default : new Thickness(0, 0, 12, 0),
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
        };
        checkBox.IsCheckedChanged += OnBspMarkerOptionChanged;
        return checkBox;
    }

    private BspLevelPreviewOptions CurrentBspOptions() => new(
        _showBspArmors.IsChecked == true,
        _showBspMegaHealth.IsChecked == true,
        _showBspPowerups.IsChecked == true,
        _showBspMajorWeapons.IsChecked == true,
        _showBspFlags.IsChecked == true);

    private bool IsBspLevelPreview(ArchivePreview preview) =>
        preview.Kind == ArchivePreviewKind.Bitmap &&
        _nodes[_index] is ArchiveFileNode file &&
        file.Extension.Equals(".bsp", StringComparison.OrdinalIgnoreCase);

    private void OnBspMarkerOptionChanged(object? sender, RoutedEventArgs e)
    {
        if (_bspMarkerOptionsPanel.IsVisible)
        {
            _ = ShowCurrentPreviewAsync();
        }
    }

    private void DisposeCurrentImage()
    {
        _imagePreview.Source = null;
        _currentImage?.Dispose();
        _currentImage = null;
    }

    private static bool TryLoadEncodedImage(byte[]? data, int width, int height, out Bitmap bitmap)
    {
        bitmap = null!;
        if (data is null)
        {
            return false;
        }
        try
        {
            using var stream = new MemoryStream(data, writable: false);
            if (width >= height)
            {
                bitmap = Bitmap.DecodeToWidth(
                    stream,
                    Math.Min(width, EncodedImageInspector.MaximumRenderedDimension),
                    BitmapInterpolationMode.HighQuality);
            }
            else
            {
                bitmap = Bitmap.DecodeToHeight(
                    stream,
                    Math.Min(height, EncodedImageInspector.MaximumRenderedDimension),
                    BitmapInterpolationMode.HighQuality);
            }
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }

    private static WriteableBitmap CreateBitmap(PreviewBitmap preview)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(preview.Width, preview.Height),
            new Vector(96, 96),
            PixelFormats.Bgra8888,
            AlphaFormat.Unpremul);
        using var framebuffer = bitmap.Lock();
        for (var row = 0; row < preview.Height; row++)
        {
            Marshal.Copy(
                preview.BgraPixels,
                row * preview.Stride,
                IntPtr.Add(framebuffer.Address, row * framebuffer.RowBytes),
                preview.Stride);
        }
        return bitmap;
    }

    private void Move(int delta)
    {
        _index = (_index + delta + _nodes.Count) % _nodes.Count;
        _ = ShowCurrentPreviewAsync();
    }

    private void OnAudioPlayPauseClick(object? sender, RoutedEventArgs e)
    {
        var player = _audioPlayer;
        if (player is null)
        {
            return;
        }

        try
        {
            var play = !_isAudioPlaying;
            if (play && _audioProgressSlider.Value >= player.DurationSeconds - 0.05)
            {
                player.Seek(0);
                UpdateAudioProgress(0, player);
            }

            if (play)
            {
                player.Play();
            }
            else
            {
                player.Pause();
            }
            if (!ReferenceEquals(player, _audioPlayer))
            {
                return;
            }

            _isAudioPlaying = play;
            _audioStatusText.Text = play ? "Playing" : "Paused";
            if (play)
            {
                _audioProgressTimer.Start();
            }
            else
            {
                _audioProgressTimer.Stop();
            }
            UpdateAudioPlayPauseButton();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ShowAudioFailure(player, exception);
        }
    }

    private void OnAudioProgressValueChanged(
        object? sender,
        RangeBaseValueChangedEventArgs e)
    {
        var player = _audioPlayer;
        if (_isUpdatingAudioProgress || player is null)
        {
            return;
        }

        try
        {
            player.Seek(e.NewValue);
            UpdateAudioProgress(e.NewValue, player);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ShowAudioFailure(player, exception);
        }
    }

    private void OnAudioProgressTick(object? sender, EventArgs e)
    {
        var player = _audioPlayer;
        if (!_isAudioPlaying || player is null)
        {
            return;
        }

        try
        {
            var seconds = player.PositionSeconds;
            if (!ReferenceEquals(player, _audioPlayer))
            {
                return;
            }

            UpdateAudioProgress(seconds, player);
            if (player.IsFinished || seconds >= player.DurationSeconds - 0.05)
            {
                _audioProgressTimer.Stop();
                _isAudioPlaying = false;
                _audioStatusText.Text = "Ready";
                UpdateAudioPlayPauseButton();
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ShowAudioFailure(player, exception);
        }
    }

    private void ShowAudioFailure(NativeAudioPlayer player, Exception exception)
    {
        if (!ReferenceEquals(player, _audioPlayer) || _activePreview is not { } preview)
        {
            return;
        }

        ResetAudioPlayback();
        _audioPanel.IsVisible = false;
        ShowMetadata(preview with
        {
            Message = $"Audio preview unavailable: {exception.Message}",
        });
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Dispose();
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isClosed = true;
        _previewCancellationSource?.Cancel();
        _previewCancellationSource?.Dispose();
        _previewCancellationSource = null;
        _previewGeneration++;
        ResetAudioPlayback();
        ResetModelPreview();
        DisposeCurrentImage();
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        /* The model viewer steers with the arrow keys, so it gets first refusal. */
        if (e.Key is not (Key.Space or Key.Escape) &&
            _modelPreview?.HandleKey(e.Key) == true)
        {
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Space:
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
            case Key.Left when _nodes.Count > 1:
            case Key.Up when _nodes.Count > 1:
                Move(-1);
                e.Handled = true;
                break;
            case Key.Right when _nodes.Count > 1:
            case Key.Down when _nodes.Count > 1:
                Move(1);
                e.Handled = true;
                break;
        }
    }
}
