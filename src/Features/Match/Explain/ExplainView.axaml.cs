using Avalonia.ReactiveUI;
using DeadlockAdvisor.Behaviors;

namespace DeadlockAdvisor.Features.Match.Explain;

public partial class ExplainView : ReactiveUserControl<ExplainViewModel>
{
    public ExplainView()
    {
        InitializeComponent();

        // A shared column keeps the widest width it has had: lines whose arithmetic has moved below
        // their name only let go of it in a scope that starts again.
        Root.GetObservable(Responsive.IsNarrowProperty).Subscribe(_ =>
        {
            Columns.SetValue(Grid.IsSharedSizeScopeProperty, false);
            Columns.SetValue(Grid.IsSharedSizeScopeProperty, true);
        });
    }
}
