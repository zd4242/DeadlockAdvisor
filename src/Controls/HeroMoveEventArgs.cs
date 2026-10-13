using Avalonia.Interactivity;

namespace DeadlockAdvisor.Controls;

/// <summary>A roster slot's hero dragged onto the place another hero holds on the same row.</summary>
public class HeroMoveEventArgs(RoutedEvent routedEvent, string heroId, string ontoHeroId) : HeroEventArgs(routedEvent, heroId)
{
    public string OntoHeroId { get; } = ontoHeroId;
}
