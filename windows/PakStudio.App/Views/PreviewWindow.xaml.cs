using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using PakStudio.App.Services;
using PakStudio.Core.Audio;
using PakStudio.Core.Nodes;
using PakStudio.Core.Preview;

namespace PakStudio.App.Views;

public partial class PreviewWindow : Window
{
    private readonly IReadOnlyList<ArchiveNode> _nodes;
    private readonly DispatcherTimer _audioProgressTimer;
    private NativeAudioPlayer? _audioPlayer;
    private ArchivePreview? _activePreview;
    private int _index;
    private bool _isAudioPlaying;
    private bool _isUpdatingAudioProgress;

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
        InitializeComponent();
        _audioProgressTimer.Tick += (_, _) => UpdateAudioProgress();
        ShowCurrentPreview();
    }

    private void ShowCurrentPreview()
    {
        ResetContent();
        ArchivePreview preview;
        try
        {
            preview = ArchivePreviewBuilder.Build(_nodes[_index]);
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

        Title = $"{preview.Title} — Quick Preview";
        _activePreview = preview;
        TitleText.Text = preview.Title;
        SubtitleText.Text = $"{preview.TypeDescription} • {ArchivePreviewBuilder.FormatSize(preview.Size)}";
        PositionText.Text = _nodes.Count == 1 ? "1 of 1" : $"{_index + 1:N0} of {_nodes.Count:N0}";
        PreviousButton.IsEnabled = _nodes.Count > 1;
        NextButton.IsEnabled = _nodes.Count > 1;

        switch (preview.Kind)
        {
            case ArchivePreviewKind.Text:
                TextPreview.Text = preview.Text ?? string.Empty;
                TextPreview.Visibility = Visibility.Visible;
                if (!string.IsNullOrWhiteSpace(preview.Message))
                {
                    SubtitleText.Text += $" • {preview.Message}";
                }
                break;
            case ArchivePreviewKind.Audio:
                ShowAudio(preview);
                break;
            case ArchivePreviewKind.EncodedImage:
            case ArchivePreviewKind.Bitmap:
                if (PreviewImageFactory.TryCreate(
                        preview,
                        EncodedImageInspector.MaximumRenderedDimension,
                        out var previewImage))
                {
                    ImagePreview.Source = previewImage;
                    ImagePanel.Visibility = Visibility.Visible;
                }
                else
                {
                    ShowMetadata(preview with { Message = "The native image decoder could not read this file." });
                }
                break;
            default:
                ShowMetadata(preview);
                break;
        }
    }

    private void ShowMetadata(ArchivePreview preview)
    {
        MetadataTypeText.Text = preview.TypeDescription;
        MetadataSizeText.Text = ArchivePreviewBuilder.FormatSize(preview.Size);
        MetadataMessageText.Text = preview.Message ?? string.Empty;
        MetadataPanel.Visibility = Visibility.Visible;
    }

    private void ShowAudio(ArchivePreview preview)
    {
        try
        {
            var audioData = preview.EncodedAudio
                ?? throw new InvalidOperationException("The audio preview contains no playable data.");
            var player = NativeAudioPlayer.Create(audioData, Path.GetExtension(preview.Title));
            _audioPlayer = player;

            AudioPanel.Visibility = Visibility.Visible;
            AudioPlayPauseButton.IsEnabled = true;
            AudioProgressSlider.Maximum = Math.Max(player.DurationSeconds, 1);
            AudioProgressSlider.IsEnabled = true;
            AudioStatusText.Text = $"{FormatAudioTime(TimeSpan.FromSeconds(player.DurationSeconds))} audio";
            UpdateAudioProgress();
        }
        catch (Exception exception)
        {
            ResetAudioPlayback();
            ShowMetadata(preview with { Message = $"Audio preview unavailable: {exception.Message}" });
        }
    }

    private void ResetContent()
    {
        ResetAudioPlayback();
        ImagePreview.Source = null;
        TextPreview.Text = string.Empty;
        ImagePanel.Visibility = Visibility.Collapsed;
        TextPreview.Visibility = Visibility.Collapsed;
        MetadataPanel.Visibility = Visibility.Collapsed;
    }

    private void ResetAudioPlayback()
    {
        _audioProgressTimer.Stop();
        _isAudioPlaying = false;

        var player = _audioPlayer;
        _audioPlayer = null;
        player?.Dispose();

        AudioPanel.Visibility = Visibility.Collapsed;
        AudioPlayPauseButton.IsEnabled = false;
        AudioProgressSlider.IsEnabled = false;
        _isUpdatingAudioProgress = true;
        AudioProgressSlider.Maximum = 1;
        AudioProgressSlider.Value = 0;
        _isUpdatingAudioProgress = false;
        AudioTimeText.Text = "0:00 / --:--";
        AudioStatusText.Text = "Loading audio...";
        UpdateAudioPlayPauseButton();
    }

    private void UpdateAudioProgress()
    {
        var player = _audioPlayer;
        if (player is null)
        {
            return;
        }

        try
        {
            var durationSeconds = player.DurationSeconds;
            var seconds = Math.Clamp(player.PositionSeconds, 0, durationSeconds);
            _isUpdatingAudioProgress = true;
            AudioProgressSlider.Value = seconds;
            _isUpdatingAudioProgress = false;
            AudioTimeText.Text =
                $"{FormatAudioTime(TimeSpan.FromSeconds(seconds))} / " +
                FormatAudioTime(TimeSpan.FromSeconds(durationSeconds));

            if (_isAudioPlaying && player.IsFinished)
            {
                _audioProgressTimer.Stop();
                _isAudioPlaying = false;
                AudioStatusText.Text = "Ready";
                UpdateAudioPlayPauseButton();
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ShowAudioFailure(player, exception);
        }
    }

    private void UpdateAudioPlayPauseButton()
    {
        AudioPlayPauseGlyph.Text = _isAudioPlaying ? "\uE769" : "\uE768";
        AudioPlayPauseButton.ToolTip = _isAudioPlaying ? "Pause audio" : "Play audio";
    }

    private static string FormatAudioTime(TimeSpan value) =>
        value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"m\:ss");

    private void Move(int delta)
    {
        _index = (_index + delta + _nodes.Count) % _nodes.Count;
        ShowCurrentPreview();
    }

    private void Window_OnKeyDown(object sender, KeyEventArgs e)
    {
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

    private void PreviousButton_OnClick(object sender, RoutedEventArgs e) => Move(-1);

    private void NextButton_OnClick(object sender, RoutedEventArgs e) => Move(1);

    private void AudioPlayPauseButton_OnClick(object sender, RoutedEventArgs e)
    {
        var player = _audioPlayer;
        if (player is null)
        {
            return;
        }

        try
        {
            if (_isAudioPlaying)
            {
                player.Pause();
                _audioProgressTimer.Stop();
                AudioStatusText.Text = "Paused";
            }
            else
            {
                if (player.IsFinished || player.PositionSeconds >= player.DurationSeconds)
                {
                    player.Seek(0);
                }
                player.Play();
                _audioProgressTimer.Start();
                AudioStatusText.Text = "Playing";
            }

            _isAudioPlaying = !_isAudioPlaying;
            UpdateAudioPlayPauseButton();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ShowAudioFailure(player, exception);
        }
    }

    private void AudioProgressSlider_OnValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        var player = _audioPlayer;
        if (_isUpdatingAudioProgress || player is null)
        {
            return;
        }

        try
        {
            var seconds = Math.Clamp(e.NewValue, 0, player.DurationSeconds);
            player.Seek(seconds);
            AudioTimeText.Text =
                $"{FormatAudioTime(TimeSpan.FromSeconds(seconds))} / " +
                FormatAudioTime(TimeSpan.FromSeconds(player.DurationSeconds));
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
        ShowMetadata(preview with
        {
            Message = $"Audio preview unavailable: {exception.Message}",
        });
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        ResetAudioPlayback();
        base.OnClosed(e);
    }
}
