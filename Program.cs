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
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        // After an update, "Restart now" starts the new exe once this one has shut down and saved everything.
        AppUpdateService.RestartIfAsked();
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTraceQuietly()
            .UseReactiveUI();
}
