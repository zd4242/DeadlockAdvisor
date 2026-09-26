using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Input;
using DeadlockAdvisor.Controls;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Features.Shared.Modals.Choice;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Services.Contracts;
using DeadlockAdvisor.Theme;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.HeroTraits;

/// <summary>
/// The hero × trait matrix, edited in place, built for filling cells fast:
/// <list type="bullet">
/// <item>Type the number and it commits itself the moment no further digit could fit (54 lands on
/// two keystrokes, 100 on three), dropping one hero down. Space or Tab commits a half-typed value
/// early, needed only for a bare 0-9.</item>
/// <item>Traits run 0..100, or -100..100 for the signed ones; press "-" first for those.</item>
/// <item>Backspace rubs out the last digit typed, or blanks the cell back to 0 when nothing is pending.</item>
/// <item>"Copy from..." clones an already-rated hero's whole profile as a starting point.</item>
/// <item>Clicking a trait's header sorts the heroes by it: highest first, then lowest, then back to by name.</item>
/// </list>
/// Every edit writes straight through to the store; the data service debounces the CSV save.
/// </summary>
public class HeroTraitsViewModel : ViewModelBase, ISearchablePage
{
    public const string FocusSearchAction = "FocusSearch";

    public const string Hint =
        "Type 0–100 · it commits as soon as no more digits fit (space or tab "
        + "ends a short one) · \"-\" first for ± traits · Backspace clears · "
        + "Enter moves down a hero";

    private readonly IDataService _data;
    private readonly IModalService _modals;
    private string _pendingDigits = "";
    private bool _pendingNegative;

    public HeroTraitsViewModel(IDataService data, IModalService modals)
    {
        _data = data;
        _modals = modals;

        CopyFromCommand = ReactiveCommand.Create(CopyFrom);
        ClearHeroCommand = ReactiveCommand.Create(ClearHero);
        SortCommand = ReactiveCommand.Create<int>(CycleSort);

        Reload();
        if (Heroes.Count > 0 && Categories.Count > 0)
            (CurrentRow, CurrentColumn) = (0, 0);
        OnCellFocused();

        this.WhenAnyValue(vm => vm.FilterText).Skip(1).Subscribe(_ => ApplyFilter()).DisposeWith(Disposables);
        // Leaving a cell by any route abandons a half-typed number rather than committing
        // something the eye never checked.
        this.WhenAnyValue(vm => vm.CurrentRow, vm => vm.CurrentColumn).Skip(1).Subscribe(_ => OnCellFocused()).DisposeWith(Disposables);
        data.StoreReplaced.Subscribe(_ => Reload()).DisposeWith(Disposables);
    }

    [Reactive] public IReadOnlyList<Hero> Heroes { get; private set; } = [];
    [Reactive] public IReadOnlyList<Category> Categories { get; private set; } = [];

    /// <summary>Every index into <see cref="Heroes"/> in the sorted order, hidden ones included; the stripes follow it.</summary>
    [Reactive] public IReadOnlyList<int> RowOrder { get; private set; } = [];

    /// <summary>Indices into <see cref="Heroes"/> the filter lets through, in the sorted order.</summary>
    [Reactive] public IReadOnlyList<int> VisibleRows { get; private set; } = [];

    /// <summary>The trait the heroes are sorted by, or -1 for by name.</summary>
    [Reactive] public int SortColumn { get; private set; } = -1;
    [Reactive] public bool SortDescending { get; private set; }

    [Reactive] public string FilterText { get; set; } = "";

    /// <summary>The focused cell: an index into <see cref="Heroes"/> and one into <see cref="Categories"/>, or -1.</summary>
    [Reactive] public int CurrentRow { get; set; } = -1;
    [Reactive] public int CurrentColumn { get; set; } = -1;

    /// <summary>A half-typed number, drawn in place of the stored one so you see what you're about to commit.</summary>
    [Reactive] public string? PendingText { get; private set; }

    /// <summary>Bumped whenever a stored value changes, so the grid repaints.</summary>
    [Reactive] public int Revision { get; private set; }

    [Reactive] public string ProgressText { get; private set; } = "";

    /// <summary>The footer: what the focused cell means, or what the last action did.</summary>
    [Reactive] public IReadOnlyList<TextSpan> ContextSpans { get; private set; } = [];

    /// <summary>How many rows a Page Up/Down moves; the view keeps this in step with the grid's height.</summary>
    public int PageRows { get; set; } = 10;

    public Func<string, string, double> ValueOf => (heroId, categoryId) => _data.Store.HeroScore(heroId, categoryId);

