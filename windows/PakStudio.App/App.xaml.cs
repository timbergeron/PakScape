using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using PakStudio.App.Services;
using PakStudio.App.ViewModels;
using PakStudio.App.Views;
using PakStudio.Core.Interfaces;
using PakStudio.Formats.Pak;

namespace PakStudio.App;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();
        ConfigureServices(services);

        _serviceProvider = services.BuildServiceProvider();

        var window = _serviceProvider.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddLogging();

        services.AddSingleton<IArchiveFormatHandler, PakFormatHandler>();
        services.AddSingleton<IArchiveFormatRegistry, ArchiveFormatRegistry>();
        services.AddSingleton<IArchiveService, ArchiveService>();
        services.AddSingleton<IFileDialogService, FileDialogService>();
        services.AddSingleton<IMessageBoxService, MessageBoxService>();
        services.AddSingleton<IRecentFilesService, JsonRecentFilesService>();
        services.AddSingleton<IIconService, GlyphIconService>();

        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainWindow>();
    }
}
