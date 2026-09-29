using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia.Input;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Contracts;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.Match.Board;

/// <summary>
/// Who's in the match. Detection normally fills it; the hero picker, hidden until opened, sets or
/// corrects it by hand. Click-to-assign: pick what you're assigning (Enemy / Ally / You), then click
/// heroes in the picker; clicking a hero who already has that role clears them. The fast path is
/// the keyboard: the search box keeps focus, so "gt" Enter puts Grey Talon on the current side and
/// clears the field for the next name.
/// </summary>
public class MatchBoardViewModel : ViewModelBase
{
    public const string FocusSearchAction = "FocusSearch";
    public const string RosterHint = "Click a portrait to change its role · × removes · an empty slot adds to that team";
    public const string NoSelfHint = "You're not set yet -- detect the match, or click an empty ally slot to pick your hero";

    public static readonly IReadOnlyList<Role> ModeOrder = [Role.Self, Role.Enemy, Role.Ally];

    private readonly MatchState _match;
    private readonly Func<DataStore> _store;
    private readonly Subject<Unit> _changed = new();
    private List<string> _visibleOrder = [];
    private int _highlightIndex;

    public MatchBoardViewModel(MatchState match, Func<DataStore> store, ISettingsService settings)
    {
        _match = match;
        _store = store;

        settings.SettingsChanged
            .Select(s => s.ShowRandomButtons)
            .DistinctUntilChanged()
            .Subscribe(show => ShowsRandom = show)
            .DisposeWith(Disposables);
        settings.SettingsChanged
            .Subscribe(s =>
            {
                DetectText = ShortcutKeys.WithKey("Detect from screen", s.Gesture(ShortcutAction.Detect));
                RandomTip = ShortcutKeys.WithKey("Fill the match with random heroes, for testing", s.Gesture(ShortcutAction.Randomize));
                RandomKeepSelfGesture = s.Gesture(ShortcutAction.RandomizeKeepSelf);
                RandomKeepTeamGesture = s.Gesture(ShortcutAction.RandomizeKeepTeam);
            })
            .DisposeWith(Disposables);

        SetModeCommand = ReactiveCommand.Create<Role>(SetMode);
        ClearCommand = ReactiveCommand.Create(() =>
        {
            _match.Clear();
            AfterChange();
        });
        RandomizeCommand = ReactiveCommand.Create<RandomizeKeep>(keep =>
        {
            _match.Randomize(_store().Heroes.Keys, Random.Shared, keep);
            AfterChange();
        }, this.WhenAnyValue(vm => vm.ShowsRandom));

        this.WhenAnyValue(vm => vm.SearchText)
            .Skip(1)
            .Subscribe(ApplyFilter)
            .DisposeWith(Disposables);
        this.WhenAnyValue(vm => vm.IsPickerOpen)
            .Where(open => !open)
            .Subscribe(_ => SearchText = "")
            .DisposeWith(Disposables);

        BuildTiles();
        SetMode(Role.Self);
        RefreshRosters();
    }

    /// <summary>The match composition changed: rescore.</summary>
    public IObservable<Unit> MatchChanged => _changed;

    public ObservableCollection<HeroTileViewModel> Tiles { get; } = [];

    [Reactive] public Role Mode { get; private set; }
    public bool IsEnemyMode => Mode == Role.Enemy;
    public bool IsAllyMode => Mode == Role.Ally;
    public bool IsSelfMode => Mode == Role.Self;

    /// <summary>Whether the hero picker is showing. It starts hidden, on the assumption detection gets the match right.</summary>
    [Reactive] public bool IsPickerOpen { get; set; }

    /// <summary>Whether the match bar offers Random, which a setting can hide; its keys go with it, so a stray one can't wipe a detected match.</summary>
    [Reactive] public bool ShowsRandom { get; private set; }

    /// <summary>The match bar's names for Detect and Random, with the keys Settings → Shortcuts has them on.</summary>
    [Reactive] public string DetectText { get; private set; } = "";
    [Reactive] public string RandomTip { get; private set; } = "";
    [Reactive] public KeyGesture? RandomKeepSelfGesture { get; private set; }
    [Reactive] public KeyGesture? RandomKeepTeamGesture { get; private set; }

    [Reactive] public string SearchText { get; set; } = "";
    [Reactive] public string SearchPlaceholder { get; private set; } = "";