    public ReactiveCommand<Unit, Unit> CopyFromCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearHeroCommand { get; }
    public ReactiveCommand<int, Unit> SortCommand { get; }

    public void FocusSearch() => RequestViewAction(FocusSearchAction);

    private Hero? CurrentHero => CurrentRow >= 0 && CurrentRow < Heroes.Count ? Heroes[CurrentRow] : null;
    private Category? CurrentCategory => CurrentColumn >= 0 && CurrentColumn < Categories.Count ? Categories[CurrentColumn] : null;
    private bool HasPending => _pendingDigits.Length > 0 || _pendingNegative;

    // -- editing ------------------------------------------------------------------

    /// <summary>Write one cell, clamped to the trait's scale. Rescoring and saving follow from the data service.</summary>
    public void SetValue(int row, int column, double value)
    {
        if (row < 0 || row >= Heroes.Count || column < 0 || column >= Categories.Count)
            return;
        var category = Categories[column];
        var number = Math.Max(category.ScaleMin, Math.Min(category.ScaleMax, value));
        if (!_data.Store.SetHeroScore(Heroes[row].HeroId, category.CategoryId, number))
            return;
        _data.MarkEdited(DataFiles.HeroScores);
        Revision++;
        RefreshProgress();
    }

    /// <summary>Typed characters: digits build the pending number, "-" flips its sign on ± traits.</summary>
    public bool HandleText(string text)
    {
        if (CurrentHero is null || CurrentCategory is null)
            return false;
        var handled = false;
        foreach (var character in text)
        {
            if (character == '-')
            {
                if (CurrentCategory.IsSigned)
                {
                    _pendingNegative = !_pendingNegative;
                    UpdatePendingText();
                }
                handled = true;
            }
            else if (char.IsAsciiDigit(character))
            {
                TypeDigit(character);
                handled = true;
            }
        }
        return handled;
    }

    private void TypeDigit(char digit)
    {
        // A leading zero is only noise.
        if (_pendingDigits == "0")
            _pendingDigits = "";
        _pendingDigits += digit;
        if (_pendingDigits == "0" || IsComplete())
        {
            var (row, column) = (CurrentRow, CurrentColumn);
            CommitPending();
            Advance(row, column);
        }
        else
        {
            UpdatePendingText();
        }
    }

    /// <summary>
    /// Everything but typed characters. Digits and "-" arrive as text, so their keys are left alone;
    /// any other key finishes a half-typed number first, as Qt's grid did.
    /// </summary>
    public bool HandleKey(Key key, KeyModifiers modifiers)
    {
        if (CurrentHero is null || CurrentCategory is null || IsTextKey(key))
            return false;
        var (row, column) = (CurrentRow, CurrentColumn);
        var shift = modifiers.HasFlag(KeyModifiers.Shift);
        var control = modifiers.HasFlag(KeyModifiers.Control);

        if (key == Key.Escape && HasPending)
        {
            ClearPending();
            return true;
        }

        if (key is Key.Delete or Key.Back)
        {
            // Mid-number, backspace rubs out one digit; on a settled cell it still means "blank this and move on".
            if (HasPending)
            {
                if (_pendingDigits.Length > 0)
                    _pendingDigits = _pendingDigits[..^1];
                else
                    _pendingNegative = false;
                UpdatePendingText();
                return true;
            }
            SetValue(row, column, 0);
            Advance(row, column);
            return true;
        }

        if (key == Key.Space || (key == Key.Tab && !shift))
        {
            // The early-commit key, needed only for a bare 1-9 that the scale can't rule out as the
            // first half of a bigger number.
            if (CommitPending())
            {
                Advance(row, column);
                return true;
            }
            if (key == Key.Space)
                return true;
        }

        if (key == Key.Enter)
        {
            CommitPending();
            // Enter drops to the next hero, same trait, without wrapping.
            if (NextVisibleRow(row) is { } below)
                CurrentRow = below;
            return true;
        }

        CommitPending();
        return Navigate(key, row, column, shift, control);
    }

    private static bool IsTextKey(Key key) =>
        key is >= Key.D0 and <= Key.D9 or >= Key.NumPad0 and <= Key.NumPad9 or Key.OemMinus or Key.Subtract;

