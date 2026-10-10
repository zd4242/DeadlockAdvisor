using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using DeadlockAdvisor.Features.MainWindow;
using DeadlockAdvisor.Features.Shared.Modals.Base;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Contracts;
using DeadlockAdvisor.Tests.Fakes;
using DeadlockAdvisor.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace DeadlockAdvisor.Tests.Ui;

/// <summary>
/// The main window wired exactly as the app wires it, but over a throwaway copy of the golden data,
/// scratch settings, and real art when DEADLOCK_ASSETS points at a folder of it (an app data folder's
/// assets/, for screenshots with portraits and icons).
/// </summary>
public sealed class UiHarness : IDisposable
{
    public static string? ArtFolder => Environment.GetEnvironmentVariable("DEADLOCK_ASSETS") is { Length: > 0 } dir && Directory.Exists(dir) ? dir : null;

    private readonly TempDirectory _root = new();
    private readonly ServiceProvider _services;

    /// <param name="overrides">Registered last, to stand in for the app's own services.</param>
    public UiHarness(Action<FakeSettingsService>? configure = null, Action<IServiceCollection>? overrides = null)
    {
        Directory.CreateDirectory(Path.Combine(_root.Path, "data"));
        foreach (var file in Directory.GetFiles(Golden.DataDir))
            File.Copy(file, Path.Combine(_root.Path, "data", Path.GetFileName(file)));

        Settings = new FakeSettingsService();
        Settings.Current.DataRoot = _root.Path;
        // Past the first run: without art, its welcome would cover every page.
        Settings.Current.WelcomeOffered = true;
        configure?.Invoke(Settings);

        var services = new ServiceCollection();
        App.RegisterServices(services);
        services.AddSingleton<ISettingsService>(Settings);
        services.AddSingleton<ILoggingService>(new FakeLoggingService());
        services.AddSingleton<IDeadlockApi>(Api);
        services.AddSingleton<IConnectivityService>(Connectivity);
        services.AddSingleton<IScreenCaptureService>(Capture);
        services.AddSingleton<IGlobalHotkeyService>(Hotkey);
        services.AddSingleton<IForegroundService>(Foreground);
        services.AddSingleton<IAttentionService>(Attention);
        overrides?.Invoke(services);
        _services = services.BuildServiceProvider();

        Data = _services.GetRequiredService<IDataService>();
        Data.Initialize();
        Art = _services.GetRequiredService<IArtService>();
        Art.SetAssetsDir(ArtFolder ?? Data.AssetsDir);

        ViewModel = _services.GetRequiredService<MainWindowViewModel>();
        Window = new MainWindow(_services.GetRequiredService<IModalService>(), Settings, Art, Data,
            _services.GetRequiredService<IForegroundService>())
        {
            DataContext = ViewModel,
            Width = 1600,
            Height = 1000,
        };
    }

    public FakeSettingsService Settings { get; }
    public IServiceProvider Services => _services;

    /// <summary>With the model editors shown, opening on <paramref name="page"/>.</summary>
    public static void Editing(FakeSettingsService settings, int page = 0)
    {
        settings.Current.ShowModelEditors = true;
        settings.Current.LastPage = page;
    }

    /// <summary>No network in tests: every call fails as if the site were down, unless a test says otherwise.</summary>
    public FakeDeadlockApi Api { get; } = new();

    /// <summary>Online until a test says otherwise, so the machine's real network never shows.</summary>
    public FakeConnectivity Connectivity { get; } = new();

    public FakeScreenCapture Capture { get; } = new();
    public FakeGlobalHotkey Hotkey { get; } = new();
    public FakeForeground Foreground { get; } = new();
    public FakeAttention Attention { get; } = new();
    public IDataService Data { get; }
    public IArtService Art { get; }
    public MainWindowViewModel ViewModel { get; }
    public MainWindow Window { get; }

    public void Show()
    {
        Window.Show();
        Settle();
    }

    /// <summary>Let layout, bindings and rendering catch up.</summary>
    public static void Settle()
    {
        for (var pass = 0; pass < 3; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    /// <summary>
    /// Wait for something on a real-time timer, such as the item card's show delay. The dispatcher
    /// only fires timers from its own loop, so this awaits (handing the loop back) rather than
    /// sleeping. False if the condition never held within the timeout.
    /// </summary>
    public static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(3));
        while (true)
        {
            Settle();
            if (condition())
                return true;
            if (DateTime.UtcNow > deadline)
                return false;
            await Task.Delay(20);
        }
    }

    public string Screenshot(string name) => Save(Window, name);

    /// <summary>The open modal, which lives in a window of its own over the main one.</summary>
    public string ScreenshotModal(string name) =>
        Save(Window.OwnedWindows.OfType<ModalWindow>().Single(), name);

    private static string Save(Window window, string name)
    {
        Settle();
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing was rendered");
        var path = MockupPath(name);
        frame.Save(path);
        return path;
    }

    /// <summary>A file in mockups/, the git-ignored folder the tests write screenshots and reports to; the folder is made if missing.</summary>
    public static string MockupPath(string name)
    {
        var path = Path.Combine(RepoRoot(), "mockups", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    public static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DeadlockAdvisor.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Couldn't find the repository root");
    }

    public void Dispose()
    {
        Window.Close();
        _services.Dispose();
        _root.Dispose();
    }
}
