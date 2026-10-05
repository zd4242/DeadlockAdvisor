using Avalonia.ReactiveUI;

namespace DeadlockAdvisor.Features.HeroItems;

public partial class HeroItemsView : ReactiveUserControl<HeroItemsViewModel>
{
    public HeroItemsView()
    {
        InitializeComponent();
    }
}
