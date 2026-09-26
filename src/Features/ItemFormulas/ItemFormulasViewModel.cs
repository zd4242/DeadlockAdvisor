using System.Reactive.Disposables;
using System.Reactive.Linq;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Features.ItemFormulas.ByItem;
using DeadlockAdvisor.Features.ItemFormulas.ByTrait;
using DeadlockAdvisor.Services.Contracts;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.ItemFormulas;

/// <summary>
/// Where the formulas get written, two ways in for two jobs: By Item to see and edit one item's rules
/// with a live preview, By Trait to type a whole trait's coefficients down every item. Both write
/// straight through to the store; the data service debounces the saves.
/// </summary>
public class ItemFormulasViewModel : ViewModelBase, ISearchablePage
{
    public ItemFormulasViewModel(IDataService data, IModalService modals, ISettingsService settings)
    {
        ByItem = new ByItemViewModel(data, modals, settings);
        ByTrait = new ByTraitViewModel(data);

        // The two panels edit the same table, so resync whichever just came into view.
        this.WhenAnyValue(vm => vm.SelectedTab)
            .Skip(1)
            .Subscribe(_ =>
            {
                this.RaisePropertyChanged(nameof(IsByItem));
                this.RaisePropertyChanged(nameof(IsByTrait));
                ByItem.RefreshCounts();
                ByTrait.Resync();
            })
            .DisposeWith(Disposables);
    }

    public ByItemViewModel ByItem { get; }
    public ByTraitViewModel ByTrait { get; }

    /// <summary>0: By Item, 1: By Trait.</summary>
    [Reactive] public int SelectedTab { get; set; }
    public bool IsByItem => SelectedTab == 0;
    public bool IsByTrait => SelectedTab == 1;

    /// <summary>Show one item's rules on the By Item panel.</summary>
    public void OpenItem(string itemId)
    {
        SelectedTab = 0;
        ByItem.OpenItem(itemId);
    }

    public void FocusSearch()
    {
        if (IsByItem)
            ByItem.FocusSearch();
        else
            ByTrait.FocusSearch();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ByItem.Dispose();
            ByTrait.Dispose();
        }
        base.Dispose(disposing);
    }
}
