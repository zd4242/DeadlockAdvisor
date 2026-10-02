namespace DeadlockAdvisor.Services.Contracts;

/// <summary>Whether another app, such as the game, has focus, which a window opened now would take from it.</summary>
public interface IForegroundService
{
    bool IsAnotherAppInFront { get; }
}