    private bool Navigate(Key key, int row, int column, bool shift, bool control)
    {
        var last = Categories.Count - 1;
        switch (key)
        {
            case Key.Up:
                MoveTo(PreviousVisibleRow(row) ?? row, column);
                return true;
            case Key.Down:
                MoveTo(NextVisibleRow(row) ?? row, column);
                return true;
            case Key.Left:
                MoveTo(row, Math.Max(0, column - 1));
                return true;
            case Key.Right:
                MoveTo(row, Math.Min(last, column + 1));
                return true;
            case Key.Home:
                MoveTo(control ? VisibleRows.FirstOrDefault(row) : row, 0);
                return true;
            case Key.End:
                MoveTo(control ? VisibleRows.LastOrDefault(row) : row, last);
                return true;
            case Key.PageUp:
            case Key.PageDown:
                MoveTo(StepVisibleRows(row, key == Key.PageDown ? PageRows : -PageRows), column);
                return true;
            case Key.Tab when shift:
                if (column > 0)
                    MoveTo(row, column - 1);
                else if (PreviousVisibleRow(row) is { } above)
                    MoveTo(above, last);
                return true;
            case Key.Tab:
                if (column < last)
                    MoveTo(row, column + 1);
                else if (NextVisibleRow(row) is { } below)
                    MoveTo(below, 0);
                return true;
            default:
                return false;
        }
    }

    private void MoveTo(int row, int column) => (CurrentRow, CurrentColumn) = (row, column);

    /// <summary>
    /// Move one hero down, wrapping onto the top of the next trait at the end of a column, so rating
    /// a trait across the whole roster is one uninterrupted run of digits.
    /// </summary>
    private void Advance(int row, int column)
    {
        if (NextVisibleRow(row) is { } below)
            MoveTo(below, column);
        else if (column + 1 < Categories.Count && VisibleRows.Count > 0)
            MoveTo(VisibleRows[0], column + 1);
    }

    /// <summary>True once another digit could only overshoot the trait's scale: the moment a number commits itself.</summary>
    private bool IsComplete()
    {
        var category = CurrentCategory;
        var limit = category is null ? 100.0 : Math.Max(Math.Abs(category.ScaleMin), Math.Abs(category.ScaleMax));
        return double.Parse(_pendingDigits, System.Globalization.CultureInfo.InvariantCulture) * 10 > limit;
    }

    /// <summary>Write whatever digits are buffered. False when there was nothing to write, so a key can tell finishing a number from navigating.</summary>
    private bool CommitPending()
    {
        var (digits, negative) = (_pendingDigits, _pendingNegative);
        ClearPending();
        if (digits.Length == 0)
            return false;
        var value = double.Parse(digits, System.Globalization.CultureInfo.InvariantCulture);
        SetValue(CurrentRow, CurrentColumn, negative ? -value : value);
        return true;
    }

    private void ClearPending()
    {
        _pendingDigits = "";
        _pendingNegative = false;
        UpdatePendingText();
    }

    private void UpdatePendingText() =>
        PendingText = HasPending ? (_pendingNegative ? "-" : "") + (_pendingDigits.Length > 0 ? _pendingDigits : "_") : null;

    // -- rows ---------------------------------------------------------------------

    /// <summary>Where <paramref name="row"/> sits on screen, or -1 if the filter hides it.</summary>
    private int Position(int row)
    {
        for (var position = 0; position < VisibleRows.Count; position++)
        {
            if (VisibleRows[position] == row)
                return position;
        }
        return -1;
    }

    /// <summary>The row shown below <paramref name="row"/>, or the top one when the filter hides it.</summary>
    private int? NextVisibleRow(int row)
    {
        var below = Position(row) + 1;
        return below < VisibleRows.Count ? VisibleRows[below] : null;
    }

    private int? PreviousVisibleRow(int row)
    {
        var above = Position(row) - 1;
        return above >= 0 ? VisibleRows[above] : null;
    }

    private int StepVisibleRows(int row, int step)
    {
        if (VisibleRows.Count == 0)
            return row;
        return VisibleRows[Math.Clamp(Math.Max(0, Position(row)) + step, 0, VisibleRows.Count - 1)];
    }

    /// <summary>Highest first, then lowest first, then a third click on the same trait clears back to by name.</summary>
    private void CycleSort(int column)
    {
        if (column < 0 || column >= Categories.Count)
            return;
        if (column != SortColumn)
            (SortColumn, SortDescending) = (column, true);
        else if (SortDescending)
            SortDescending = false;
        else
            (SortColumn, SortDescending) = (-1, false);
        Reorder();
        ApplyFilter();
        // Straight to the top of the sorted trait, ready to read or rate down it.
        if (VisibleRows.Count > 0)
            MoveTo(VisibleRows[0], column);
    }

