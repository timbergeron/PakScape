using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using PakScape.Linux.Services;
using PakScape.Linux.ViewModels;
using PakScape.Linux.Views;
using PakStudio.Core.Interfaces;
using PakStudio.Formats.Pak;
using PakStudio.Formats.Pk3;

namespace PakScape.Linux;

public partial class App : Application, IDisposable
{
    private readonly List<WindowSession> _sessions = [];
    private ArchiveService? _archiveService;
    private XdgRecentFilesService? _recentFilesService;
    private IClassicDesktopStyleApplicationLifetime? _desktop;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var handlers = new IArchiveFormatHandler[]
            {
                new PakFormatHandler(),
                new Pk3FormatHandler(),
                new KpfFormatHandler(),
            };
            _desktop = desktop;
            desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
            _archiveService = new ArchiveService(new ArchiveFormatRegistry(handlers));
            _recentFilesService = new XdgRecentFilesService();
            var startupPath = desktop.Args?
                .FirstOrDefault(argument => argument.Length == 0 || argument[0] != '-');
            OpenArchiveWindow(startupPath);
            desktop.Exit += (_, _) => Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }

    public void OpenArchiveWindow(string? archivePath, string formatId = "pak")
    {
        if (!string.IsNullOrWhiteSpace(archivePath))
        {
            var fullPath = Path.GetFullPath(archivePath);
            var existing = _sessions.FirstOrDefault(session =>
                session.ViewModel.Document?.FilePath is { } openPath &&
                string.Equals(Path.GetFullPath(openPath), fullPath, StringComparison.Ordinal));
            if (existing is not null)
            {
                existing.Window.Activate();
                return;
            }
        }

        var archiveService = _archiveService
            ?? throw new InvalidOperationException("Archive services are not initialized.");
        var recentFilesService = _recentFilesService
            ?? throw new InvalidOperationException("Recent-file services are not initialized.");
        var transferService = new LinuxArchiveFileTransferService();
        var thumbnailService = new ArchiveThumbnailService();
        var window = new MainWindow();
        var interactionService = new AvaloniaUserInteractionService(() => window);
        var viewModel = new MainWindowViewModel(
            archiveService,
            transferService,
            interactionService,
            recentFilesService,
            thumbnailService);
        var session = new WindowSession(window, viewModel, transferService, thumbnailService);
        _sessions.Add(session);
        window.Closed += (_, _) =>
        {
            _sessions.Remove(session);
            session.Dispose();
        };
        window.Configure(viewModel, transferService, archivePath, formatId);
        var isFirstWindow = _desktop?.MainWindow is null;
        if (isFirstWindow)
        {
            _desktop!.MainWindow = window;
        }
        else
        {
            window.Show();
        }
    }

    public void Dispose()
    {
        foreach (var session in _sessions.ToList())
        {
            session.Dispose();
        }
        _sessions.Clear();
        GC.SuppressFinalize(this);
    }

    private sealed record WindowSession(
        MainWindow Window,
        MainWindowViewModel ViewModel,
        LinuxArchiveFileTransferService TransferService,
        ArchiveThumbnailService ThumbnailService) : IDisposable
    {
        public void Dispose()
        {
            TransferService.Dispose();
            ThumbnailService.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
