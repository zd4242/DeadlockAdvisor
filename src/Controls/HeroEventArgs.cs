using Avalonia.Interactivity;

namespace DeadlockAdvisor.Controls;

/// <summary>A click on a hero in the palette or a roster slot.</summary>
public class HeroEventArgs(RoutedEvent routedEvent, string heroId) : RoutedEventArgs(routedEvent)
{
    public string HeroId { get; } = heroId;
}
