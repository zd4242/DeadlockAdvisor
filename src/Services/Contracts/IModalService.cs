using System.Reactive;
using DeadlockAdvisor.Core;

namespace DeadlockAdvisor.Services.Contracts;

public interface IModalService
{
    void ShowModal(ViewModelBase viewModel);
    void CloseModal();
    bool IsModalOpen { get; }
    IObservable<ViewModelBase> ShowModalObservable { get; }
    IObservable<Unit> CloseModalObservable { get; }
    IObservable<bool> IsModalOpenObservable { get; }
}
