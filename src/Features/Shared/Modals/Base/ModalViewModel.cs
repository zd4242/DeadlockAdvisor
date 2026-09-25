using DeadlockAdvisor.Core;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.Shared.Modals.Base;

public class ModalViewModel : ViewModelBase
{
    [Reactive] public bool IsVisible { get; set; }
    [Reactive] public object? Content { get; set; }
}
