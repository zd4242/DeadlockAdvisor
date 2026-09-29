using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Features.Match;
using DeadlockAdvisor.Features.Match.Detect;
using DeadlockAdvisor.Features.Match.Import;
using DeadlockAdvisor.Features.Settings.General;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Tests.Fakes;
using DeadlockAdvisor.Tests.Support;

namespace DeadlockAdvisor.Tests;

/// <summary>Importing a finished match by its ID: the lookup, picking you out, and what lands in the match.</summary>
public sealed class MatchImportTests : IDisposable
{
    public const long MatchId = 108474234;
    public const string MetadataUrl = "https://api.deadlock-api.com/v1/matches/108474234/metadata";

    // Hidden King (team 0) won: Haze's side. The last Archmother player is a hero the data doesn't have yet.
    private static readonly (long Account, long HeroGameId, int Team, int Slot)[] _players =
    [
        (1003, 13, 0, 3), // haze
        (1001, 6, 0, 1), // abrams
        (1002, 15, 0, 2), // bebop
        (1004, 11, 0, 4), // dynamo
        (1005, 20, 0, 5), // ivy
        (1006, 1, 0, 6), // infernus
        (1012, 999, 1, 12),
        (1007, 77, 1, 7), // apollo
        (1008, 72, 1, 8), // billy
        (1009, 16, 1, 9), // calico
        (1010, 81, 1, 10), // celeste
        (1011, 64, 1, 11), // drifter
    ];

    private readonly DataFixture _fixture = new();
    private readonly FakeDeadlockApi _api = new();
    private readonly List<ViewModelBase> _shown = [];
    private readonly IDisposable _watchModals;
    private readonly MatchViewModel _page;

    public MatchImportTests()
    {
        _watchModals = _fixture.Modals.ShowModalObservable.Subscribe(_shown.Add);
        var log = new FakeLoggingService();
        var detect = new DetectAction(_fixture.Data, _fixture.Settings, _fixture.Modals, new FakeScreenCapture(), log);
        var import = new ImportMatchAction(_fixture.Data, _fixture.Settings, _fixture.Modals, new MatchLookupService(_api), log);
        var dataRanks = new DataRanksViewModel(_fixture.Data, new MatchStatsService(new FakeDeadlockApi()), new NotificationService(log));
        _page = new MatchViewModel(_fixture.Data, _fixture.Settings, detect, import, dataRanks);
    }

    public void Dispose()
    {
        _watchModals.Dispose();
        _page.Dispose();
        _fixture.Dispose();
    }

    /// <summary>The API's answers for the canned match, and Steam names for all but the unknown hero's player.</summary>
    internal static void Serve(FakeDeadlockApi api)
    {
        api.Json[MetadataUrl] = Metadata;
        var accounts = string.Join(",", _players.OrderBy(p => p.Team).ThenBy(p => p.Slot).Select(p => p.Account));
        api.Json[$"{MatchLookupService.SteamProfiles}?account_ids={accounts}"] = () => new JsonArray(_players
            .Where(p => p.Account != 1012)
            .Select(p => (JsonNode)new JsonObject { ["account_id"] = p.Account, ["personaname"] = $"Player {p.Account}" })
            .ToArray());
    }

    private static JsonNode Metadata() => new JsonObject
    {
        ["match_info"] = new JsonObject
        {
            ["match_id"] = MatchId,
            ["start_time"] = 1790689419,
            ["duration_s"] = 2541,
            ["winning_team"] = 0,
            ["players"] = new JsonArray(_players
                .Select(p => (JsonNode)new JsonObject
                {
                    ["account_id"] = p.Account,
                    ["hero_id"] = p.HeroGameId,
                    ["team"] = p.Team,
                    ["player_slot"] = p.Slot,
                })
                .ToArray()),
        },
    };

    private async Task<ImportMatchViewModel> LookUpAsync(string text = "108474234")
    {
        await _page.ImportCommand.Execute();
        var dialog = Assert.IsType<ImportMatchViewModel>(_shown[^1]);
        dialog.MatchIdText = text;
        await dialog.LookUpCommand.Execute();
        return dialog;
    }

