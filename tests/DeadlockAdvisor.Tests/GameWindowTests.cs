using DeadlockAdvisor.Services;

namespace DeadlockAdvisor.Tests;

/// <summary>Which window Detect reads: the game's, found by its executable's name.</summary>
public class GameWindowTests
{
    private static readonly IntPtr _foreground = 100;
    private static readonly IntPtr _gameWindow = 200;

    [Theory]
    [InlineData("project8", true)]
    [InlineData("Project8", true)]
    [InlineData("deadlock", true)]
    [InlineData("Deadlock", true)]
    [InlineData("DeadlockAdvisor", false)]
    [InlineData("chrome", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TheGameIsRecognisedByItsExecutablesName(string? name, bool isGame) =>
        Assert.Equal(isGame, ScreenCaptureService.IsGameProcess(name));

    [Fact]
    public void TheForegroundWindowIsReadWhenItIsTheGames()
    {
        var chosen = ScreenCaptureService.ChooseGameWindow(_foreground, "project8", [("project8", _gameWindow)]);

        Assert.Equal(_foreground, chosen);
    }

    [Fact]
    public void WithAnotherAppInFrontTheRunningGamesWindowIsRead()
    {
        var chosen = ScreenCaptureService.ChooseGameWindow(_foreground, "chrome", [("project8", _gameWindow)]);

        Assert.Equal(_gameWindow, chosen);
    }

    [Fact]
    public void AGameProcessWithoutAWindowIsSkipped()
    {
        var chosen = ScreenCaptureService.ChooseGameWindow(_foreground, "chrome",
            [("project8", IntPtr.Zero), ("project8", _gameWindow)]);

        Assert.Equal(_gameWindow, chosen);
    }

    [Fact]
    public void WithNoGameRunningNoWindowIsChosen()
    {
        Assert.Equal(IntPtr.Zero, ScreenCaptureService.ChooseGameWindow(_foreground, "chrome", []));
        Assert.Equal(IntPtr.Zero, ScreenCaptureService.ChooseGameWindow(IntPtr.Zero, null, [("notepad", _gameWindow)]));
    }

    [Fact]
    public void AForegroundWindowWithNoHandleIsNotTheGame()
    {
        Assert.Equal(_gameWindow, ScreenCaptureService.ChooseGameWindow(IntPtr.Zero, "project8", [("project8", _gameWindow)]));
    }
}
