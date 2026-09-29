using System.Reactive;
using System.Reactive.Subjects;
using Avalonia.Controls;
using DeadlockAdvisor.Services.Contracts;

namespace DeadlockAdvisor.Tests.Fakes;

/// <summary>Tests never take F9 from the desktop: a test presses it, and says whether it was free.</summary>
public sealed class FakeGlobalHotkey : IGlobalHotkeyService
{
    private readonly Subject<Unit> _pressed = new();
    private readonly BehaviorSubject<HotkeyStatus> _status = new(HotkeyStatus.Registered);

    public IObservable<Unit> Pressed => _pressed;
    public IObservable<HotkeyStatus> Status => _status;

    public void Attach(TopLevel window)
    {
    }

    public void Press() => _pressed.OnNext(Unit.Default);

    public void SetStatus(HotkeyStatus status) => _status.OnNext(status);
}
