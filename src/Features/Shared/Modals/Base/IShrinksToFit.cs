namespace DeadlockAdvisor.Features.Shared.Modals.Base;

/// <summary>A dialog that would rather be scaled down than scroll when the window is short.</summary>
public interface IShrinksToFit
{
    /// <summary>The smallest scale the dialog shrinks to before it scrolls.</summary>
    double MinFitScale { get; }
}
