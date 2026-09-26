using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using DeadlockAdvisor.Controls;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.Match.Board;

/// <summary>
/// Who's in the match. Click-to-assign: pick what you're assigning (Enemy / Ally / You), then click
/// heroes in the palette; clicking a hero who already has that role clears them. The fast path is
/// the keyboard: the search box keeps focus, so "hay" Enter puts Haze on the current side and
/// clears the field for the next name.
/// </summary>
public class MatchBoardViewModel : ViewModelBase
{
    public const string FocusSearchAction = "FocusSearch";
    public const string RosterHint = "Click a portrait to toggle lane · × removes · an empty slot picks that team";
    public const string NoSelfHint = "You're not set yet -- double click your hero below, or click an empty ally slot";

    public static readonly IReadOnlyList<Role> ModeOrder = [Role.Enemy, Role.Ally, Role.Self];

    private readonly MatchState _match;
    private readonly Func<DataStore> _store;
    private readonly Subject<Unit> _changed = new();
    private List<string> _visibleOrder = [];
    private int _highlightIndex;

    public MatchBoardViewModel(MatchState match, Func<DataStore> store)
    {
        _match = match;
        _store = store;

        SetModeCommand = ReactiveCommand.Create<Role>(SetMode);
        ClearCommand = ReactiveCommand.Create(() =>
        {
            _match.Clear();
            AfterChange();
        });
        RandomizeCommand = ReactiveCommand.Create(() =>
        {
            _match.Randomize(_store().Heroes.Keys, Random.Shared);
            AfterChange();
        });

        this.WhenAnyValue(vm => vm.SearchText)
            .Skip(1)
            .Subscribe(ApplyFilter)
            .DisposeWith(Disposables);

        BuildTiles();
        SetMode(Role.Enemy);
        RefreshRosters();
    }

    /// <summary>The match composition or a lane flag changed: rescore.</summary>
    public IObservable<Unit> MatchChanged => _changed;

    public ObservableCollection<HeroTileViewModel> Tiles { get; } = [];

    [Reactive] public Role Mode { get; private set; }
    public bool IsEnemyMode => Mode == Role.Enemy;
    public bool IsAllyMode => Mode == Role.Ally;
    public bool IsSelfMode => Mode == Role.Self;

    [Reactive] public string SearchText { get; set; } = "";
    [Reactive] public string SearchPlaceholder { get; private set; } = "";

    [Reactive] public IReadOnlyList<SlotEntry> AllySlots { get; private set; } = [];
    [Reactive] public IReadOnlyList<SlotEntry> EnemySlots { get; private set; } = [];
    [Reactive] public string AllyCount { get; private set; } = "";
    [Reactive] public string EnemyCount { get; private set; } = "";
    [Reactive] public string Hint { get; private set; } = NoSelfHint;

    public ReactiveCommand<Role, Unit> SetModeCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearCommand { get; }
    public ReactiveCommand<Unit, Unit> RandomizeCommand { get; }

    /// <summary>Screen detection, owned by the Match tab (F9 or the button).</summary>
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

    public void ToggleLane(string heroId)
    {
        _match.ToggleLane(heroId);
        AfterChange();
    }

    public Role RoleOf(string heroId) => _match.RoleOf(heroId);
    public bool IsInLane(string heroId) => _match.IsInLane(heroId);
    public bool HasHero(string heroId) => _store().Heroes.ContainsKey(heroId);

    /// <summary>An empty ally slot means "You" until you're set.</summary>
    public void AllySlotClicked() => StartFilling(_match.SelfHero is null ? Role.Self : Role.Ally);

    public void EnemySlotClicked() => StartFilling(Role.Enemy);

    /// <summary>An empty roster slot was clicked: aim the palette at that team.</summary>
    private void StartFilling(Role role)
    {
        SetMode(role);
        RequestViewAction(FocusSearchAction);
    }

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

    private void ApplyFilter(string text)
    {
        var needle = text.Trim().ToLowerInvariant();
        _visibleOrder = [];
        foreach (var tile in Tiles)
        {
            var match = needle.Length == 0
                        || tile.HeroName.ToLowerInvariant().Contains(needle, StringComparison.Ordinal)
                        || tile.HeroId.ToLowerInvariant().Contains(needle, StringComparison.Ordinal);
            tile.IsShown = match;
            if (match)
                _visibleOrder.Add(tile.HeroId);
        }
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
        {
            tile.Role = _match.RoleOf(tile.HeroId);
            tile.InLane = _match.IsInLane(tile.HeroId);
        }
    }

    private void RefreshRosters()
    {
        var self = _match.SelfHero;
        var allies = (self is null ? [] : new List<string> { self }).Concat(_match.Allies).ToList();
        var enemies = _match.Enemies;

        AllyCount = $"{allies.Count}/{MatchState.MaxAllies + 1}";
        EnemyCount = $"{enemies.Count}/{MatchState.MaxEnemies}";
        AllySlots = SlotEntries(allies);
        EnemySlots = SlotEntries(enemies);
        Hint = self is null ? NoSelfHint : RosterHint;
    }

    private List<SlotEntry> SlotEntries(IEnumerable<string> heroIds)
    {
        var heroes = _store().Heroes;
        return heroIds
            .Where(heroes.ContainsKey)
            .Select(heroId => new SlotEntry(heroId, heroes[heroId].HeroName, heroId == _match.SelfHero, _match.IsInLane(heroId)))
            .ToList();
    }
}
