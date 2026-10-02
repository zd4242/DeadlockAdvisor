using System.Globalization;
using DeadlockAdvisor.Services.Formats;

namespace DeadlockAdvisor.Core;

/// <summary>How numbers are shown in the UI.</summary>
public static class Format
{
    /// <summary><c>_num</c>: 3 rather than 3.0, anything else as <c>:g</c>.</summary>
    public static string Num(double value) => NumberFormat.Short(value);

    /// <summary><c>_signed</c>: a leading + for zero and up.</summary>
    public static string Signed(double value) => (value >= 0 ? "+" : "") + Num(value);

    /// <summary>Always signed, to <paramref name="decimals"/> places; a negative that rounds to zero keeps its minus.</summary>
    public static string SignedFixed(double value, int decimals) =>
        (double.IsNegative(value) ? "" : "+") + NumberFormat.Fixed(value, decimals);

    /// <summary>
    /// A score or share as the results print it: to a tenth, so a column of them reads evenly, and without its
    /// sign, which a ▲/▼ or colour beside it carries.
    /// </summary>
    public static string Tenths(double value) => NumberFormat.Fixed(Math.Abs(value), 1);

    /// <summary>Match counts and souls, as the game prints net worth: "4.2k", "38k", or the number itself under a thousand.</summary>
    public static string Compact(int count)
    {
        if (count >= 10000)
            return NumberFormat.Fixed(count / 1000.0, 0) + "k";
        if (count >= 1000)
            return NumberFormat.Fixed(count / 1000.0, 1) + "k";
        return count.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Thousands separated by commas: 12,345.</summary>
    public static string Thousands(long value) => value.ToString("#,0", CultureInfo.InvariantCulture);

    /// <summary>Title case: every run of letters capitalised, the rest lowercased.</summary>
    public static string Title(string text)
    {
        var chars = text.ToCharArray();
        var previousIsLetter = false;
        for (var i = 0; i < chars.Length; i++)
        {
            var isLetter = char.IsLetter(chars[i]);
            if (isLetter)
                chars[i] = previousIsLetter ? char.ToLowerInvariant(chars[i]) : char.ToUpperInvariant(chars[i]);
            previousIsLetter = isLetter;
        }
        return new string(chars);
    }
}