    /// <summary>
    /// The order only changes on a header click or a reload. Edits don't re-sort: a hero that jumped
    /// away mid-pass would break typing down the column. Both directions are stable, so ties stay by name.
    /// </summary>
    private void Reorder()
    {
        var rows = Enumerable.Range(0, Heroes.Count);
        if (SortColumn >= 0)
        {
            var store = _data.Store;
            var categoryId = Categories[SortColumn].CategoryId;
            Func<int, double> score = row => store.HeroScore(Heroes[row].HeroId, categoryId);
            rows = SortDescending ? rows.OrderByDescending(score) : rows.OrderBy(score);
        }
        RowOrder = rows.ToList();
    }

    private void ApplyFilter()
    {
        var needle = FilterText.Trim().ToLowerInvariant();
        VisibleRows = RowOrder
            .Where(row => needle.Length == 0
                          || Heroes[row].HeroName.ToLowerInvariant().Contains(needle, StringComparison.Ordinal)
                          || Heroes[row].HeroId.ToLowerInvariant().Contains(needle, StringComparison.Ordinal))
            .ToList();
    }

    // -- toolbar ------------------------------------------------------------------

    private void CopyFrom()
    {
        if (CurrentHero is not { } target)
            return;
        var store = _data.Store;
        // Only already-rated heroes: copying from a blank one is never what you meant.
        var rated = Heroes.Where(hero => hero.HeroId != target.HeroId && store.HeroFilledCount(hero.HeroId) > 0).ToList();
        if (rated.Count == 0)
        {
            ShowMessage("No other hero has any traits filled in yet — nothing to copy.");
            return;
        }

        _modals.ShowModal(new ChoiceModalViewModel(
            _modals,
            "Copy trait profile",
            $"Copy every trait onto {target.HeroName} from:",
            rated.Select(hero => hero.HeroName).ToList(),
            index =>
            {
                var source = rated[index];
                if (!_data.Store.CopyHeroScores(source.HeroId, target.HeroId))
                    return;
                AfterBulkEdit();
                ShowMessage($"Copied {source.HeroName}'s profile onto {target.HeroName} — adjust from there.");
            }));
    }

    private void ClearHero()
    {
        if (CurrentHero is not { } hero || !_data.Store.ClearHeroScores(hero.HeroId))
            return;
        AfterBulkEdit();
        ShowMessage($"Cleared every trait on {hero.HeroName}.");
    }

    private void AfterBulkEdit()
    {
        _data.MarkEdited(DataFiles.HeroScores);
        Revision++;
        RefreshProgress();
    }

    private void ShowMessage(string text) => ContextSpans = [new TextSpan(text)];

    // -- status -------------------------------------------------------------------

    private void OnCellFocused()
    {
        ClearPending();
        RefreshProgress();
        if (CurrentHero is not { } hero || CurrentCategory is not { } category)
            return;
        var value = _data.Store.HeroScore(hero.HeroId, category.CategoryId);
        ContextSpans =
        [
            new TextSpan(hero.HeroName, Bold: true),
            new TextSpan("  ·  "),
            new TextSpan(category.CategoryName, Bold: true),
            new TextSpan(" "),
            new TextSpan($"(scale {Format.Num(category.ScaleMin)} to {Format.Num(category.ScaleMax)}, currently {Format.Num(value)})", Palette.TextFaint),
            TextSpan.LineBreak,
            new TextSpan(category.Description, Palette.TextDim),
        ];
    }

    private void RefreshProgress()
    {
        var coverage = _data.Store.Coverage();
        var parts = new List<string>();
        if (CurrentHero is { } hero)
            parts.Add($"{hero.HeroName}: {_data.Store.HeroFilledCount(hero.HeroId)}/{Categories.Count} traits");
        parts.Add($"{coverage.ScoresFilled}/{coverage.ScoresTotal} overall");
        ProgressText = string.Join("  ·  ", parts);
    }

    /// <summary>Adopt a reloaded store: heroes and traits may have changed.</summary>
    private void Reload()
    {
        var store = _data.Store;
        Heroes = store.HeroesSorted();
        Categories = store.CategoriesOrdered();
        if (SortColumn >= Categories.Count)
            (SortColumn, SortDescending) = (-1, false);
        Reorder();
        ApplyFilter();
        if (CurrentRow >= Heroes.Count || CurrentColumn >= Categories.Count)
            MoveTo(Math.Min(CurrentRow, Heroes.Count - 1), Math.Min(CurrentColumn, Categories.Count - 1));
        Revision++;
        RefreshProgress();
    }
}
