using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using ReactiveUI;

namespace DeadlockAdvisor.Core;

public class ViewModelBase : ReactiveObject, IDisposable, IActivatableViewModel
{
    // Enable ViewModel to request View actions (hotkeys)
    private readonly Subject<string> _viewInteraction = new();
    public IObservable<string> ViewInteraction => _viewInteraction.AsObservable();
    protected void RequestViewAction(string actionName)
    {
        _viewInteraction.OnNext(actionName);
    }

    public ViewModelActivator Activator { get; } = new();

    protected readonly CompositeDisposable Disposables = [];

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            Disposables.Dispose();
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}
