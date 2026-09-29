using System.Globalization;
using System.Text.RegularExpressions;

namespace DeadlockAdvisor.Core;

/// <summary>A Steam account as deadlock-api.com keys players: the 32-bit account ID, the number in a SteamID3.</summary>
public static partial class SteamAccount
{
    /// <summary>The SteamID64 of account 0: an account's SteamID64 is this plus its account ID.</summary>
    public const long SteamId64Base = 76561197960265728;

    /// <summary>
    /// Read an account ID out of whatever form it was pasted in: the account ID itself, a SteamID64,
    /// [U:1:n], STEAM_0:y:z, or a steamcommunity.com/profiles/ link. A custom /id/ link has no number
    /// in it, so it isn't read.
    /// </summary>
    public static bool TryParse(string? text, out long accountId)
    {
        accountId = 0;
        text = text?.Trim() ?? "";
        long number;
        if (SteamId3().Match(text) is { Success: true } id3)
        {
            number = Number(id3.Groups[1].Value);
        }
        else if (LegacySteamId().Match(text) is { Success: true } legacy)
        {
            number = Number(legacy.Groups[2].Value) * 2 + Number(legacy.Groups[1].Value);
        }
        else if (ProfileLink().Match(text) is { Success: true } link)
        {
            number = Number(link.Groups[1].Value);
        }
        else if (Digits().IsMatch(text))
        {
            number = Number(text);
        }
        else
        {
            return false;
        }

        if (number >= SteamId64Base)
            number -= SteamId64Base;
        if (number is <= 0 or > uint.MaxValue)
            return false;
        accountId = number;
        return true;
    }

    // Too long for a long reads as 0, which isn't an account.
    private static long Number(string digits) =>
        long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0;

    [GeneratedRegex(@"^\[?U:1:(\d+)\]?$", RegexOptions.IgnoreCase)]
    private static partial Regex SteamId3();

    [GeneratedRegex(@"^STEAM_[0-5]:([01]):(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex LegacySteamId();

    [GeneratedRegex(@"/profiles/(\d+)/?$", RegexOptions.IgnoreCase)]
    private static partial Regex ProfileLink();

    [GeneratedRegex(@"^\d+$")]
    private static partial Regex Digits();
}
