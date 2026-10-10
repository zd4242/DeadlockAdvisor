using DeadlockAdvisor.Core;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.Shared.Modals.Base;

public class ModalViewModel : ViewModelBase
{
    [Reactive] public bool IsVisible { get; set; }
    [Reactive] public object? Content { get; set; }

    /// <summary>The main window's zoom: a dialog is a window of its own, outside the transform that zooms the app.</summary>
    [Reactive] public double UiScale { get; set; } = 1.0;
}
