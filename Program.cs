using Avalonia.ReactiveUI;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Services;

namespace DeadlockAdvisor;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Started as the installer of an update: put this version in place of the old one, and nothing else.
        if (AppUpdateService.InstallIfAsked(args))
            return;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        // A version downloaded meanwhile goes in now, once this one has shut down and saved everything.
        AppUpdateService.InstallIfDownloaded();
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTraceQuietly()
            .UseReactiveUI();
}
