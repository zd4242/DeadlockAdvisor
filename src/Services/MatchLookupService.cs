using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using DeadlockAdvisor.Services.Contracts;
using DeadlockAdvisor.Services.GameApi;

namespace DeadlockAdvisor.Services;

/// <param name="Team">The game's team: 0 is the Hidden King, 1 the Archmother.</param>
/// <param name="PlayerSlot">The player's place in the lobby, which orders each team.</param>
public sealed record MatchPlayer(long AccountId, long HeroGameId, int Team, int PlayerSlot);

/// <param name="WinningTeam">The team that won, as <see cref="MatchPlayer.Team"/> numbers them; null if the API didn't say.</param>
public sealed record LookedUpMatch(long MatchId, DateTimeOffset? StartedAt, TimeSpan? Duration, int? WinningTeam, IReadOnlyList<MatchPlayer> Players);

/// <summary>A lookup that failed, with a message fit to show as it is.</summary>
public sealed class MatchLookupException(string message, Exception? inner = null) : Exception(message, inner);

public interface IMatchLookupService
{
    /// <summary>A finished match's players. Throws <see cref="MatchLookupException"/> or <see cref="OperationCanceledException"/>.</summary>
    Task<LookedUpMatch> LookUpAsync(long matchId, CancellationToken cancellationToken = default);

    /// <summary>Account → Steam display name, for the accounts that have a known profile. Throws as <see cref="IDeadlockApi"/> does.</summary>
    Task<IReadOnlyDictionary<long, string>> SteamNamesAsync(IEnumerable<long> accountIds, CancellationToken cancellationToken = default);
}

/// <summary>
/// Finished matches from deadlock-api.com: /v1/matches/{id}/metadata for who played what, and
/// /v1/players/steam for their names. A match only has metadata once it has ended.
/// </summary>
public sealed partial class MatchLookupService(IDeadlockApi api) : IMatchLookupService
{
    public const string Matches = "https://api.deadlock-api.com/v1/matches";
    public const string SteamProfiles = "https://api.deadlock-api.com/v1/players/steam";

    /// <summary>
    /// A match ID out of whatever was pasted: the number itself, or a link with it in. The longest run
    /// of digits wins, so a link's page number or tab doesn't.
    /// </summary>
    public static bool TryParseMatchId(string? text, out long matchId)
    {
        matchId = 0;
        var digits = DigitRuns().Matches(text ?? "").Select(match => match.Value).OrderByDescending(run => run.Length).FirstOrDefault();
        return digits is not null
               && long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out matchId)
               && matchId > 0;
    }

    public async Task<LookedUpMatch> LookUpAsync(long matchId, CancellationToken cancellationToken = default)
    {
        JsonNode? metadata;
        try
        {
            metadata = await api.GetJsonAsync($"{Matches}/{matchId.ToString(CultureInfo.InvariantCulture)}/metadata", cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new MatchLookupException(
                $"No finished match {matchId} was found. A match can only be looked up once it's over, and it can take a few minutes to become available.", ex);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new MatchLookupException(
                "deadlock-api.com can only fetch a few new matches an hour from Steam, and that limit has been reached. Try again later.", ex);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
        {
            throw new MatchLookupException($"deadlock-api.com didn't accept {matchId} as a match ID.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new MatchLookupException($"Couldn't reach deadlock-api.com: {ex.Message}", ex);
        }
        catch (TimeoutException ex)
        {
            throw new MatchLookupException(ex.Message, ex);
        }
        catch (JsonException ex)
        {
            throw new MatchLookupException("deadlock-api.com's answer couldn't be read.", ex);
        }

        var info = PyJson.Get(metadata, "match_info");
        var players = PyJson.Items(info, "players")
            .Select(player => new MatchPlayer(PyJson.Int(player, "account_id"), PyJson.Int(player, "hero_id"),
                (int)PyJson.Int(player, "team"), (int)PyJson.Int(player, "player_slot")))
            .OrderBy(player => player.Team)
            .ThenBy(player => player.PlayerSlot)
            .ToList();
        if (players.Count == 0)
            throw new MatchLookupException($"deadlock-api.com has match {matchId}, but no players in it.");

        var start = PyJson.Int(info, "start_time");
        var duration = PyJson.Int(info, "duration_s");
        return new LookedUpMatch(matchId,
            start > 0 ? DateTimeOffset.FromUnixTimeSeconds(start) : null,
            duration > 0 ? TimeSpan.FromSeconds(duration) : null,
            PyJson.Get(info, "winning_team") is null ? null : (int)PyJson.Int(info, "winning_team"),
            players);
    }

    public async Task<IReadOnlyDictionary<long, string>> SteamNamesAsync(IEnumerable<long> accountIds, CancellationToken cancellationToken = default)
    {
        var ids = accountIds.Where(id => id > 0).Distinct().ToList();
        var names = new Dictionary<long, string>();
        if (ids.Count == 0)
            return names;

        JsonNode? profiles;
        try
        {
            profiles = await api.GetJsonAsync($"{SteamProfiles}?account_ids=" + string.Join(",", ids.Select(id => id.ToString(CultureInfo.InvariantCulture))),
                cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // None of them has a known profile.
            return names;
        }

        foreach (var profile in profiles as JsonArray ?? [])
        {
            var name = PyJson.Text(profile, "personaname").Trim();
            if (name.Length > 0)
                names[PyJson.Int(profile, "account_id")] = name;
        }
        return names;
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex DigitRuns();
}
