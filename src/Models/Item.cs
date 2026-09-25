namespace DeadlockAdvisor.Models;

/// <param name="Category">The shop category (weapon / vitality / spirit), unrelated to trait categories.</param>
public sealed record Item(string ItemId, string ItemName, string Category, int Tier, long GameId = 0, int Cost = 0);
