using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Subjects;
using Avalonia.Controls;
using DeadlockAdvisor.Services.Contracts;

namespace DeadlockAdvisor.Tests.Fakes;

/// <summary>Tests never take a key from the desktop: a test presses it, and says whether it was free.</summary>
public sealed class FakeGlobalHotkey : IGlobalHotkeyService
{
    private readonly Subject<Unit> _pressed = new();
    private readonly BehaviorSubject<HotkeyStatus> _status = new(HotkeyStatus.Registered);
    private int _suspensions;

    public IObservable<Unit> Pressed => _pressed;
    public IObservable<HotkeyStatus> Status => _status;

    public bool IsSuspended => _suspensions > 0;

    public void Attach(TopLevel window)
    {
    }

    public IDisposable Suspend()
    {
        _suspensions++;
        return Disposable.Create(() => _suspensions--);
    }

    public void Press() => _pressed.OnNext(Unit.Default);

    public void SetStatus(HotkeyStatus status) => _status.OnNext(status);
}