    /// <summary>Your team's side of the match bar, in the game's top-bar order after a detection.</summary>
    public IReadOnlyList<RosterSlotViewModel> AllySlots { get; } = TeamSlots(Role.Ally);
    public IReadOnlyList<RosterSlotViewModel> EnemySlots { get; } = TeamSlots(Role.Enemy);
    [Reactive] public string AllyCount { get; private set; } = "";
    [Reactive] public string EnemyCount { get; private set; } = "";
    [Reactive] public string Hint { get; private set; } = NoSelfHint;

    /// <summary>Whether any net worth has been read this match, which makes room for it on the bar.</summary>
    [Reactive] public bool HasNetWorth { get; private set; }

    /// <summary>A team's summed net worth, once every hero on it has been read; empty until then.</summary>
    [Reactive] public string AllyNetWorth { get; private set; } = "";
    [Reactive] public string EnemyNetWorth { get; private set; } = "";

    /// <summary>How far your team is ahead ("+8.3%") or behind, as the game shows it under the totals.</summary>
    [Reactive] public string NetWorthLead { get; private set; } = "";
    [Reactive] public bool IsBehind { get; private set; }

    public ReactiveCommand<Role, Unit> SetModeCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearCommand { get; }
    public ReactiveCommand<RandomizeKeep, Unit> RandomizeCommand { get; }

    /// <summary>Screen detection, owned by the Match tab (its key or the button).</summary>
    public System.Windows.Input.ICommand? DetectCommand { get; set; }

    public void SetMode(Role role)
    {
        Mode = role;
        // Raised even when unchanged: clicking the already-checked toggle unchecks it on screen.
        this.RaisePropertyChanged(nameof(IsEnemyMode));
        this.RaisePropertyChanged(nameof(IsAllyMode));
        this.RaisePropertyChanged(nameof(IsSelfMode));
        SearchPlaceholder = role switch
        {
            Role.Enemy => "Type an enemy hero, then press Enter",
            Role.Ally => "Type an ally hero, then press Enter",
            _ => "Type your own hero, then press Enter",
        };
    }

    /// <summary>Show the picker with its search box focused, ready for a name.</summary>
    public void OpenPicker()
    {
        IsPickerOpen = true;
        RequestViewAction(FocusSearchAction);
    }

    /// <summary>Open the picker aimed at <paramref name="role"/>, as Alt+1/2/3 and the empty slots do.</summary>
    public void StartAssigning(Role role)
    {
        SetMode(role);
        OpenPicker();
    }

    /// <summary>Rebuild the palette for a reloaded store: heroes may have been added or renamed.</summary>
    public void Rebind()
    {
        BuildTiles();
        RefreshTiles();
        RefreshRosters();
    }

    /// <summary>Redraw after the match changed from outside the board, e.g. screen detection.</summary>
    public void Refresh()
    {
        RefreshTiles();
        RefreshRosters();
    }

    // -- interactions ---------------------------------------------------------

    public void TileClicked(string heroId)
    {
        _match.ToggleRole(heroId, Mode);
        AfterChange();
    }

    public void TileDoubleClicked(string heroId)
    {
        _match.SetRole(heroId, Role.Self);
        AfterChange();
    }

    public void SetRole(string heroId, Role role)
    {
        _match.SetRole(heroId, role);
        AfterChange();
    }

    public Role RoleOf(string heroId) => _match.RoleOf(heroId);
    public bool HasHero(string heroId) => _store().Heroes.ContainsKey(heroId);

    /// <summary>
    /// An empty slot on the match bar was clicked: open the picker aimed at that team. An empty ally
    /// slot means "You" until you're set.
    /// </summary>
    public void EmptySlotClicked(Role team) => StartAssigning(team == Role.Ally && _match.SelfHero is null ? Role.Self : team);

    // -- search / keyboard flow -------------------------------------------------

    public void AssignHighlighted()
    {
        if (_visibleOrder.Count == 0)
            return;
        _match.SetRole(_visibleOrder[_highlightIndex], Mode);
        // Ready for the next name.
        SearchText = "";
        AfterChange();
    }

    public void MoveHighlight(int delta)
    {
        if (_visibleOrder.Count == 0)
            return;
        _highlightIndex = ((_highlightIndex + delta) % _visibleOrder.Count + _visibleOrder.Count) % _visibleOrder.Count;
        RefreshHighlight();
    }

