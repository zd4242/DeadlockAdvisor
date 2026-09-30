using System.Globalization;
using System.Net.Http;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Contracts;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.Match.Import;

/// <param name="Roles">Your team in lobby order, you in your place, then the enemies.</param>
/// <param name="RememberAccount">An account to save as yours, if asked to.</param>
public sealed record ImportMatchResult(OrderedDictionary<string, Role> Roles, long? RememberAccount);

/// <summary>
/// Import a finished match by its ID: look it up, say which player you were (or let a saved Steam
/// account say it), and apply. Nothing lands until you're known, since that's what splits the teams.
/// </summary>
public class ImportMatchViewModel : ViewModelBase
{
    public const string SyncHint = "Data → Sync New Heroes / Items / Categories adds them.";

    private readonly IMatchLookupService _lookup;
    private readonly IReadOnlyDictionary<long, Hero> _heroes;
    private readonly long? _savedAccount;
    private readonly ILoggingService _log;
    private readonly CancellationTokenSource _closed = new();
    private LookedUpMatch? _match;

    /// <param name="heroesByGameId">The heroes this data has, by deadlock-api.com's id.</param>
    /// <param name="savedAccount">Your Steam account, from Settings, which picks you out of the match.</param>
    public ImportMatchViewModel(IMatchLookupService lookup, IReadOnlyDictionary<long, Hero> heroesByGameId, long? savedAccount, ILoggingService log,
        Action<ImportMatchResult> apply, Action cancel)
    {
        _lookup = lookup;
        _heroes = heroesByGameId;
        _savedAccount = savedAccount;
        _log = log;

        var canLookUp = this.WhenAnyValue(vm => vm.MatchIdText).Select(text => MatchLookupService.TryParseMatchId(text, out _));
        LookUpCommand = ReactiveCommand.CreateFromTask(LookUpAsync, canLookUp);
        ApplyCommand = ReactiveCommand.Create(() => apply(Result()), this.WhenAnyValue(vm => vm.Self).Select(self => self is not null));
        CancelCommand = ReactiveCommand.Create(cancel);
    }

    [Reactive] public string MatchIdText { get; set; } = "";
    [Reactive] public bool IsLookingUp { get; private set; }
    [Reactive] public string? Error { get; private set; }

    [Reactive] public IReadOnlyList<ImportPlayerViewModel> Players { get; private set; } = [];
    public bool HasMatch => Players.Count > 0;
    [Reactive] public ImportPlayerViewModel? Self { get; private set; }
    public bool HasSelf => Self is not null;
    /// <summary>The match is shown, but can't be applied until you're picked out of it.</summary>
    public bool NeedsSelf => HasMatch && !HasSelf;

    [Reactive] public IReadOnlyList<ImportPlayerViewModel> OwnRows { get; private set; } = [];
    [Reactive] public IReadOnlyList<ImportPlayerViewModel> FoeRows { get; private set; } = [];
    [Reactive] public string OwnHeading { get; private set; } = "";
    [Reactive] public string FoeHeading { get; private set; } = "";

    /// <summary>When the match was played, how long it went, and who won.</summary>
    [Reactive] public string Summary { get; private set; } = "";

    /// <summary>How you were picked out, when it was automatic.</summary>
    [Reactive] public string Prompt { get; private set; } = "";

    /// <summary>Heroes the match has that this data doesn't, which are left out.</summary>
    [Reactive] public string Warning { get; private set; } = "";

    /// <summary>Offered when you're a player other than the saved account: save this one as yours.</summary>
    [Reactive] public bool ShowsRememberMe { get; private set; }
    [Reactive] public bool RememberMe { get; set; }
    [Reactive] public string RememberText { get; private set; } = "";

    public ReactiveCommand<Unit, Unit> LookUpCommand { get; }
    public ReactiveCommand<Unit, Unit> ApplyCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    public static string TeamName(int team) => team switch
    {
        0 => "The Hidden King",
        1 => "The Archmother",
        _ => $"Team {team}",
    };

    /// <summary>Enter in the ID box: look the match up, or apply it once it's the one shown and you're known.</summary>
    public void Submit()
    {
        if (IsLookingUp || !MatchLookupService.TryParseMatchId(MatchIdText, out var matchId))
            return;
        var command = matchId == _match?.MatchId && Self is not null ? ApplyCommand : LookUpCommand;
        command.Execute().Subscribe();
    }

    /// <summary>Make a player you, or clear it if they already are.</summary>
    public void ToggleSelf(ImportPlayerViewModel player)
    {
        Self = Self == player ? null : player;
        Relabel();
    }

