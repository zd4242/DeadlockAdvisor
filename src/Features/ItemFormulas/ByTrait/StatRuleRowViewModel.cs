using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Services.Formats;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.ItemFormulas.ByTrait;

/// <summary>A stat in the stat-rule dropdown: "Spirit Resist (22 items)".</summary>
public sealed record StatChoice(string Stat, string Text)
{
    public override string ToString() => Text;
}

/// <summary>One stat rule on the selected trait: which stat, what each point of it is worth, and how much a conditional stat counts.</summary>
public class StatRuleRowViewModel : ViewModelBase
{
    public const string PerUnitTip =
        "Coefficient per point of the stat: 0.2 turns 30% Spirit Resist into 6.\n"
        + "Negative to count the stat against the item.";

    public const string ConditionalTip =
        "How much a stat counts when it only applies sometimes -- on an\n"
        + "active, a passive trigger, below half health. 1 = fully, 0 = ignore.";

    private StatRule _rule;
    private double _perUnit;
    private double _conditional;

    /// <param name="edited">(the rule now, the stat it used to read when that changed, else null).</param>
    public StatRuleRowViewModel(StatRule rule, IReadOnlyList<StatChoice> choices, Action<StatRule, string?> edited, Action<string> remove)
    {
        _rule = rule;
        // The spin boxes show three and two decimals, so that's what the row holds from the start.
        _perUnit = NumberFormat.Round(rule.PerUnit, 3);
        _conditional = NumberFormat.Round(rule.ConditionalFactor, 2);

        Choices = choices;
        SelectedChoice = choices.FirstOrDefault(choice => choice.Stat == rule.Stat) ?? choices.FirstOrDefault();
        PerUnit = (decimal)_perUnit;
        Conditional = (decimal)_conditional;
        Note = string.IsNullOrEmpty(rule.Note) ? null : rule.Note;

        RemoveCommand = ReactiveCommand.Create(() => remove(_rule.Stat));

        this.WhenAnyValue(vm => vm.SelectedChoice)
            .Skip(1)
            .WhereNotNull()
            .Subscribe(choice =>
            {
                var oldStat = _rule.Stat;
                _rule = _rule with { Stat = choice.Stat };
                edited(_rule, oldStat);
            })
            .DisposeWith(Disposables);
        this.WhenAnyValue(vm => vm.PerUnit, vm => vm.Conditional)
            .Skip(1)
            .Subscribe(values =>
            {
                if (values.Item1 is { } perUnit)
                    _perUnit = (double)perUnit;
                if (values.Item2 is { } conditional)
                    _conditional = (double)conditional;
                _rule = _rule with { PerUnit = NumberFormat.Round(_perUnit, 4), ConditionalFactor = NumberFormat.Round(_conditional, 4) };
                edited(_rule, null);
            })
            .DisposeWith(Disposables);
    }

    public IReadOnlyList<StatChoice> Choices { get; }
    [Reactive] public StatChoice? SelectedChoice { get; set; }
    [Reactive] public decimal? PerUnit { get; set; }
    [Reactive] public decimal? Conditional { get; set; }

    /// <summary>The rule's note from the CSV, shown as the row's tooltip.</summary>
    public string? Note { get; }

    public ReactiveCommand<Unit, Unit> RemoveCommand { get; }
}
