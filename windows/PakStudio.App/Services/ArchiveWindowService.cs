using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using PakStudio.App.Views;

namespace PakStudio.App.Services;

public sealed class ArchiveWindowService : IArchiveWindowService, IDisposable
{
    private const double CascadeDistance = 28;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Dictionary<MainWindow, IServiceScope> _windowScopes = [];
    private bool _disposed;

    public ArchiveWindowService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public MainWindow ShowNewArchive(string formatId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(formatId);
        return CreateWindow(archivePath: null, formatId: formatId);
    }

    public MainWindow ShowBlankWorkspace() =>
        CreateWindow(archivePath: null, formatId: "pak", initializeDocument: false);

    public MainWindow ShowArchive(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var existing = _windowScopes.Keys.FirstOrDefault(window =>
            string.Equals(window.ArchivePath, fullPath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }
            existing.Activate();
            return existing;
        }

        return CreateWindow(fullPath, formatId: null);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        foreach (var (window, scope) in _windowScopes.ToList())
        {
            window.Closed -= Window_OnClosed;
            scope.Dispose();
        }
        _windowScopes.Clear();
    }

    private MainWindow CreateWindow(
        string? archivePath,
        string? formatId,
        bool initializeDocument = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var activeWindow = WindowOwnership.ActiveMainWindow();
        var scope = _scopeFactory.CreateScope();
        MainWindow window;
        try
        {
            window = scope.ServiceProvider.GetRequiredService<MainWindow>();
            window.ConfigureStartupArchive(archivePath, formatId ?? "pak", initializeDocument);
            Place(window, activeWindow);
            _windowScopes.Add(window, scope);
            window.Closed += Window_OnClosed;
            try
            {
                window.Show();
            }
            catch
            {
                window.Closed -= Window_OnClosed;
                _windowScopes.Remove(window);
                throw;
            }
        }
        catch
        {
            scope.Dispose();
            throw;
        }

        return window;
    }

    private void Window_OnClosed(object? sender, EventArgs e)
    {
        if (sender is not MainWindow window ||
            !_windowScopes.Remove(window, out var scope))
        {
            return;
        }

        window.Closed -= Window_OnClosed;
        scope.Dispose();

        if (ReferenceEquals(Application.Current.MainWindow, window) &&
            _windowScopes.Keys.FirstOrDefault(candidate => candidate.IsVisible) is { } replacement)
        {
            Application.Current.MainWindow = replacement;
        }
    }

    private static void Place(MainWindow window, MainWindow? activeWindow)
    {
        if (activeWindow is null)
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        var workArea = SystemParameters.WorkArea;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = Math.Clamp(
            activeWindow.Left + CascadeDistance,
            workArea.Left,
            Math.Max(workArea.Left, workArea.Right - window.Width));
        window.Top = Math.Clamp(
            activeWindow.Top + CascadeDistance,
            workArea.Top,
            Math.Max(workArea.Top, workArea.Bottom - window.Height));
    }
}
