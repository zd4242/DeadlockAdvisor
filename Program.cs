using Avalonia.ReactiveUI;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Services;

namespace DeadlockAdvisor;

sealed class Program
{
    /// <summary>This copy's claim on the data folder; the app listens on it for a second start (null off Windows).</summary>
    internal static SingleInstance? Primary { get; private set; }

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Started as the installer of an update: put this version in place of the old one, and nothing else.
        if (AppUpdateService.InstallIfAsked(args))
            return;

        // One copy per data folder: a second start raises the first one's window and leaves.
        if (OperatingSystem.IsWindows())
        {
            var name = SingleInstance.NameFor(JsonSettingsService.AppDataPath);
            Primary = SingleInstance.TryBecomePrimary(name);
            if (Primary is null)
            {
                SingleInstance.SignalPrimary(name);
                return;
            }
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        // Let go before the installer below waits for this process to exit and starts the new version.
        Primary?.Dispose();
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
