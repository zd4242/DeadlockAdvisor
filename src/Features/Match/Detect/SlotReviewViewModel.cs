using System.Reactive;
using System.Reactive.Disposables;
using Avalonia.Media.Imaging;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Vision;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.Match.Detect;

/// <summary>A hero in the correction dropdown; the empty id is "not detected".</summary>
public sealed record HeroChoice(string? HeroId, string Name)
{
    public const string UnknownName = "— not detected —";

    public static HeroChoice Unknown { get; } = new(null, UnknownName);

    public override string ToString() => Name;
}

public enum SlotRole
{
    Unknown,
    You,
    Ally,
    Enemy,
}

/// <summary>One slot of the review: its crop, what it was read as, and a way to say otherwise.</summary>
public class SlotReviewViewModel : ViewModelBase
{
    public SlotReviewViewModel(SlotReading reading, RgbImage? crop, IReadOnlyList<HeroChoice> choices, Action<int> setSelf,
        int? souls = null)
    {
        Reading = reading;
        Crop = crop;
        Choices = choices;
        NetWorth = souls is { } value ? Format.Compact(value) : "";
        Thumbnail = crop is null ? null : RgbImageBitmap.ToBitmap(crop);
        var detected = choices.FirstOrDefault(choice => choice.HeroId is not null && choice.HeroId == reading.HeroId);
        SelectedHero = detected ?? HeroChoice.Unknown;
        SetSelfCommand = ReactiveCommand.Create(() => setSelf(reading.Index));

        this.WhenAnyValue(vm => vm.SelectedHero)
            .Subscribe(_ =>
            {
                Detail = !WasCorrected ? DetailFor(reading, NameOf(reading.RunnerUp))
                    : detected is null ? "set by hand"
                    : $"corrected from {detected.Name}";
                this.RaisePropertyChanged(nameof(WasCorrected));
                this.RaisePropertyChanged(nameof(IsUncertain));
            })
            .DisposeWith(Disposables);
    }

    public SlotReading Reading { get; }
    public int Index => Reading.Index;

    /// <summary>The slot as captured, kept to save as reference art if the hero is corrected.</summary>
    public RgbImage? Crop { get; }

    public Bitmap? Thumbnail { get; }
    public IReadOnlyList<HeroChoice> Choices { get; }

    /// <summary>The net worth read under the portrait, as the game prints it; empty when it wasn't read.</summary>
    public string NetWorth { get; }
    [Reactive] public HeroChoice SelectedHero { get; set; }
    [Reactive] public string Detail { get; private set; } = "";

    /// <summary>Worth a second look: nothing read, or a read without a clear lead, and not yet corrected.</summary>
    public bool IsUncertain => !Reading.IsConfident && !WasCorrected;

    [Reactive] public SlotRole Role { get; private set; }
    [Reactive] public string RoleText { get; private set; } = "";
    public bool IsYou => Role == SlotRole.You;

    public ReactiveCommand<Unit, Unit> SetSelfCommand { get; }

    public string? HeroId => SelectedHero.HeroId;

    public bool WasCorrected => HeroId is not null && HeroId != Reading.HeroId;

    public void SetRole(SlotRole role)
    {
        Role = role;
        RoleText = role switch
        {
            SlotRole.You => "YOU",
            SlotRole.Ally => "ally",
            SlotRole.Enemy => "enemy",
            _ => "team unknown",
        };
        this.RaisePropertyChanged(nameof(IsYou));
    }

    private string? NameOf(string? heroId) =>
        heroId is null ? null : Choices.FirstOrDefault(choice => choice.HeroId == heroId)?.Name ?? heroId;

    private static string DetailFor(SlotReading reading, string? runnerUp)
    {
        if (reading.HeroId is null)
            return "No confident match — likely dead at capture, or wearing a skin.";
        if (reading.IsConfident)
            return FormattableString.Invariant($"match {reading.Score:0.00}  ·  clear by {reading.Margin:0.00}");
        return FormattableString.Invariant($"uncertain: {reading.Score:0.00}, only {reading.Margin:0.00} ahead of {runnerUp ?? "—"}");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            Thumbnail?.Dispose();
        base.Dispose(disposing);
    }
}
