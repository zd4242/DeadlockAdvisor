using DeadlockAdvisor.Services.Formats;
using DeadlockAdvisor.Tests.Support;

namespace DeadlockAdvisor.Tests;

public class NumberFormatTests
{
    public static TheoryData<int> Rows()
    {
        var data = new TheoryData<int>();
        var count = Golden.Json("number_format.json").AsArray().Count;
        for (var i = 0; i < count; i++)
            data.Add(i);
        return data;
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public void MatchesPython(int index)
    {
        var row = Golden.Json("number_format.json")[index]!;
        var value = Golden.Number(row["value"]);

        Assert.Equal(Golden.Text(row["fmt_number"]), NumberFormat.Python(value));
        Assert.Equal(Golden.Text(row["g6"]), NumberFormat.G(value));
        Assert.Equal(Golden.Text(row["g2"]), NumberFormat.G(value, 2));
        Assert.Equal(Golden.Text(row["f3"]), NumberFormat.Fixed(value, 3));
        Assert.Equal(Golden.Text(row["f2"]), NumberFormat.Fixed(value, 2));
        Assert.Equal(Golden.Text(row["repr"]), NumberFormat.Repr(value));
        var round3 = Golden.Number(row["round3"]);
        Assert.Equal(round3, NumberFormat.Round(value, 3));
        Assert.Equal(double.IsNegative(round3), double.IsNegative(NumberFormat.Round(value, 3)));
    }

    [Theory]
    [InlineData("3", 3.0)]
    [InlineData(" 2.5 ", 2.5)]
    [InlineData("1e-5", 1e-5)]
    [InlineData("-inf", double.NegativeInfinity)]
    [InlineData("Infinity", double.PositiveInfinity)]
    [InlineData(".5", 0.5)]
    public void ParsesLikePythonFloat(string text, double expected)
    {
        Assert.Equal(expected, NumberFormat.ToFloat(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nonsense")]
    [InlineData("1,000")]
    public void UnreadableNumbersFallBackToTheDefault(string? text)
    {
        Assert.Equal(7.0, NumberFormat.ToFloat(text, 7.0));
    }
}
