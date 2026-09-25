using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using DeadlockAdvisor.Features.MainWindow;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Contracts;
using DeadlockAdvisor.Tests.Fakes;
using DeadlockAdvisor.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace DeadlockAdvisor.Tests.Ui;

/// <summary>
/// The main window wired exactly as the app wires it, but over a throwaway copy of the golden data,
/// scratch settings, and the Python repo's art when it's there.
/// </summary>
public sealed class UiHarness : IDisposable
{
    public const string PythonAssets = @"D:\Dev\Python\deadlock_advisor\assets";

    private readonly TempDirectory _root = new();
    private readonly ServiceProvider _services;

    public UiHarness(Action<FakeSettingsService>? configure = null)
    {
        Directory.CreateDirectory(Path.Combine(_root.Path, "data"));
        foreach (var file in Directory.GetFiles(Golden.DataDir))
            File.Copy(file, Path.Combine(_root.Path, "data", Path.GetFileName(file)));

        Settings = new FakeSettingsService();
        Settings.Current.DataRoot = _root.Path;
        configure?.Invoke(Settings);

        var services = new ServiceCollection();
        App.RegisterServices(services);
        services.AddSingleton<ISettingsService>(Settings);
        _services = services.BuildServiceProvider();

        Data = _services.GetRequiredService<IDataService>();
        Data.Initialize();
        Art = _services.GetRequiredService<IArtService>();
        Art.SetAssetsDir(Directory.Exists(PythonAssets) ? PythonAssets : Data.AssetsDir);

        ViewModel = _services.GetRequiredService<MainWindowViewModel>();
        Window = new MainWindow(_services.GetRequiredService<IModalService>(), Settings, Art, Data)
        {
            DataContext = ViewModel,
            Width = 1600,
            Height = 1000,
        };
    }

    public FakeSettingsService Settings { get; }
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

    public string Screenshot(string name)
    {
        Settle();
        var frame = Window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing was rendered");
        var path = Path.Combine(RepoRoot(), "mockups", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        frame.Save(path);
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
