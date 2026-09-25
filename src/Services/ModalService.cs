using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Services.Contracts;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Services;

public class ModalService(ILoggingService loggingService) : ReactiveObject, IModalService
{
    private readonly Subject<ViewModelBase> _showModalSubject = new();
    public IObservable<ViewModelBase> ShowModalObservable => _showModalSubject;

    public IObservable<bool> IsModalOpenObservable =>
        _showModalSubject.Select(_ => true)
            .Merge(_closeModalSubject.Select(_ => false))
            .StartWith(false);

    private readonly Subject<Unit> _closeModalSubject = new();
    public IObservable<Unit> CloseModalObservable => _closeModalSubject;

    private IInputElement? _elementToRestoreFocusTo;

    [Reactive] public bool IsModalOpen { get; private set; }

    public void ShowModal(ViewModelBase modalViewModel)
    {
        if (IsModalOpen)
        {
            loggingService.Error("Trying to open a new modal when one is already open.");
            return;
        }

        _elementToRestoreFocusTo = null;

        var lifetime = GetDesktopLifetime();
        if (lifetime?.MainWindow?.FocusManager != null)
            _elementToRestoreFocusTo = lifetime.MainWindow.FocusManager.GetFocusedElement();

        _showModalSubject.OnNext(modalViewModel);
        IsModalOpen = true;
    }

    public void CloseModal()
    {
        if (!IsModalOpen)
            return;

        if (_elementToRestoreFocusTo != null)
        {
            if (_elementToRestoreFocusTo is Control controlToRestore && controlToRestore.IsAttachedToVisualTree() && controlToRestore.Focusable)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    controlToRestore.Focus(NavigationMethod.Unspecified);
                }, DispatcherPriority.Input);
            }
            else
            {
                loggingService.Warning($"Couldn't restore focus in {nameof(ModalService)}.");
            }
        }

        _closeModalSubject.OnNext(Unit.Default);
        IsModalOpen = false;
        _elementToRestoreFocusTo = null;
    }

    private static IClassicDesktopStyleApplicationLifetime? GetDesktopLifetime()
    {
        return Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
    }
}
