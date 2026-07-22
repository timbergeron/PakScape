using Avalonia;
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
    private LinuxArchiveFileTransferService? _fileTransferService;
    private ArchiveThumbnailService? _thumbnailService;

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
            };
            var archiveService = new ArchiveService(new ArchiveFormatRegistry(handlers));
            _fileTransferService = new LinuxArchiveFileTransferService();
            _thumbnailService = new ArchiveThumbnailService();
            var recentFilesService = new XdgRecentFilesService();

            var window = new MainWindow();
            var interactionService = new AvaloniaUserInteractionService(() => window);
            var viewModel = new MainWindowViewModel(
                archiveService,
                _fileTransferService,
                interactionService,
                recentFilesService,
                _thumbnailService);
            var startupPath = desktop.Args?
                .FirstOrDefault(argument => argument.Length == 0 || argument[0] != '-');

            window.Configure(viewModel, _fileTransferService, startupPath);
            desktop.MainWindow = window;
            desktop.Exit += (_, _) => Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }

    public void Dispose()
    {
        _fileTransferService?.Dispose();
        _fileTransferService = null;
        _thumbnailService?.Dispose();
        _thumbnailService = null;
        GC.SuppressFinalize(this);
    }
}
