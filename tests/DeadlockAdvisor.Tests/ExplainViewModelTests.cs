using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Features.Match.Explain;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Tests.Support;

namespace DeadlockAdvisor.Tests;

public class ExplainViewModelTests
{
    [Fact]
    public void TheTypicalTeamExplainsItselfInTheMatchsNumbers()
    {
        var store = TestStore.Make();
        store.Items["spirit_resist_t1"] = store.Items["spirit_resist_t1"] with { SingleTarget = true };
        var match = new MatchState();
        match.SetRole("heavy_spirit", Role.Enemy);
        match.SetRole("low_hp", Role.Enemy);
        var explain = new ExplainViewModel();

        explain.ShowItem(store, match, "spirit_resist_t1", now: 0);

        // As in ScoringTests: the pair comes to 6 + -4/2 = 4 as targets, and a typical pair to 5/3.
        var typical = explain.Contributions.Single(card => card.Info is not null);
        Assert.Equal("Typical enemy team", typical.HeroName);
        Assert.Contains("a typical team of 2 enemies comes to +1.7", typical.Info);
        Assert.Contains("These enemies come to +4.0 as targets, and +2.3 once the typical team is taken off, "
            + "so these enemies are better targets for it than usual.", typical.Info);
        Assert.Null(typical.NoteTip);
        Assert.All(explain.Contributions.Where(card => card != typical), card => Assert.NotNull(card.NoteTip));
    }
}
