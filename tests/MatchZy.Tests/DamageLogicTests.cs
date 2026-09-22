using MatchZy;
using Xunit;

namespace MatchZy.Tests;

public class DamageLogicTests
{
    [Theory]
    [InlineData(27, 100, 27)]   // an ordinary hit
    [InlineData(100, 100, 100)] // a full-health headshot
    [InlineData(115, 100, 100)] // QA: AWP body shot reported as 115 on a full-health player
    [InlineData(115, 40, 40)]   // the same shot on a player at 40 takes 40
    [InlineData(30, 12, 12)]    // finishing a low player
    [InlineData(0, 100, 0)]
    [InlineData(-5, 100, 0)]
    [InlineData(50, 0, 0)]      // a dead player has nothing left to take
    [InlineData(50, 250, 50)]   // health above the cap is not a thing; the cap holds
    public void DealtIsCappedAtWhatTheVictimHad(int reported, int before, int expected)
    {
        Assert.Equal(expected, DamageLogic.Dealt(reported, before));
    }

    [Fact]
    public void TrackerMeasuresEachHitAgainstTheLast()
    {
        var t = new RoundHealthTracker();
        Assert.Equal(100, t.HealthOf(7));

        Assert.Equal(60, t.Hurt(7, 60, 40));   // 100 -> 40
        Assert.Equal(40, t.Hurt(7, 115, 0));   // an AWP reports 115; the victim had 40
        Assert.Equal(0, t.HealthOf(7));
        Assert.Equal(0, t.Hurt(7, 30, 0));     // already dead: nothing more is taken
    }

    [Fact]
    public void TwoAttackersShareOneVictimsHealth()
    {
        // QA shape: A does 80, B finishes with a shot reported as 50. The report must say
        // A 80 and B 20, not B 50 — the pair's total is the 100 the victim had.
        var t = new RoundHealthTracker();
        Assert.Equal(80, t.Hurt(3, 80, 20));
        Assert.Equal(20, t.Hurt(3, 50, 0));
    }

    [Fact]
    public void ResetBringsEveryoneBackToFullHealth()
    {
        var t = new RoundHealthTracker();
        t.Hurt(3, 90, 10);
        t.Reset();
        Assert.Equal(100, t.HealthOf(3));
        Assert.Equal(90, t.Hurt(3, 90, 10));
    }

    [Fact]
    public void VictimsAreTrackedSeparately()
    {
        var t = new RoundHealthTracker();
        t.Hurt(1, 90, 10);
        Assert.Equal(100, t.HealthOf(2));
        Assert.Equal(100, t.Hurt(2, 130, 0));
    }
}
