namespace MatchZy;

/// <summary>
/// Open rosters: a team registered by name alone, whose players are whoever
/// takes its side. Pure rules with no CounterStrikeSharp dependency, so they can
/// be unit tested outside the game server (see tests/MatchZy.Tests).
///
/// A match config opts in with "open_rosters": true. Only a team whose
/// "players" object is empty is opened by it -- a team that did send a roster
/// stays locked to it, so one config can pair a registered team with one that
/// only gave its name. Without the flag an empty roster means what it always
/// has: everyone who connects is kicked.
///
/// An open team does not stay unassigned. A player on no roster who joins the
/// side that team is on is written into its roster (a claim), and from then on
/// is that team's player exactly as if the config had named them: side swaps,
/// the knife round, reconnects and the stats blocks all follow the roster.
/// Claims stop at players_per_team. Until the match goes live a claimed player
/// may move to the other team if it is open too, so a player who picked the
/// wrong side in warmup is not stuck there.
/// </summary>
public static class OpenRosterLogic
{
    public enum Claim
    {
        /// <summary>The team on that side has a roster of its own.</summary>
        NotOpen,
        /// <summary>The team is open but already has players_per_team players.</summary>
        Full,
        /// <summary>The player takes a place on the team.</summary>
        Take,
    }

    /// <summary>Whether a loaded team takes its players from who joins its side.</summary>
    public static bool IsOpen(bool openRostersRequested, int configuredPlayers) =>
        openRostersRequested && configuredPlayers == 0;

    /// <summary>Whether an open team still has a place for a player.</summary>
    public static bool HasRoom(bool open, int claimed, int playersPerTeam) =>
        open && claimed < Math.Max(1, playersPerTeam);

    /// <summary>What happens to a player on no roster who joins the side this team is on.</summary>
    public static Claim Decide(bool teamOpen, int claimed, int playersPerTeam)
    {
        if (!teamOpen) return Claim.NotOpen;
        return HasRoom(teamOpen, claimed, playersPerTeam) ? Claim.Take : Claim.Full;
    }

    /// <summary>
    /// Whether a player who claimed a place on one open team may move to the
    /// other. Only before the match is live: once a round has been played the
    /// player's rounds and stats belong to the team they played them for.
    /// </summary>
    public static bool CanMove(bool matchStarted, bool fromOpen, bool toOpen, int toClaimed, int playersPerTeam) =>
        !matchStarted && fromOpen && HasRoom(toOpen, toClaimed, playersPerTeam);

    /// <summary>
    /// Whether a player on no roster is let onto the server rather than kicked:
    /// only while an open team still has a place they could take.
    /// </summary>
    public static bool Admits(bool team1Open, int team1Claimed, bool team2Open, int team2Claimed, int playersPerTeam) =>
        HasRoom(team1Open, team1Claimed, playersPerTeam) || HasRoom(team2Open, team2Claimed, playersPerTeam);
}
