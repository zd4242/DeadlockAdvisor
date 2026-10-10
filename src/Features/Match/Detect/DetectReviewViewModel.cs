using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Vision;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.Match.Detect;

/// <param name="SlotHeroes">The hero in each of the twelve slots, left to right, or null where none was read.</param>
/// <param name="SelfSlot">Which slot is you; null when the heroes are known but not whose they are, which the match page asks for.</param>
/// <param name="Corrections">Crops whose hero was corrected, to keep as reference art.</param>
/// <param name="SlotSouls">Each slot's net worth, where it was read and its side added up.</param>
/// <param name="CorrectedSlots">Every slot whose hero was corrected, whether or not the crops are kept.</param>
/// <param name="TooFaded">Corrections not kept as reference art because the portrait was too faded to learn from.</param>
/// <param name="LikelySelfSlot">With no <paramref name="SelfSlot"/>, the slot that might be you.</param>
public sealed record DetectReviewResult(IReadOnlyList<string?> SlotHeroes, int? SelfSlot,
    IReadOnlyList<(string HeroId, RgbImage Crop)> Corrections, IReadOnlyList<int?> SlotSouls, IReadOnlyList<int> CorrectedSlots,
    int TooFaded = 0, int? LikelySelfSlot = null);

/// <summary>
/// What was read off the screen, shown before anything lands: a dead player is a black silhouette
/// and a hero in a skin won't look like the reference art. Correcting a slot can also keep its crop
/// as reference art, which is how the matcher learns alternate portraits. Nothing applies until
/// you're known, since that's what splits the teams; each slot's portrait and "This is me" button
/// cover the captures where the backplate couldn't be read.
/// </summary>
public class DetectReviewViewModel : ViewModelBase
{
    private readonly Detection _detection;
    private readonly NetWorthReading _netWorth;

    public DetectReviewViewModel(Detection detection, NetWorthReading netWorth, IReadOnlyList<HeroChoice> heroes,
        Func<DetectReviewResult, Task> apply, Action cancel)
    {
        _detection = detection;
        _netWorth = netWorth;
        IReadOnlyList<HeroChoice> choices = [HeroChoice.Unknown, .. heroes];
        var souls = netWorth.Souls;
        Slots = detection.Slots
            .Select(reading => new SlotReviewViewModel(reading, detection.CropOf(reading.Index), choices, ToggleSelf,
                reading.Index < souls.Count ? souls[reading.Index] : null))
            .ToList();
        SelfSlot = detection.SelfSlot;
        Relabel();
        Slots.Select(slot => slot.WhenAnyValue(s => s.SelectedHero))
            .Merge()
            .Subscribe(_ => HasCorrections = Slots.Any(slot => slot.WasCorrected))
            .DisposeWith(Disposables);

        ApplyCommand = ReactiveCommand.CreateFromTask(() => apply(Result()), this.WhenAnyValue(vm => vm.SelfSlot).Select(self => self is not null));
        CancelCommand = ReactiveCommand.Create(cancel);
    }

    public IReadOnlyList<SlotReviewViewModel> Slots { get; }

    [Reactive] public int? SelfSlot { get; private set; }
    public bool HasSelf => SelfSlot is not null;
    [Reactive] public IReadOnlyList<SlotReviewViewModel> OwnRows { get; private set; } = [];
    [Reactive] public IReadOnlyList<SlotReviewViewModel> FoeRows { get; private set; } = [];
    [Reactive] public string OwnHeading { get; private set; } = "";
    [Reactive] public string FoeHeading { get; private set; } = "";
    [Reactive] public string Summary { get; private set; } = "";
    /// <summary>Starts from the setting, and changing it here changes the setting too.</summary>
    [Reactive] public bool RememberCorrections { get; set; } = true;

    /// <summary>A slot has been corrected, so there's something to remember.</summary>
    [Reactive] public bool HasCorrections { get; private set; }

    public ReactiveCommand<Unit, Unit> ApplyCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    /// <summary>Make a slot you, or clear it if it already is.</summary>
    public void ToggleSelf(int slot)
    {
        SelfSlot = SelfSlot == slot ? null : slot;
        Relabel();
    }

    private void Relabel()
    {
        var view = _detection with { SelfSlot = SelfSlot };
        var allies = view.AllySlots;
        var enemies = view.EnemySlots;
        foreach (var slot in Slots)
        {
            var index = slot.Index;
            slot.SetRole(index == SelfSlot ? SlotRole.You
                : allies.Contains(index) ? SlotRole.Ally
                : enemies.Contains(index) ? SlotRole.Enemy
                : SlotRole.Unknown);
        }

        var ownBase = SelfSlot >= Layout.PerTeam ? Layout.PerTeam : 0;
        OwnRows = Slots.Where(slot => slot.Index >= ownBase && slot.Index < ownBase + Layout.PerTeam).ToList();
        FoeRows = Slots.Except(OwnRows).ToList();
        (OwnHeading, FoeHeading) = SelfSlot is null ? ("LEFT SIDE", "RIGHT SIDE") : ("YOUR TEAM", "ENEMY TEAM");
        this.RaisePropertyChanged(nameof(HasSelf));

        var confident = _detection.ConfidentCount;
        var kept = _detection.Slots.Count(slot => slot.Kept);
        var keptText = kept > 0 ? $" (and kept {kept} from your current match)" : "";
        Summary = (SelfSlot is null
            ? $"Read {confident} of 12 heroes{keptText}, but couldn't tell which one is you, so the teams can't be split."
            : $"Read {confident} of 12 heroes confidently{keptText}. Check anything marked Unsure before applying — "
              + "a slot whose player was dead at the moment of capture can't be identified from the portrait.")
            + $" Net worth read for {_netWorth.ReadCount} of 12.";
    }

    private DetectReviewResult Result()
    {
        var corrected = RememberCorrections ? Slots.Where(slot => slot.WasCorrected && slot.Crop is not null).ToList() : [];
        var learnable = corrected.Where(slot => TemplateBank.CanLearnFrom(slot.Crop!)).ToList();
        return new DetectReviewResult(Slots.OrderBy(slot => slot.Index).Select(slot => slot.HeroId).ToList(), SelfSlot!.Value,
            learnable.Select(slot => (slot.HeroId!, slot.Crop!)).ToList(), _netWorth.Souls,
            Slots.Where(slot => slot.WasCorrected).Select(slot => slot.Index).ToList(), corrected.Count - learnable.Count);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var slot in Slots)
                slot.Dispose();
        }
        base.Dispose(disposing);
    }
}
