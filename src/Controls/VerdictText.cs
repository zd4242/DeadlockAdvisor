using Avalonia.Media;
using DeadlockAdvisor.Features.Match.Explain;
using DeadlockAdvisor.Theme;

namespace DeadlockAdvisor.Controls;

/// <summary>"Formula ▲8.0 · data ▼1.0": a <see cref="BlendVerdict"/>'s parts, each opinion named in the colour of its bar.</summary>
public class VerdictText : AmountLine
{
    public static readonly StyledProperty<BlendVerdict?> VerdictProperty =
        AvaloniaProperty.Register<VerdictText, BlendVerdict?>(nameof(Verdict));

    private static readonly IBrush _formula = new SolidColorBrush(Palette.Formula);
    private static readonly IBrush _data = new SolidColorBrush(Palette.Data);

    public BlendVerdict? Verdict
    {
        get => GetValue(VerdictProperty);
        set => SetValue(VerdictProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == VerdictProperty)
            Rebuild();
    }

    protected override void Build()
    {
        if (Verdict is not { } verdict)
            return;
        Part("Advisor rating", _formula, verdict.Formula, "no rule for this line-up");
        Part("results", _data, verdict.Data, "none for these heroes");
    }

    private void Part(string name, IBrush brush, double? value, string missing)
    {
        Separate();
        Word(name, brush);
        if (value is { } amount)
            Amount(amount);
        else
            Word(missing, Faint, gap: 0);
    }
}
