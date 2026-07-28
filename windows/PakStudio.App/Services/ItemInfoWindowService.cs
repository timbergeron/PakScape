using System.Windows;
using PakStudio.App.ViewModels;
using PakStudio.App.Views;
using PakStudio.Core.Interfaces;
using PakStudio.Core.Nodes;

namespace PakStudio.App.Services;

public sealed class ItemInfoWindowService
{
    private const int MaximumWindowsPerRequest = 32;
    private const double CascadeDistance = 26;
    private readonly ArchiveThumbnailService _thumbnailService;
    private readonly IIconService _iconService;
    private readonly IMessageBoxService _messageBoxService;
    private readonly Dictionary<ArchiveNode, ItemInfoWindow> _windows =
        new(ReferenceEqualityComparer.Instance);
    private int _cascadeIndex;

    public ItemInfoWindowService(
        ArchiveThumbnailService thumbnailService,
        IIconService iconService,
        IMessageBoxService messageBoxService)
    {
        _thumbnailService = thumbnailService;
        _iconService = iconService;
        _messageBoxService = messageBoxService;
    }

    public void Show(IEnumerable<ArchiveNode> nodes, string archiveName)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var requested = nodes
            .Distinct<ArchiveNode>(ReferenceEqualityComparer.Instance)
            .ToList();
        if (requested.Count > MaximumWindowsPerRequest)
        {
            _messageBoxService.ShowError(
                "Too Many Get Info Windows",
                $"Select no more than {MaximumWindowsPerRequest:N0} items at once.");
            return;
        }

        foreach (var node in requested)
        {
            if (_windows.TryGetValue(node, out var existing))
            {
                if (existing.WindowState == WindowState.Minimized)
                {
                    existing.WindowState = WindowState.Normal;
                }
                existing.Activate();
                continue;
            }

            var viewModel = new ItemInfoViewModel(
                node,
                archiveName,
                _iconService.GetGlyphForNode(node),
                _thumbnailService);
            var window = new ItemInfoWindow(viewModel);
            if (WindowOwnership.ActiveMainWindow() is { IsVisible: true } owner)
            {
                window.Owner = owner;
            }
            Place(window);
            window.Closed += (_, _) =>
            {
                _windows.Remove(node);
                if (_windows.Count == 0)
                {
                    _cascadeIndex = 0;
                }
            };
            _windows[node] = window;
            window.Show();
        }
    }

    public void CloseAll()
    {
        foreach (var window in _windows.Values.ToList())
        {
            window.Close();
        }
        _windows.Clear();
        _cascadeIndex = 0;
    }

    public void CloseMissingFrom(ArchiveFolderNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var live = new HashSet<ArchiveNode>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<ArchiveNode>();
        pending.Push(root);
        while (pending.TryPop(out var node))
        {
            live.Add(node);
            if (node is ArchiveFolderNode folder)
            {
                foreach (var child in folder.Children)
                {
                    pending.Push(child);
                }
            }
        }

        foreach (var (node, window) in _windows.ToList())
        {
            if (!live.Contains(node))
            {
                window.Close();
            }
        }
    }

    private void Place(Window window)
    {
        var workArea = SystemParameters.WorkArea;
        var owner = WindowOwnership.ActiveMainWindow();
        var offset = (_cascadeIndex++ % 10) * CascadeDistance;
        var preferredLeft = owner is { IsVisible: true }
            ? owner.Left + owner.ActualWidth + 12
            : workArea.Left + 40;
        if (preferredLeft + window.Width > workArea.Right)
        {
            preferredLeft = owner is { IsVisible: true }
                ? owner.Left + 50
                : workArea.Left + 40;
        }

        var preferredTop = owner is { IsVisible: true }
            ? owner.Top + 36
            : workArea.Top + 40;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = Math.Clamp(
            preferredLeft + offset,
            workArea.Left,
            Math.Max(workArea.Left, workArea.Right - window.Width));
        window.Top = Math.Clamp(
            preferredTop + offset,
            workArea.Top,
            Math.Max(workArea.Top, workArea.Bottom - window.Height));
    }
}
