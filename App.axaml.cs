using System.IO;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DeadlockAdvisor.Features.HeroTraits;
using DeadlockAdvisor.Features.ItemFormulas;
using DeadlockAdvisor.Features.MainWindow;
using DeadlockAdvisor.Features.Match;
using DeadlockAdvisor.Features.Match.Detect;
using DeadlockAdvisor.Features.Match.Import;
using DeadlockAdvisor.Features.Shared.Notifications;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Contracts;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using MainWindow = DeadlockAdvisor.Features.MainWindow.MainWindow;

namespace DeadlockAdvisor;

public partial class App : Application
{
    private IServiceProvider? _serviceProvider;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        ConfigureServices();
    }

    public static void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<ISettingsService, JsonSettingsService>();
        services.AddSingleton<ILoggingService, LoggingService>();
        services.AddSingleton<INotificationService, NotificationService>();
        services.AddSingleton<IModalService, ModalService>();
        services.AddSingleton<IDataService, DataService>();
        services.AddSingleton<IArtService, ArtService>();
        services.AddSingleton<IFilePickerService, FilePickerService>();
        services.AddSingleton<IDeadlockApi, DeadlockApi>();
        services.AddSingleton<IGameApiService, GameApiService>();
        services.AddSingleton<IMatchStatsService, MatchStatsService>();
        services.AddSingleton<IArtDownloadService, ArtDownloadService>();
        services.AddSingleton<IExcelExportService, ExcelExportService>();
        services.AddSingleton<IScreenCaptureService, ScreenCaptureService>();
        services.AddSingleton<IGlobalHotkeyService, GlobalHotkeyService>();
        services.AddSingleton<IForegroundService, ForegroundService>();
        services.AddSingleton<IMatchLookupService, MatchLookupService>();
        services.AddTransient<DetectAction>();
        services.AddTransient<ImportMatchAction>();

        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<DataMenuViewModel>();
        services.AddTransient<MatchViewModel>();
        services.AddTransient<DataRanksViewModel>();
        services.AddTransient<HeroTraitsViewModel>();
        services.AddTransient<ItemFormulasViewModel>();
        services.AddSingleton<NotificationOverlayViewModel>();
    }

    private void ConfigureServices()
    {
        var services = new ServiceCollection();
        RegisterServices(services);
        _serviceProvider = services.BuildServiceProvider();

        InstallGlobalExceptionHandlers(
            _serviceProvider.GetRequiredService<ILoggingService>(),
            _serviceProvider.GetRequiredService<INotificationService>());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            try
            {
                var services = _serviceProvider!;
                var settingsService = services.GetRequiredService<ISettingsService>();
                settingsService.LoadAsync().GetAwaiter().GetResult();

                var data = services.GetRequiredService<IDataService>();
                data.Initialize();
                var art = services.GetRequiredService<IArtService>();
                art.SetAssetsDir(data.AssetsDir);
                services.GetRequiredService<ILoggingService>().Information(
                    $"Started: data in {data.DataRoot}, {art.Count(ArtKind.Hero)} hero portrait(s) and {art.Count(ArtKind.Item)} item icon(s), "
                    + $"log in {Path.Combine(JsonSettingsService.AppDataPath, LoggingService.FileName)}");

                desktop.MainWindow = new MainWindow(services.GetRequiredService<IModalService>(), settingsService, art, data,
                    services.GetRequiredService<IForegroundService>())
                {
                    DataContext = services.GetRequiredService<MainWindowViewModel>(),
                };
                services.GetRequiredService<IGlobalHotkeyService>().Attach(desktop.MainWindow);
            }
            catch (Exception ex)
            {
                Directory.CreateDirectory(JsonSettingsService.AppDataPath);
                File.WriteAllText(Path.Combine(JsonSettingsService.AppDataPath, "startup-error.log"), ex.ToString());
                throw;
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Last-resort handlers so an unexpected failure is logged and reported instead of terminating
    /// the app. Expected failures should still be caught where they can be reported meaningfully.
    /// </summary>
    private static void InstallGlobalExceptionHandlers(ILoggingService loggingService, INotificationService notificationService)
    {
        // Exceptions from ReactiveCommands without a ThrownExceptions subscriber and from OAPH sources.
        RxApp.DefaultExceptionHandler = System.Reactive.Observer.Create<Exception>(ex =>
            notificationService.ShowError($"Something went wrong: {ex.Message}", ex));

        // Exceptions escaping async void methods and other dispatcher callbacks.
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            notificationService.ShowError($"Something went wrong: {e.Exception.Message}", e.Exception);
            e.Handled = true;
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            loggingService.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };
    }
}
