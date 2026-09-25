using System.IO;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DeadlockAdvisor.Features.MainWindow;
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

    private void ConfigureServices()
    {
        var services = new ServiceCollection();

        services.AddSingleton<ISettingsService, JsonSettingsService>();
        services.AddSingleton<ILoggingService, ConsoleLoggingService>();
        services.AddSingleton<INotificationService, NotificationService>();
        services.AddSingleton<IModalService, ModalService>();

        services.AddTransient<MainWindowViewModel>();
        services.AddSingleton<NotificationOverlayViewModel>();

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
                var settingsService = _serviceProvider!.GetRequiredService<ISettingsService>();
                settingsService.LoadAsync().GetAwaiter().GetResult();

                desktop.MainWindow = new MainWindow(_serviceProvider!.GetRequiredService<IModalService>())
                {
                    DataContext = _serviceProvider!.GetRequiredService<MainWindowViewModel>(),
                };
            }
            catch (Exception ex)
            {
                var logPath = Path.Combine(JsonSettingsService.AppDataPath, "startup-error.log");
                Directory.CreateDirectory(JsonSettingsService.AppDataPath);
                File.WriteAllText(logPath, ex.ToString());
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