    private async Task LookUpAsync()
    {
        if (!MatchLookupService.TryParseMatchId(MatchIdText, out var matchId))
            return;
        var token = _closed.Token;
        Error = null;
        IsLookingUp = true;
        LookedUpMatch match;
        try
        {
            match = await _lookup.LookUpAsync(matchId, token);
        }
        catch (MatchLookupException ex)
        {
            _log.Warning($"Import: match {matchId}: {ex.Message}");
            Error = ex.Message;
            Show(null);
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            IsLookingUp = false;
        }

        Show(match);
        await LoadNamesAsync(token);
    }

    private void Show(LookedUpMatch? match)
    {
        foreach (var player in Players)
            player.Dispose();
        _match = match;
        Players = match?.Players.Select(player => new ImportPlayerViewModel(player, _heroes.GetValueOrDefault(player.HeroGameId), ToggleSelf)).ToList() ?? [];
        Self = _savedAccount is { } mine ? Players.FirstOrDefault(player => player.AccountId == mine) : null;
        RememberMe = _savedAccount is null;
        var unknown = Players.Count(player => player.HeroId is null);
        Warning = unknown == 0 ? ""
            : $"{unknown} of these heroes {(unknown == 1 ? "isn't" : "aren't")} in your data yet and will be left out. {SyncHint}";
        this.RaisePropertyChanged(nameof(HasMatch));
        Relabel();
    }

    private async Task LoadNamesAsync(CancellationToken token)
    {
        try
        {
            var names = await _lookup.SteamNamesAsync(Players.Select(player => player.AccountId), token);
            foreach (var player in Players)
            {
                if (names.TryGetValue(player.AccountId, out var name))
                    player.PlayerName = name;
            }
            RefreshRemember();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or JsonException)
        {
            // The names are a nicety: the match works without them.
            _log.Warning($"Import: couldn't fetch Steam names: {ex.Message}");
        }
    }

    private void Relabel()
    {
        var ownTeam = Self?.Team ?? 0;
        foreach (var player in Players)
        {
            player.SetRole(Self is null ? Role.None
                : player == Self ? Role.Self
                : player.Team == ownTeam ? Role.Ally
                : Role.Enemy);
        }
        OwnRows = Players.Where(player => player.Team == ownTeam).ToList();
        FoeRows = Players.Where(player => player.Team != ownTeam).ToList();
        var foeTeam = FoeRows.FirstOrDefault()?.Team ?? 1 - ownTeam;
        (OwnHeading, FoeHeading) = Self is null
            ? (TeamName(ownTeam).ToUpperInvariant(), TeamName(foeTeam).ToUpperInvariant())
            : ("YOUR TEAM", "ENEMY TEAM");
        this.RaisePropertyChanged(nameof(HasSelf));
        this.RaisePropertyChanged(nameof(NeedsSelf));

        Summary = SummaryText();
        Prompt = Self is not null && Self.AccountId == _savedAccount ? "You were picked out by your saved Steam account." : "";
        RefreshRemember();
    }

    private void RefreshRemember()
    {
        ShowsRememberMe = Self is { AccountId: > 0 } self && self.AccountId != _savedAccount;
        var who = Self is { PlayerName.Length: > 0 } named ? named.PlayerName : "this player";
        RememberText = $"Remember {who} as my Steam account, to pick me out of the next match";
    }

    private string SummaryText()
    {
        if (_match is not { } match)
            return "";
        var parts = new List<string> { $"Match {match.MatchId.ToString(CultureInfo.InvariantCulture)}" };
        if (match.StartedAt is { } started)
            parts.Add("played " + started.ToLocalTime().ToString("ddd d MMM yyyy, HH:mm", CultureInfo.CurrentCulture));
        if (match.Duration is { } duration)
            parts.Add($"{(int)Math.Round(duration.TotalMinutes)} min");
        if (match.WinningTeam is { } winner)
        {
            parts.Add(Self is null ? $"{TeamName(winner)} won"
                : Self.Team == winner ? "your team won"
                : "your team lost");
        }
        return string.Join(" · ", parts);
    }

    private ImportMatchResult Result()
    {
        var roles = new OrderedDictionary<string, Role>();
        foreach (var player in OwnRows.Concat(FoeRows))
        {
            if (player.HeroId is { } heroId)
                roles[heroId] = player.Role;
        }
        return new ImportMatchResult(roles, ShowsRememberMe && RememberMe ? Self!.AccountId : null);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _closed.Cancel();
            _closed.Dispose();
            foreach (var player in Players)
                player.Dispose();
        }
        base.Dispose(disposing);
    }
}
