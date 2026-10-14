using System.Reactive;
using System.Reactive.Linq;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Contracts;
using DeadlockAdvisor.Services.Formats;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.ItemFormulas.ByItem;

/// <summary>How the item's lean is decided: by its stats, or fixed to one of three by hand.</summary>
public enum LeanChoice
{
    Auto,
    Bullet,
    Neutral,
    Spirit,
}

/// <summary>
/// "What does this item push its holder's damage toward?" for the item open on the By Item page: what its stats work out to and which
/// stats voted, and a way to fix it by hand, with the reason, where the stats don't say (<see cref="DataStore.SetItemLean"/>).
/// </summary>
public sealed class ItemLeanViewModel : ViewModelBase
{
    public const string SectionTip =
        "What this item pushes its holder's damage toward, from the stats it has and the debuffs it puts on enemies (data/stat_leans.csv).\n"
        + "A hero who has built mostly items that push toward bullet damage counts as more of a bullet-damage hero in a match, and the items that\n"
        + "answer that follow. Fix it by hand where the stats don't say what the item does.";

    public const string ReasonTip = "Why the stats are wrong about this item: it's kept with the override, so the next person knows.";

    private readonly IDataService _data;
    private readonly Action<string> _changed;
    private string? _itemId;
    private bool _showing;

    public ItemLeanViewModel(IDataService data, Action<string> changed)
    {
        _data = data;
        _changed = changed;
        ChooseCommand = ReactiveCommand.Create<LeanChoice>(Choose);
        this.WhenAnyValue(vm => vm.Choice).Subscribe(_ =>
        {
            this.RaisePropertyChanged(nameof(IsAuto));
            this.RaisePropertyChanged(nameof(IsBullet));
            this.RaisePropertyChanged(nameof(IsNeutral));
            this.RaisePropertyChanged(nameof(IsSpirit));
        });
        this.WhenAnyValue(vm => vm.Reason).Skip(1).Subscribe(_ => ReasonEdited());
    }

    [Reactive] public bool HasItem { get; private set; }

    /// <summary>"+0.50 toward bullet damage".</summary>
    [Reactive] public string Summary { get; private set; } = "";

    /// <summary>Where it comes from: its stats, its shop, or a hand-set value (and what the stats say).</summary>
    [Reactive] public string Source { get; private set; } = "";

    /// <summary>One line per stat or debuff that voted, "Fire Rate +0.50".</summary>
    [Reactive] public IReadOnlyList<string> Votes { get; private set; } = [];

    [Reactive] public LeanChoice? Choice { get; private set; }
    [Reactive] public string Reason { get; set; } = "";

    /// <summary>A choice was refused: nothing says why the stats are wrong.</summary>
    [Reactive] public string? Hint { get; private set; }

    public bool IsAuto => Choice == LeanChoice.Auto;
    public bool IsBullet => Choice == LeanChoice.Bullet;
    public bool IsNeutral => Choice == LeanChoice.Neutral;
    public bool IsSpirit => Choice == LeanChoice.Spirit;

    public ReactiveCommand<LeanChoice, Unit> ChooseCommand { get; }

    /// <summary>Show this item's lean; none for no item.</summary>
    public void Show(Item? item)
    {
        _itemId = item?.ItemId;
        HasItem = item is not null;
        Hint = null;
        if (item is null)
            return;

        var store = _data.Store;
        var lean = store.LeanOf(item.ItemId);
        _showing = true;
        Summary = $"{Format.SignedFixed(lean.Lean, 2)} {Direction(lean.Lean)}";
        Votes = lean.Source == LeanSource.Shop
            ? []
            : (lean.Source == LeanSource.Override ? store.DerivedLeanOf(item.ItemId) : lean).Votes
                .Select(vote => $"{vote.Label} {Format.SignedFixed(vote.Vote, 2)}")
                .ToList();
        Source = lean.Source switch
        {
            LeanSource.Derived => "from its stats",
            LeanSource.Shop => $"from its shop ({item.Category}): no stat of its has a vote in stat_leans.csv",
            _ => $"set by hand; its stats say {Format.SignedFixed(store.DerivedLeanOf(item.ItemId).Lean, 2)}",
        };
        Choice = lean.Source != LeanSource.Override ? LeanChoice.Auto : lean.Lean switch
        {
            1.0 => LeanChoice.Bullet,
            0.0 => LeanChoice.Neutral,
            -1.0 => LeanChoice.Spirit,
            _ => null,
        };
        Reason = store.ItemLeanOverrides.TryGetValue(item.ItemId, out var fixedLean) ? fixedLean.Reason : "";
        _showing = false;
    }

    private static string Direction(double lean) => lean switch
    {
        > 0.05 => "toward bullet damage",
        < -0.05 => "toward spirit damage",
        _ => "neither way",
    };

    private void Choose(LeanChoice choice)
    {
        if (_itemId is not { } itemId)
            return;
        var store = _data.Store;
        if (choice == LeanChoice.Auto)
        {
            if (store.ClearItemLean(itemId))
                Saved(itemId);
            return;
        }
        if (Reason.Trim().Length == 0)
        {
            Hint = "Write why the stats are wrong about this item first.";
            return;
        }
        var lean = choice switch { LeanChoice.Bullet => 1.0, LeanChoice.Spirit => -1.0, _ => 0.0 };
        if (store.SetItemLean(itemId, lean, Reason))
            Saved(itemId);
    }

    /// <summary>Edit the reason of a lean fixed by hand; a reason typed before choosing waits for the choice.</summary>
    private void ReasonEdited()
    {
        Hint = null;
        if (_showing || _itemId is not { } itemId || !_data.Store.ItemLeanOverrides.TryGetValue(itemId, out var fixedLean))
            return;
        // Typing doesn't re-show the item: that would trim the space just typed.
        if (_data.Store.SetItemLean(itemId, fixedLean.Lean, Reason))
            _data.MarkEdited(DataFiles.ItemLeanOverrides);
    }

    private void Saved(string itemId)
    {
        _data.MarkEdited(DataFiles.ItemLeanOverrides);
        _changed(itemId);
        Show(_data.Store.Items.GetValueOrDefault(itemId));
    }
}
