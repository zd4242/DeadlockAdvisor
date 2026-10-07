namespace DeadlockAdvisor.Features.HeroTraits;

/// <summary>One cell of the hero × trait grid, named by its hero and trait.</summary>
public sealed record TraitCell(string HeroId, string CategoryId);