    [Theory]
    [InlineData("22202", 22202)]
    [InlineData(" 76561197960287930 ", 22202)]
    [InlineData("[U:1:22202]", 22202)]
    [InlineData("STEAM_0:0:11101", 22202)]
    [InlineData("https://steamcommunity.com/profiles/76561197960287930/", 22202)]
    public void ASteamAccountIsReadInAnyOfItsForms(string text, long expected)
    {
        Assert.True(SteamAccount.TryParse(text, out var accountId));
        Assert.Equal(expected, accountId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("someone")]
    [InlineData("https://steamcommunity.com/id/someone/")]
    [InlineData("99999999999999999999999")]
    public void WhatIsntASteamAccountIsntRead(string text) => Assert.False(SteamAccount.TryParse(text, out _));

    [Theory]
    [InlineData("108474234", MatchId)]
    [InlineData(" https://example.com/match/108474234?tab=2 ", MatchId)]
    public void AMatchIdIsReadOnItsOwnOrOutOfALink(string text, long expected)
    {
        Assert.True(MatchLookupService.TryParseMatchId(text, out var matchId));
        Assert.Equal(expected, matchId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("match")]
    [InlineData("0")]
    public void WhatIsntAMatchIdIsntRead(string text) => Assert.False(MatchLookupService.TryParseMatchId(text, out _));

    [Fact]
    public async Task TheLookupReadsEachTeamInLobbyOrder()
    {
        Serve(_api);

        var match = await new MatchLookupService(_api).LookUpAsync(MatchId);

        Assert.Equal(MatchId, match.MatchId);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790689419), match.StartedAt);
        Assert.Equal(TimeSpan.FromSeconds(2541), match.Duration);
        Assert.Equal(0, match.WinningTeam);
        Assert.Equal(Enumerable.Range(1, 12), match.Players.Select(player => player.PlayerSlot));
        Assert.Equal(new MatchPlayer(1003, 13, 0, 3), match.Players[2]);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "once it's over")]
    [InlineData(HttpStatusCode.TooManyRequests, "Try again later")]
    [InlineData(HttpStatusCode.InternalServerError, "Couldn't reach deadlock-api.com")]
    public async Task AFailedLookupSaysWhy(HttpStatusCode status, string message)
    {
        _api.Json[MetadataUrl] = () => throw new HttpRequestException("failed (test)", null, status);

        var error = await Assert.ThrowsAsync<MatchLookupException>(() => new MatchLookupService(_api).LookUpAsync(MatchId));

        Assert.Contains(message, error.Message);
    }

    [Fact]
    public async Task NoKnownSteamProfilesIsNoNames()
    {
        _api.Json[$"{MatchLookupService.SteamProfiles}?account_ids=1,2"] = () => throw new HttpRequestException("none (test)", null, HttpStatusCode.NotFound);

        Assert.Empty(await new MatchLookupService(_api).SteamNamesAsync([1, 2, 0]));
    }

    [AvaloniaFact]
    public async Task ASavedAccountPicksYouOutAndTheMatchIsApplied()
    {
        Serve(_api);
        _fixture.Settings.Update(s => s.SteamAccountId = 1003);

        var dialog = await LookUpAsync();

        Assert.Equal("haze", dialog.Self?.HeroId);
        Assert.Equal("YOUR TEAM", dialog.OwnHeading);
        Assert.Equal([1, 2, 3, 4, 5, 6], dialog.OwnRows.Select(row => row.Player.PlayerSlot));
        Assert.Equal(["YOU", "ally", "enemy"], new[] { dialog.OwnRows[2], dialog.OwnRows[0], dialog.FoeRows[0] }.Select(row => row.RoleText));
        Assert.Contains("your team won", dialog.Summary);
        Assert.Contains("42 min", dialog.Summary);
        Assert.Contains("saved Steam account", dialog.Prompt);
        Assert.Equal("Player 1003", dialog.Self!.PlayerName);
        Assert.Equal("", dialog.FoeRows[^1].PlayerName);
        Assert.Contains("1 of these heroes isn't in your data", dialog.Warning);
        Assert.False(dialog.ShowsRememberMe);

        dialog.Submit();

        Assert.False(_fixture.Modals.IsModalOpen);
        var match = _page.Match;
        Assert.Equal("haze", match.SelfHero);
        Assert.Equal(["abrams", "bebop", "haze", "dynamo", "ivy", "infernus"], match.OwnTeam);
        Assert.Equal(["apollo", "billy", "calico", "celeste", "drifter"], match.Enemies);
        Assert.Equal(["abrams", "bebop", "haze", "dynamo", "ivy", "infernus"], _page.Board.AllySlots.Select(slot => slot.HeroId));
        Assert.Equal("self", _fixture.Settings.Current.LastMatch!.Roles["haze"]);
        Assert.False(_page.Results.IsEmpty);
        Assert.Equal(1003, _fixture.Settings.Current.SteamAccountId);
    }

    [AvaloniaFact]
    public async Task WithoutASavedAccountYouPickYourselfAndCanBeRemembered()
    {
        Serve(_api);

        var dialog = await LookUpAsync();

        Assert.Null(dialog.Self);
        Assert.False(await dialog.ApplyCommand.CanExecute.FirstAsync());
        Assert.Equal(("THE HIDDEN KING", "THE ARCHMOTHER"), (dialog.OwnHeading, dialog.FoeHeading));
        Assert.Contains("The Hidden King won", dialog.Summary);
        Assert.Contains("Press You", dialog.Prompt);
        Assert.All(dialog.Players, player => Assert.Equal(Role.None, player.Role));

        // You on the right-hand side: your team moves to the left.
        var celeste = dialog.FoeRows.Single(row => row.HeroId == "celeste");
        await celeste.SetSelfCommand.Execute();

        Assert.Same(celeste, dialog.Self);
        Assert.Equal([7, 8, 9, 10, 11, 12], dialog.OwnRows.Select(row => row.Player.PlayerSlot));
        Assert.Contains("your team lost", dialog.Summary);
        Assert.True(dialog.ShowsRememberMe);
        Assert.True(dialog.RememberMe);
        Assert.Contains("Remember Player 1010 as my Steam account", dialog.RememberText);

        await dialog.ApplyCommand.Execute();

        Assert.Equal("celeste", _page.Match.SelfHero);
        Assert.Equal(["apollo", "billy", "calico", "drifter"], _page.Match.Allies);
        Assert.Equal(6, _page.Match.Enemies.Count);
        Assert.Equal(1010, _fixture.Settings.Current.SteamAccountId);
    }

    [AvaloniaFact]
    public async Task PlayingAsSomeoneElseDoesntReplaceTheSavedAccountUnlessAsked()
    {
        Serve(_api);
        _fixture.Settings.Update(s => s.SteamAccountId = 1003);
        var dialog = await LookUpAsync();

        dialog.ToggleSelf(dialog.OwnRows[0]);

        Assert.True(dialog.ShowsRememberMe);
        Assert.False(dialog.RememberMe);
        await dialog.ApplyCommand.Execute();
        Assert.Equal("abrams", _page.Match.SelfHero);
        Assert.Equal(1003, _fixture.Settings.Current.SteamAccountId);
    }

    [AvaloniaFact]
    public async Task AFailedLookupShowsWhyAndLeavesTheMatchAlone()
    {
        _page.Match.SetRole("haze", Role.Self);
        _api.Json[MetadataUrl] = () => throw new HttpRequestException("failed (test)", null, HttpStatusCode.NotFound);

        var dialog = await LookUpAsync("https://example.com/match/108474234");

        Assert.Contains("once it's over", dialog.Error);
        Assert.False(dialog.HasMatch);
        Assert.False(dialog.IsLookingUp);
        await dialog.CancelCommand.Execute();
        Assert.False(_fixture.Modals.IsModalOpen);
        Assert.Equal("haze", _page.Match.SelfHero);
    }

    [AvaloniaFact]
    public async Task SteamNamesAreANicety()
    {
        _api.Json[MetadataUrl] = Metadata;

        var dialog = await LookUpAsync();

        Assert.True(dialog.HasMatch);
        Assert.All(dialog.Players, player => Assert.Equal("", player.PlayerName));
    }

    [Fact]
    public void TheSettingsBoxSavesAnAccountAndIgnoresWhatIsntOne()
    {
        var settings = new FakeSettingsService();
        using var page = new GeneralSettingsViewModel(settings, null!, null!, null!);
        Assert.Equal("Not set", page.SteamAccountStatus);

        page.SteamAccountText = "https://steamcommunity.com/profiles/76561197960287930";
        Assert.Equal(22202, settings.Current.SteamAccountId);
        Assert.Equal("Saved: account 22202", page.SteamAccountStatus);

        page.SteamAccountText = "https://steamcommunity.com/id/someone";
        Assert.True(page.IsSteamAccountInvalid);
        Assert.Equal(22202, settings.Current.SteamAccountId);

        page.SteamAccountText = "";
        Assert.Null(settings.Current.SteamAccountId);
        Assert.False(page.IsSteamAccountInvalid);

        // An import can save it while the page is away; showing the page picks that up.
        settings.Update(s => s.SteamAccountId = 1010);
        page.Refresh();
        Assert.Equal("1010", page.SteamAccountText);
    }
}
