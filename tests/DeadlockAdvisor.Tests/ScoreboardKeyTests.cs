using System.Reactive.Concurrency;
using Avalonia.Input;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Contracts;
using DeadlockAdvisor.Tests.Fakes;

namespace DeadlockAdvisor.Tests;

/// <summary>The scoreboard key watcher on a virtual clock and a keyboard that is only what a test says is down.</summary>
public sealed class ScoreboardKeyTests : IDisposable
{
    private const uint Tab = 0x09;
    private const uint Alt = 0x12;

    private readonly HistoricalScheduler _clock = new();
    private readonly FakeSettingsService _settings = new();
    private readonly FakeForeground _foreground = new() { IsGameInFront = true };
    private readonly HashSet<uint> _down = [];
    private readonly List<uint> _asked = [];
    private readonly ScoreboardKeyService _service;
    private int _presses;

    public ScoreboardKeyTests()
    {
        _settings.Current.ReadBuildsOnTab = true;
        _service = new ScoreboardKeyService(_settings, _foreground, new FakeLoggingService(), _clock, key =>
        {
            _asked.Add(key);
            return _down.Contains(key);
        });
        _service.Pressed.Subscribe(_ => _presses++);
    }

    public void Dispose() => _service.Dispose();

    private void Wait(int polls = 1) => _clock.AdvanceBy(ScoreboardKeyService.PollEvery * polls);

    [Fact]
    public void NothingIsPolledWhileTheSettingIsOff()
    {
        if (!OperatingSystem.IsWindows())
            return;
        _settings.Update(s => s.ReadBuildsOnTab = false);
        _asked.Clear();
        _down.Add(Tab);

        Wait(50);

        Assert.Empty(_asked);
        Assert.Equal(0, _presses);
        Assert.Equal(ScoreboardKeyStatus.Off, StatusNow());
    }

    [Fact]
    public void AHeldKeyIsOnePressUntilItIsLetGoAndPressedAgain()
    {
        if (!OperatingSystem.IsWindows())
            return;
        Wait(3);
        Assert.Equal(0, _presses);

        _down.Add(Tab);
        Wait(10);
        Assert.Equal(1, _presses);

        _down.Remove(Tab);
        Wait(2);
        _down.Add(Tab);
        Wait(2);
        Assert.Equal(2, _presses);
    }

    [Fact]
    public void TheKeyIsIgnoredWhileTheGameIsNotInFrontAndCountsAsAFreshPressWhenItIs()
    {
        if (!OperatingSystem.IsWindows())
            return;
        _foreground.IsGameInFront = false;
        _down.Add(Tab);
        Wait(5);
        Assert.Equal(0, _presses);

        _foreground.IsGameInFront = true;
        Wait(2);
        Assert.Equal(1, _presses);
    }

    [Fact]
    public void AltTabIsTheGameBeingLeftNotTheScoreboardBeingAsked()
    {
        if (!OperatingSystem.IsWindows())
            return;
        _down.Add(Alt);
        _down.Add(Tab);
        Wait(5);

        Assert.Equal(0, _presses);
    }

    [Fact]
    public void AMovedKeyIsTheOneWatched()
    {
        if (!OperatingSystem.IsWindows())
            return;
        _settings.Update(s => s.ScoreboardKey = nameof(Key.F3));

        _down.Add(Tab);
        Wait(3);
        Assert.Equal(0, _presses);

        _down.Add(0x72);
        Wait(3);
        Assert.Equal(1, _presses);
    }

    [Fact]
    public void TurningItOnStartsWatchingWithoutARestart()
    {
        if (!OperatingSystem.IsWindows())
            return;
        _settings.Update(s => s.ReadBuildsOnTab = false);
        _down.Add(Tab);
        Wait(5);
        Assert.Equal(0, _presses);

        _settings.Update(s => s.ReadBuildsOnTab = true);
        Wait(3);

        Assert.Equal(1, _presses);
        Assert.Equal(ScoreboardKeyStatus.Watching, StatusNow());
    }

    [Fact]
    public void IsHeldSaysWhetherTheKeyIsDownNow()
    {
        if (!OperatingSystem.IsWindows())
            return;
        Assert.False(_service.IsHeld);
        _down.Add(Tab);
        Assert.True(_service.IsHeld);
    }

    [Fact]
    public void ItCannotWatchOffWindows()
    {
        if (OperatingSystem.IsWindows())
            return;
        Assert.Equal(ScoreboardKeyStatus.Unsupported, StatusNow());
    }

    [Fact]
    public void TabIsTheDefaultAndOnlyAnUnmodifiedSupportedKeyCanReplaceIt()
    {
        var settings = new AppSettings();
        Assert.False(settings.ReadBuildsOnTab);
        Assert.Equal(Key.Tab, settings.Get());
        Assert.Null(ScoreboardKeys.Problem(new KeyGesture(Key.Tab)));
        Assert.Null(ScoreboardKeys.Problem(new KeyGesture(Key.CapsLock)));
        Assert.Null(ScoreboardKeys.Problem(new KeyGesture(Key.F3)));
        Assert.NotNull(ScoreboardKeys.Problem(new KeyGesture(Key.Tab, KeyModifiers.Control)));
        Assert.NotNull(ScoreboardKeys.Problem(new KeyGesture(Key.Space)));
        Assert.NotNull(ScoreboardKeys.Problem(new KeyGesture(Key.Escape)));
    }

    [Theory]
    [InlineData("Banana")]
    [InlineData("Escape")]
    [InlineData("")]
    public void ASavedKeyThatCannotBeWatchedFallsBackToTab(string saved) =>
        Assert.Equal(Key.Tab, new AppSettings { ScoreboardKey = saved }.Get());

    [Theory]
    [InlineData(Key.Tab, 0x09u)]
    [InlineData(Key.CapsLock, 0x14u)]
    [InlineData(Key.F3, 0x72u)]
    [InlineData(Key.X, 0x58u)]
    public void KeysMapToTheirWindowsCodes(Key key, uint code) => Assert.Equal(code, ScoreboardKeys.VirtualKey(key));

    private ScoreboardKeyStatus StatusNow()
    {
        var status = ScoreboardKeyStatus.Unsupported;
        using var _ = _service.Status.Subscribe(value => status = value);
        return status;
    }
}
