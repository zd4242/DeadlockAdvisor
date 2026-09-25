using System.Globalization;
using System.Numerics;

namespace DeadlockAdvisor.Services.Formats;

/// <summary>
/// Python's float formatting and parsing, reproduced digit for digit so both apps write the same
/// files. Rounding works on the double's exact binary value, half to even, as Python's does.
/// </summary>
public static class NumberFormat
{
    private static readonly CultureInfo _invariant = CultureInfo.InvariantCulture;

    /// <summary>
    /// The data files' number style (<c>_fmt_number</c>): 3 rather than 3.0, anything else as
    /// Python's <c>f"{value:g}"</c>.
    /// </summary>
    public static string Python(double value)
    {
        if (double.IsFinite(value) && value == Math.Truncate(value))
            return new BigInteger(value).ToString(_invariant);
        return G(value);
    }

    /// <summary>Python's <c>format(value, f".{precision}g")</c>.</summary>
    public static string G(double value, int precision = 6)
    {
        if (!double.IsFinite(value))
            return NonFinite(value);
        if (precision == 0)
            precision = 1;

        var (negative, mantissa, exp10) = Exact(value);
        var sign = negative ? "-" : "";
        if (mantissa.IsZero)
            return sign + "0";

        var sciExp = DigitCount(mantissa) - 1 + exp10;
        var k = sciExp - (precision - 1);
        var rounded = RoundAt(mantissa, exp10, k);
        if (DigitCount(rounded) > precision)
        {
            rounded /= 10;
            k++;
            sciExp++;
        }

        var digits = rounded.ToString(_invariant);
        if (sciExp >= -4 && sciExp < precision)
            return sign + StripZeros(PlaceDecimalPoint(digits, -k));

        var mantissaText = digits.Length > 1 ? digits[0] + "." + digits[1..] : digits;
        return sign + StripZeros(mantissaText) + Exponent(sciExp);
    }

    /// <summary>Python's <c>f"{value:.{decimals}f}"</c>.</summary>
    public static string Fixed(double value, int decimals)
    {
        if (!double.IsFinite(value))
            return NonFinite(value);

        var (negative, mantissa, exp10) = Exact(value);
        var rounded = RoundAt(mantissa, exp10, -decimals);
        return (negative ? "-" : "") + PlaceDecimalPoint(rounded.ToString(_invariant), decimals);
    }

    /// <summary>Python's <c>round(value, ndigits)</c> for a float.</summary>
    public static double Round(double value, int ndigits)
    {
        if (!double.IsFinite(value))
            return value;
        return double.Parse(Fixed(value, ndigits), NumberStyles.Float, _invariant);
    }

    /// <summary>Python's <c>repr(value)</c>: the shortest digits that read back as the same double.</summary>
    public static string Repr(double value)
    {
        if (double.IsNaN(value))
            return "nan";
        if (double.IsInfinity(value))
            return value > 0 ? "inf" : "-inf";

        var sign = double.IsNegative(value) ? "-" : "";
        if (value == 0)
            return sign + "0.0";

        var (digits, decimalPoint) = ShortestDigits(Math.Abs(value));
        if (decimalPoint > -4 && decimalPoint <= 16)
        {
            if (decimalPoint <= 0)
                return sign + "0." + new string('0', -decimalPoint) + digits;
            if (decimalPoint >= digits.Length)
                return sign + digits + new string('0', decimalPoint - digits.Length) + ".0";
            return sign + digits[..decimalPoint] + "." + digits[decimalPoint..];
        }

        var mantissaText = digits.Length > 1 ? digits[0] + "." + digits[1..] : digits;
        return sign + mantissaText + Exponent(decimalPoint - 1);
    }

    /// <summary>Python's <c>float(text)</c>, false where that would raise.</summary>
    public static bool TryParseFloat(string? text, out double value)
    {
        value = 0;
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return false;
        if (double.TryParse(trimmed, NumberStyles.Float, _invariant, out value))
            return true;

        // Python also reads inf / infinity / nan in any case, with an optional sign.
        var negative = trimmed[0] == '-';
        var body = trimmed[0] is '-' or '+' ? trimmed[1..] : trimmed;
        switch (body.ToLowerInvariant())
        {
            case "inf":
            case "infinity":
                value = negative ? double.NegativeInfinity : double.PositiveInfinity;
                return true;
            case "nan":
                value = double.NaN;
                return true;
            default:
                return false;
        }
    }

