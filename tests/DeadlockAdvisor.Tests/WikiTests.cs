using DeadlockAdvisor.Core;

namespace DeadlockAdvisor.Tests;

public class WikiTests
{
    [Theory]
    [InlineData("Abrams", "https://deadlock.wiki/Abrams")]
    [InlineData("Grey Talon", "https://deadlock.wiki/Grey_Talon")]
    [InlineData("Mo & Krill", "https://deadlock.wiki/Mo_%26_Krill")]
    [InlineData("Diviner's Kevlar", "https://deadlock.wiki/Diviner%27s_Kevlar")]
    [InlineData("High-Velocity Rounds", "https://deadlock.wiki/High-Velocity_Rounds")]
    public void PagesAreTitledWithUnderscoresAndEscaped(string title, string url) =>
        Assert.Equal(url, Wiki.PageUrl(title).AbsoluteUri);
}
