using Avalonia;
using Avalonia.Headless;
using Avalonia.ReactiveUI;
using DeadlockAdvisor.Tests.Ui;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace DeadlockAdvisor.Tests.Ui;

/// <summary>The real app, rendered through Skia without a screen, so UI tests can take screenshots.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .UseReactiveUI();
}
