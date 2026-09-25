namespace DeadlockAdvisor.Models;

/// <param name="GameId">deadlock-api.com's id; 0 until the game sync fills it in.</param>
public sealed record Hero(string HeroId, string HeroName, long GameId = 0);
