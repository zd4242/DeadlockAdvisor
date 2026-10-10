using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.ReactiveUI;
using Avalonia.Threading;
using DeadlockAdvisor.Features.Shared.ItemCard;
using ReactiveUI;

namespace DeadlockAdvisor.Features.ItemFormulas.ByItem;

public partial class ByItemView : ReactiveUserControl<ByItemViewModel>
{
    /// <summary>The detail's width that leaves the rules about 300 beside the 344 card, its gap and the card's padding.</summary>
    private const double _roomForCard = 700;

    public ByItemView()
    {
        InitializeComponent();

        DetailSplitter.DragCompleted += (_, _) => SaveSplit();

        this.WhenActivated(disposables =>
        {
            RestoreSplit();
            DetailPane.GetObservable(BoundsProperty)
                .Select(bounds => bounds.Width)
                .Where(width => width > 0)
                .Select(width => width >= _roomForCard)
                .DistinctUntilChanged()
                .Subscribe(roomy => ViewModel!.ShowCard = roomy)
                .DisposeWith(disposables);
            ViewModel!.WhenAnyValue(vm => vm.CurrentItem)
                .Subscribe(_ => ShowCard())
                .DisposeWith(disposables);
            ViewModel!.ViewInteraction
                .Subscribe(action =>
                {
                    if (action == ByItemViewModel.FocusSearchAction)
                    {
                        SearchBox.Focus();
                        SearchBox.SelectAll();
                    }
                    else if (action == ByItemViewModel.ScrollToRuleAction)
                        ScrollToHighlightedRule();
                })
                .DisposeWith(disposables);
        });
    }

    /// <summary>The same card hovering the item's icon shows, built by the window's card presenter.</summary>
    private void ShowCard()
    {
        var itemId = ViewModel?.CurrentItem?.ItemId;
        var card = itemId is null ? null : ItemCardHover.GetPresenter(this)?.CardFactory?.Invoke(itemId);
        if (card is not null)
            card.Width = ItemCardBuilder.Width;
        CardHost.Content = card;
        CardScroller.Offset = default;
    }

    /// <summary>After layout, since the rebuilt cards have no containers until then.</summary>
    private void ScrollToHighlightedRule()
    {
        Dispatcher.UIThread.Post(() =>
        {
            var rules = ViewModel?.Rules;
            var index = rules?.ToList().FindIndex(card => card.IsHighlighted) ?? -1;
            if (index >= 0)
                RuleCards.ContainerFromIndex(index)?.BringIntoView();
        }, DispatcherPriority.Loaded);
    }

    private void RestoreSplit()
    {
        if (ViewModel?.SplitterPosition is not { } share || share <= 0 || share >= 1)
            return;
        DetailSplit.RowDefinitions[0].Height = new GridLength(share, GridUnitType.Star);
        DetailSplit.RowDefinitions[2].Height = new GridLength(1 - share, GridUnitType.Star);
    }

    private void SaveSplit()
    {
        var rules = DetailSplit.RowDefinitions[0].ActualHeight;
        var preview = DetailSplit.RowDefinitions[2].ActualHeight;
        if (ViewModel is not null && rules + preview > 0)
            ViewModel.SplitterPosition = rules / (rules + preview);
    }
}
