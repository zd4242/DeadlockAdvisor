using System.Reactive;
using System.Reactive.Subjects;
using DeadlockAdvisor.Services.Contracts;

namespace DeadlockAdvisor.Tests.Fakes;

/// <summary>Tests never watch the real keyboard: a test presses the scoreboard key and says whether it is still held.</summary>
public sealed class FakeScoreboardKey : IScoreboardKeyService
{
    private readonly Subject<Unit> _pressed = new();
    private readonly BehaviorSubject<ScoreboardKeyStatus> _status = new(ScoreboardKeyStatus.Watching);

    public IObservable<Unit> Pressed => _pressed;
    public IObservable<ScoreboardKeyStatus> Status => _status;

    /// <summary>Held from the moment it is pressed, until a test lets go.</summary>
    public bool IsHeld { get; set; } = true;

    public void Press() => _pressed.OnNext(Unit.Default);

    public void SetStatus(ScoreboardKeyStatus status) => _status.OnNext(status);
}