    /// <summary>The tiles stay where they are, but the pick goes best match first, so Enter takes it and Up/Down step down the ranking.</summary>
    private void ApplyFilter(string text)
    {
        _visibleOrder = FuzzyMatch.Filter(Tiles, text, tile => tile.HeroName).Select(tile => tile.HeroId).ToList();
        var shown = _visibleOrder.ToHashSet();
        foreach (var tile in Tiles)
            tile.IsShown = shown.Contains(tile.HeroId);
        _highlightIndex = 0;
        RefreshHighlight();
    }

    private void RefreshHighlight()
    {
        var highlighted = _visibleOrder.Count > 0 && SearchText.Trim().Length > 0 ? _visibleOrder[_highlightIndex] : null;
        foreach (var tile in Tiles)
            tile.IsHighlighted = tile.HeroId == highlighted;
    }

    // -- rendering ---------------------------------------------------------------

    private void BuildTiles()
    {
        Tiles.Clear();
        foreach (var hero in _store().HeroesSorted())
            Tiles.Add(new HeroTileViewModel(hero.HeroId, hero.HeroName));
        ApplyFilter(SearchText);
    }

    private void AfterChange()
    {
        RefreshTiles();
        RefreshRosters();
        _changed.OnNext(Unit.Default);
    }

    private void RefreshTiles()
    {
        foreach (var tile in Tiles)
            tile.Role = _match.RoleOf(tile.HeroId);
    }

    private void RefreshRosters()
    {
        var allies = _match.OwnTeam;
        var enemies = _match.Enemies;

        AllyCount = $"{allies.Count}/{MatchState.TeamSize}";
        EnemyCount = $"{enemies.Count}/{MatchState.TeamSize}";
        HasNetWorth = !_match.NetWorth.IsEmpty;
        FillSlots(AllySlots, allies);
        FillSlots(EnemySlots, enemies);
        Hint = _match.SelfHero is null ? NoSelfHint : RosterHint;
        RefreshTotals(allies, enemies);
    }

    /// <summary>The team in order from the first slot, any past the sixth left off the bar.</summary>
    private void FillSlots(IReadOnlyList<RosterSlotViewModel> slots, IEnumerable<string> heroIds)
    {
        var heroes = _store().Heroes;
        var shown = heroIds.Where(heroes.ContainsKey).ToList();
        for (var index = 0; index < slots.Count; index++)
        {
            if (index < shown.Count)
            {
                var heroId = shown[index];
                slots[index].Fill(heroId, heroes[heroId].HeroName, heroId == _match.SelfHero,
                    _match.NetWorth.Latest(heroId), ChangeText(_match.NetWorth.Change(heroId)));
            }
            else
            {
                slots[index].Clear();
            }
            slots[index].ShowsNetWorth = HasNetWorth;
        }
    }

    private void RefreshTotals(IReadOnlyList<string> allies, IReadOnlyList<string> enemies)
    {
        var ours = TeamTotal(allies);
        var theirs = TeamTotal(enemies);
        AllyNetWorth = ours is { } a ? Format.Compact(a) : "";
        EnemyNetWorth = theirs is { } e ? Format.Compact(e) : "";
        if (ours is { } own && theirs is { } other && other > 0)
        {
            var lead = (own - other) * 100.0 / other;
            NetWorthLead = FormattableString.Invariant($"{lead:+0.0;-0.0;0.0}%");
            IsBehind = lead < 0;
        }
        else
        {
            NetWorthLead = "";
            IsBehind = false;
        }
    }

    /// <summary>A full team's summed net worth; null while anyone on it is unread or the team isn't full.</summary>
    private int? TeamTotal(IReadOnlyList<string> heroIds)
    {
        if (heroIds.Count != MatchState.TeamSize)
            return null;
        var total = 0;
        foreach (var heroId in heroIds)
        {
            if (_match.NetWorth.Latest(heroId) is not { } souls)
                return null;
            total += souls;
        }
        return total;
    }

    private static string? ChangeText((int Souls, TimeSpan Over)? change)
    {
        if (change is not { } moved)
            return null;
        var (souls, over) = moved;
        var minutes = Math.Max(1, (int)Math.Round(over.TotalMinutes));
        return $"{(souls < 0 ? "−" : "+")}{Format.Compact(Math.Abs(souls))} in {minutes} min";
    }

    private static List<RosterSlotViewModel> TeamSlots(Role team) =>
        Enumerable.Range(0, MatchState.TeamSize).Select(_ => new RosterSlotViewModel(team)).ToList();
}
