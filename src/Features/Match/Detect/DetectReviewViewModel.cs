using System.Reactive;
using System.Reactive.Linq;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Vision;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.Match.Detect;

/// <param name="SlotHeroes">The hero in each of the twelve slots, left to right, or null where none was read.</param>
/// <param name="Corrections">Crops whose hero was corrected, to keep as reference art.</param>
public sealed record DetectReviewResult(IReadOnlyList<string?> SlotHeroes, int SelfSlot, IReadOnlyList<(string HeroId, RgbImage Crop)> Corrections);

/// <summary>
/// What was read off the screen, shown before anything lands: a dead player is a black silhouette
/// and a hero in a skin won't look like the reference art. Correcting a slot can also keep its crop
/// as reference art, which is how the matcher learns alternate portraits. Nothing applies until
/// you're known, since that's what splits the teams; a You button on each slot covers the captures
/// where the backplate couldn't be read.
/// </summary>
public class DetectReviewViewModel : ViewModelBase
{
    private readonly Detection _detection;

    public DetectReviewViewModel(Detection detection, IReadOnlyList<HeroChoice> heroes, Action<DetectReviewResult> apply, Action cancel)
    {
        _detection = detection;
        IReadOnlyList<HeroChoice> choices = [HeroChoice.Unknown, .. heroes];
        Slots = detection.Slots
            .Select(reading => new SlotReviewViewModel(reading, detection.Image is { } image ? Layout.Crop(image, reading.Box) : null, choices, ToggleSelf))
            .ToList();
        SelfSlot = detection.SelfSlot;
        Relabel();

        ApplyCommand = ReactiveCommand.Create(() => apply(Result()), this.WhenAnyValue(vm => vm.SelfSlot).Select(self => self is not null));
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
    [Reactive] public bool RememberCorrections { get; set; } = true;

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
        var lane = view.LaneSlots;
        var allies = view.AllySlots;
        var enemies = view.EnemySlots;
        foreach (var slot in Slots)
        {
            var index = slot.Index;
            slot.SetRole(index == SelfSlot ? SlotRole.You
                : lane.Contains(index) ? allies.Contains(index) ? SlotRole.LaneAlly : SlotRole.LaneEnemy
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
        Summary = SelfSlot is null
            ? $"Read {confident} of 12 heroes, but couldn't tell which one is you, so the teams can't be split. "
              + "Press You on your own slot to apply."
            : $"Read {confident} of 12 heroes confidently. Check anything marked uncertain before applying — "
              + "a slot whose player was dead at the moment of capture can't be identified from the portrait.";
    }

    private DetectReviewResult Result()
    {
        var corrections = RememberCorrections
            ? Slots.Where(slot => slot.WasCorrected && slot.Crop is not null).Select(slot => (slot.HeroId!, slot.Crop!)).ToList()
            : [];
        return new DetectReviewResult(Slots.OrderBy(slot => slot.Index).Select(slot => slot.HeroId).ToList(), SelfSlot!.Value, corrections);
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
