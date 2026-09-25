using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Media;
using DeadlockAdvisor.Controls;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Formats;
using DeadlockAdvisor.Theme;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.ItemFormulas.ByItem;

/// <summary>Which trait a rule card reads and which relations it applies on.</summary>
public sealed record RuleTarget(string CategoryId, IReadOnlyList<Relation> Relations)
{
    public bool Equals(RuleTarget? other) =>
        other is not null && CategoryId == other.CategoryId && Relations.SequenceEqual(other.Relations);

    public override int GetHashCode() => HashCode.Combine(CategoryId, Relations.Count);
}

/// <summary>
/// One rule card on one item: a trait and a coefficient, applied on one or more relations. The store
/// keeps one row per (trait, relation), so the card stands for every relation sharing that number.
/// </summary>
public class RuleCardViewModel : ViewModelBase
{
    private readonly DataStore _store;
    private readonly string _itemId;
    private readonly Action<RuleTarget, RuleTarget, double> _changed;
    private readonly HashSet<Relation> _checked;
    private RuleTarget _target;
    private double _coefficient;
    private bool _reverting;

    /// <param name="changed">(what the card was, what it is now, its coefficient) after every accepted edit.</param>
    /// <param name="remove">Delete the rule the card stands for.</param>
    public RuleCardViewModel(DataStore store, string itemId, IReadOnlyList<Category> categories, RuleTarget target,
        double coefficient, Color traitColor, Action<RuleTarget, RuleTarget, double> changed, Action<RuleTarget> remove)
    {
        _store = store;
        _itemId = itemId;
        _changed = changed;
        _target = target;
        _checked = [.. target.Relations];
        // The spin box shows one decimal, so that's what the card holds from the start.
        _coefficient = NumberFormat.Round(coefficient, 1);

        Categories = categories;
        TraitColor = traitColor;
        SelectedCategory = categories.FirstOrDefault(category => category.CategoryId == target.CategoryId);
        Coefficient = (decimal)_coefficient;
        Explanation = FormulaText.BuyWhen(target.Relations);

        ToggleRelationCommand = ReactiveCommand.Create<Relation>(ToggleRelation);
        RemoveCommand = ReactiveCommand.Create(() => remove(_target));

        this.WhenAnyValue(vm => vm.SelectedCategory).Skip(1).Subscribe(_ => OnEdited()).DisposeWith(Disposables);
        this.WhenAnyValue(vm => vm.Coefficient)
            .Skip(1)
            .Subscribe(value =>
            {
                if (value is null)
                    return;
                _coefficient = NumberFormat.Round((double)value, 1);
                OnEdited();
            })
            .DisposeWith(Disposables);
    }

    public IReadOnlyList<Category> Categories { get; }
    public Color TraitColor { get; }
    [Reactive] public Category? SelectedCategory { get; set; }
    [Reactive] public decimal? Coefficient { get; set; }

    public bool IsAgainst => _checked.Contains(Relation.Against);
    public bool IsWith => _checked.Contains(Relation.With);
    public bool IsAs => _checked.Contains(Relation.As);

    /// <summary>What the card means, or why the last edit was refused.</summary>
    [Reactive] public IReadOnlyList<TextSpan> Explanation { get; private set; }

    public ReactiveCommand<Relation, Unit> ToggleRelationCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveCommand { get; }

    /// <summary>At least one relation stays on: the × is how a rule goes.</summary>
    private void ToggleRelation(Relation relation)
    {
        if (!_checked.Remove(relation))
            _checked.Add(relation);
        if (_checked.Count == 0)
            _checked.Add(relation);
        else
            OnEdited();
        RaiseRelationFlags();
    }

    private void OnEdited()
    {
        if (_reverting || SelectedCategory is null)
            return;
        var old = _target;
        var categoryId = SelectedCategory.CategoryId;
        IReadOnlyList<Relation> relations = Relations.All.Where(_checked.Contains).ToList();

        // The store keeps one number per trait + relation, so a relation another card on this trait
        // already has would silently overwrite that card's number: refuse it and say so.
        var clashes = relations.Where(relation => IsTaken(categoryId, relation)).ToList();
        if (clashes.Count > 0)
        {
            var free = Relations.All.Where(relation => !IsTaken(categoryId, relation)).ToList();
            if (categoryId != _target.CategoryId && relations.Count == 1 && free.Count > 0)
            {
                // A card pointed at a trait that's partly used slides onto a relation still free.
                relations = [free[0]];
                SetChecked(relations);
            }
            else
            {
                Revert(clashes[0]);
                return;
            }
        }

        _target = new RuleTarget(categoryId, relations);
        Explanation = FormulaText.BuyWhen(relations);
        _changed(old, _target, _coefficient);
    }

    /// <summary>Another card on this item already has this trait + relation.</summary>
    private bool IsTaken(string categoryId, Relation relation)
    {
        if (categoryId == _target.CategoryId && _target.Relations.Contains(relation))
            return false;
        return _store.ItemCoefficients.ContainsKey(new CoefficientKey(_itemId, categoryId, relation));
    }

    /// <summary>Put the card back as it was and say why.</summary>
    private void Revert(Relation relation)
    {
        _reverting = true;
        SelectedCategory = Categories.FirstOrDefault(category => category.CategoryId == _target.CategoryId);
        SetChecked(_target.Relations);
        _reverting = false;
        Explanation = [new TextSpan($"Another rule on that trait already covers '{relation.Key()}'.", Palette.TextDim)];
    }

    private void SetChecked(IEnumerable<Relation> relations)
    {
        _checked.Clear();
        _checked.UnionWith(relations);
        RaiseRelationFlags();
    }

    private void RaiseRelationFlags()
    {
        this.RaisePropertyChanged(nameof(IsAgainst));
        this.RaisePropertyChanged(nameof(IsWith));
        this.RaisePropertyChanged(nameof(IsAs));
    }
}
