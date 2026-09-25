using MatchZy;
using Xunit;

namespace MatchZy.Tests;

public class OpenRosterLogicTests
{
    [Theory]
    [InlineData(true, 0, true)]
    [InlineData(true, 5, false)]  // a team that sent a roster stays locked to it
    [InlineData(false, 0, false)] // without the flag an empty roster kicks, as it always did
    public void OnlyAnEmptyRosterUnderTheFlagIsOpen(bool requested, int configured, bool open)
    {
        Assert.Equal(open, OpenRosterLogic.IsOpen(requested, configured));
    }

    [Theory]
    [InlineData(true, 0, 5, OpenRosterLogic.Claim.Take)]
    [InlineData(true, 4, 5, OpenRosterLogic.Claim.Take)]
    [InlineData(true, 5, 5, OpenRosterLogic.Claim.Full)]
    [InlineData(true, 2, 2, OpenRosterLogic.Claim.Full)] // wingman
    [InlineData(false, 0, 5, OpenRosterLogic.Claim.NotOpen)]
    public void ClaimsStopAtPlayersPerTeam(bool open, int claimed, int perTeam, OpenRosterLogic.Claim expected)
    {
        Assert.Equal(expected, OpenRosterLogic.Decide(open, claimed, perTeam));
    }

    [Fact]
    public void AZeroPlayersPerTeamStillTakesOne()
    {
        Assert.Equal(OpenRosterLogic.Claim.Take, OpenRosterLogic.Decide(true, 0, 0));
        Assert.Equal(OpenRosterLogic.Claim.Full, OpenRosterLogic.Decide(true, 1, 0));
    }

    [Theory]
    [InlineData(false, true, true, 4, true)]
    [InlineData(true, true, true, 0, false)]  // live: the rounds belong to the team they were played for
    [InlineData(false, false, true, 0, false)] // a rostered player cannot leave their team
    [InlineData(false, true, false, 0, false)] // nor join a team that sent a roster
    [InlineData(false, true, true, 5, false)]  // the other team is full
    public void MovingBetweenOpenTeamsOnlyBeforeLive(bool started, bool fromOpen, bool toOpen, int toClaimed, bool allowed)
    {
        Assert.Equal(allowed, OpenRosterLogic.CanMove(started, fromOpen, toOpen, toClaimed, 5));
    }

    [Theory]
    [InlineData(true, 5, true, 4, true)]
    [InlineData(true, 5, true, 5, false)]
    [InlineData(false, 0, true, 3, true)]
    [InlineData(false, 0, false, 0, false)]
    public void AnUnknownPlayerIsAdmittedWhileAPlaceIsFree(bool t1Open, int t1, bool t2Open, int t2, bool admitted)
    {
        Assert.Equal(admitted, OpenRosterLogic.Admits(t1Open, t1, t2Open, t2, 5));
    }
}
