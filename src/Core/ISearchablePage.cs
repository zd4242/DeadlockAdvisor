namespace DeadlockAdvisor.Core;

/// <summary>A page with a search box that Ctrl+F should jump to.</summary>
public interface ISearchablePage
{
    void FocusSearch();
}
