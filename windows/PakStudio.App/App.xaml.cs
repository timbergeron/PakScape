using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using PakStudio.App.Services;
using PakStudio.App.ViewModels;
using PakStudio.App.Views;
using PakStudio.Core.Interfaces;
using PakStudio.Formats.Pak;
using PakStudio.Formats.Pk3;

namespace PakStudio.App;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;
    private SystemThemeService? _systemThemeService;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _systemThemeService = new SystemThemeService(this);

        var services = new ServiceCollection();
        ConfigureServices(services);

        _serviceProvider = services.BuildServiceProvider();

        /* Explorer verbs are re-registered so they follow the running copy of PakScape. */
        ShellIntegrationService.UpdateExplorerActions(PakScapeSettings.Current.ExplorerActionsEnabled);

        /* Shell verbs only show dialogs, so the app must outlive the last closed dialog. */
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (await ShellCommandRunner.TryRunAsync(_serviceProvider, e.Args).ConfigureAwait(true))
        {
            Shutdown();
            return;
        }

        var startupArchive = e.Args.FirstOrDefault(argument =>
            !string.IsNullOrWhiteSpace(argument) && !argument.StartsWith("-", StringComparison.Ordinal));
        var windowService = _serviceProvider.GetRequiredService<IArchiveWindowService>();
        var window = string.IsNullOrWhiteSpace(startupArchive)
            ? windowService.ShowBlankWorkspace()
            : windowService.ShowArchive(startupArchive);
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnLastWindowClose;

        /* Launching from the taskbar with no document opens the archive picker. */
        if (string.IsNullOrWhiteSpace(startupArchive))
        {
            window.OpenArchiveDialog();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _systemThemeService?.Dispose();
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddLogging();

        services.AddSingleton<IArchiveFormatHandler, PakFormatHandler>();
        services.AddSingleton<IArchiveFormatHandler, Pk3FormatHandler>();
        services.AddSingleton<IArchiveFormatHandler, KpfFormatHandler>();
        services.AddSingleton<IArchiveFormatRegistry, ArchiveFormatRegistry>();
        services.AddSingleton<IArchiveService, ArchiveService>();
        services.AddSingleton<IFileDialogService, FileDialogService>();
        services.AddSingleton<IMessageBoxService, MessageBoxService>();
        services.AddSingleton<ITextInputService, TextInputService>();
        services.AddSingleton<IRecentFilesService, JsonRecentFilesService>();
        services.AddSingleton<IDetailsColumnLayoutService, JsonDetailsColumnLayoutService>();
        services.AddSingleton<IIconService, GlyphIconService>();
        services.AddScoped<IArchiveFileTransferService, ArchiveFileTransferService>();
        services.AddScoped<ArchiveThumbnailService>();
        services.AddScoped<ItemInfoWindowService>();

        services.AddSingleton<IArchiveWindowService, ArchiveWindowService>();
        services.AddScoped<MainWindowViewModel>();
        services.AddScoped<MainWindow>();
    }
}
