using DeadlockAdvisor.Core;
using DeadlockAdvisor.Enums;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.Match.Board;

/// <summary>One hero in the palette, kept for the life of the loaded data and refreshed in place.</summary>
public class HeroTileViewModel(string heroId, string heroName) : ViewModelBase
{
    public string HeroId { get; } = heroId;
    public string HeroName { get; } = heroName;

    [Reactive] public Role Role { get; set; }
    [Reactive] public bool IsHighlighted { get; set; }
    [Reactive] public bool IsShown { get; set; } = true;
}