    /// <summary>The data layer's <c>_to_float</c>: blank or unreadable gives the default.</summary>
    public static double ToFloat(string? text, double defaultValue = 0.0) =>
        TryParseFloat(text, out var value) ? value : defaultValue;

    /// <summary>Python's <c>float(text)</c> where a bad value is an error.</summary>
    public static double ParseFloat(string? text) =>
        TryParseFloat(text, out var value) ? value : throw new FormatException($"could not convert string to float: '{text}'");

    /// <summary>Python's <c>int(text)</c> for a string.</summary>
    public static int ParseInt(string? text) =>
        int.TryParse(text?.Trim(), NumberStyles.AllowLeadingSign, _invariant, out var value)
            ? value
            : throw new FormatException($"invalid literal for int() with base 10: '{text}'");

    /// <summary>Python's <c>int(x)</c> for a float: truncates toward zero.</summary>
    public static long Truncate(double value) => (long)Math.Truncate(value);

    private static string NonFinite(double value) =>
        double.IsNaN(value) ? "nan" : value > 0 ? "inf" : "-inf";

    private static string Exponent(int exponent) =>
        "e" + (exponent < 0 ? "-" : "+") + Math.Abs(exponent).ToString("00", _invariant);

    /// <summary>The exact value of a double as sign, mantissa and power of ten: value = mantissa × 10^exp10.</summary>
    private static (bool Negative, BigInteger Mantissa, int Exp10) Exact(double value)
    {
        var bits = BitConverter.DoubleToInt64Bits(value);
        var negative = bits < 0;
        var exponentBits = (int)((bits >> 52) & 0x7FF);
        var fraction = bits & 0xFFFFFFFFFFFFFL;

        BigInteger mantissa;
        int exp2;
        if (exponentBits == 0)
        {
            mantissa = fraction;
            exp2 = -1074;
        }
        else
        {
            mantissa = fraction | (1L << 52);
            exp2 = exponentBits - 1075;
        }

        if (mantissa.IsZero)
            return (negative, BigInteger.Zero, 0);
        if (exp2 >= 0)
            return (negative, mantissa << exp2, 0);
        // m × 2^-n = m × 5^n / 10^n
        return (negative, mantissa * BigInteger.Pow(5, -exp2), exp2);
    }

    /// <summary>mantissa × 10^exp10 rounded half to even to a multiple of 10^k, returned as that multiple.</summary>
    private static BigInteger RoundAt(BigInteger mantissa, int exp10, int k)
    {
        if (exp10 >= k)
            return mantissa * BigInteger.Pow(10, exp10 - k);

        var divisor = BigInteger.Pow(10, k - exp10);
        var quotient = BigInteger.DivRem(mantissa, divisor, out var remainder);
        var twice = remainder * 2;
        if (twice > divisor || (twice == divisor && !quotient.IsEven))
            quotient += 1;
        return quotient;
    }

    private static int DigitCount(BigInteger value) =>
        value.IsZero ? 1 : BigInteger.Abs(value).ToString(_invariant).Length;

    /// <summary>Digits with a decimal point inserted <paramref name="decimals"/> places from the right.</summary>
    private static string PlaceDecimalPoint(string digits, int decimals)
    {
        if (decimals <= 0)
            return digits + new string('0', -decimals);
        if (digits.Length <= decimals)
            digits = new string('0', decimals - digits.Length + 1) + digits;
        return digits[..^decimals] + "." + digits[^decimals..];
    }

    private static string StripZeros(string text) =>
        text.Contains('.') ? text.TrimEnd('0').TrimEnd('.') : text;

    /// <summary>The shortest round-trip digits of a positive double, with value = 0.digits × 10^decimalPoint.</summary>
    private static (string Digits, int DecimalPoint) ShortestDigits(double value)
    {
        var text = value.ToString("R", _invariant);
        var exponent = 0;
        var e = text.IndexOfAny(['E', 'e']);
        if (e >= 0)
        {
            exponent = int.Parse(text[(e + 1)..], NumberStyles.AllowLeadingSign, _invariant);
            text = text[..e];
        }

        var point = text.IndexOf('.');
        var integerLength = point < 0 ? text.Length : point;
        var digits = text.Replace(".", "");
        var leadingZeros = digits.Length - digits.TrimStart('0').Length;
        digits = digits.TrimStart('0').TrimEnd('0');
        return (digits, integerLength - leadingZeros + exponent);
    }
}
