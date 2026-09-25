using DeadlockAdvisor.Core;

namespace DeadlockAdvisor.Features.Shared;

/// <summary>A page not ported yet: says what will be there.</summary>
public class PlaceholderPageViewModel(string title, string text) : ViewModelBase
{
    public string Title { get; } = title;
    public string Text { get; } = text;
}
