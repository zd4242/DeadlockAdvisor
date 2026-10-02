using DeadlockAdvisor.Services.Contracts;

namespace DeadlockAdvisor.Tests.Fakes;

/// <summary>Tests have no desktop: this app is in front unless a test puts the game there.</summary>
public sealed class FakeForeground : IForegroundService
{
    public bool IsAnotherAppInFront { get; set; }
}
