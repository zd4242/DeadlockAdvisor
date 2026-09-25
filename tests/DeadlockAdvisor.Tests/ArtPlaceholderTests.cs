using DeadlockAdvisor.Controls.Art;
using DeadlockAdvisor.Tests.Support;
using DeadlockAdvisor.Theme;
using static DeadlockAdvisor.Tests.Support.Golden;

namespace DeadlockAdvisor.Tests;

public class ArtPlaceholderTests
{
    [Fact]
    public void PlaceholderColoursAndInitialsMatchThePythonApp()
    {
        foreach (var row in Items(Json("art_placeholders.json")))
        {
            var key = Text(row["key"]);
            Assert.Equal(Text(row["color"]), Palette.Hex(ArtPlaceholder.ColorFor(key)));
            Assert.Equal(Text(row["initials"]), ArtPlaceholder.Initials(Text(row["name"])));
        }
    }
}
