namespace DeadlockAdvisor.Models;

/// <summary>A hero trait, rated per hero on its scale and referenced by item formulas.</summary>
public sealed record Category(string CategoryId, string CategoryName, double ScaleMin, double ScaleMax, string Description)
{
    private static readonly string[] _sharedPrefixes =
    [
        "Deals ", "Has ", "Applies ", "Cares About ", "Countered by ",
        "Scales With ", "Summons ", "Does ", "Locks Down / ", "Is ",
    ];

    public bool IsSigned => ScaleMin < 0;

    /// <summary>
    /// Compact label for narrow grid columns, with the full name in the tooltip. Strips the
    /// leading verb most names share.
    /// </summary>
    public string ShortName
    {
        get
        {
            foreach (var prefix in _sharedPrefixes)
            {
                if (CategoryName.StartsWith(prefix, StringComparison.Ordinal))
                    return CategoryName[prefix.Length..];
            }
            return CategoryName;
        }
    }
}
