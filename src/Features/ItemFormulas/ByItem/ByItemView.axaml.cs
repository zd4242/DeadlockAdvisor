using System.Reactive.Disposables;
using Avalonia.ReactiveUI;
using DeadlockAdvisor.Features.Shared.ItemCard;
using ReactiveUI;

namespace DeadlockAdvisor.Features.ItemFormulas.ByItem;

public partial class ByItemView : ReactiveUserControl<ByItemViewModel>
{
    public ByItemView()
    {
        InitializeComponent();

        DetailSplitter.DragCompleted += (_, _) => SaveSplit();

        this.WhenActivated(disposables =>
        {
            RestoreSplit();
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
