using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using Newtonsoft.Json.Linq;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Modules.Utils;

namespace MatchZy
{

    public class Team 
    {
        [JsonPropertyName("id")]
        public string id = "";

        [JsonPropertyName("teamname")]
        public required string teamName;

        [JsonPropertyName("teamflag")]
        public string teamFlag = "";

        [JsonPropertyName("teamtag")]
        public string teamTag = "";

        [JsonPropertyName("teamplayers")]
        public JToken? teamPlayers;

        // An open roster (OpenRosterLogic): the config named no players and asked for
        // open_rosters, so teamPlayers is filled by the players who join this team's side.
        // Serialised with the team so a restored backup keeps both the flag and the claims.
        [JsonPropertyName("openroster")]
        public bool openRoster = false;

        [JsonIgnore, Newtonsoft.Json.JsonIgnore]
        public HashSet<CCSPlayerController> coach = [];

        [JsonPropertyName("seriesscore")]
        public int seriesScore = 0;
    }

    public partial class MatchZy
    {
        [ConsoleCommand("css_coach", "Sets coach for the requested team")]
        public void OnCoachCommand(CCSPlayerController? player, CommandInfo command) 
        {
            HandleCoachCommand(player, command.ArgString);
        }

        [ConsoleCommand("css_uncoach", "Sets coach for the requested team")]
        public void OnUnCoachCommand(CCSPlayerController? player, CommandInfo? command)
        {
            if (player == null || !player.PlayerPawn.IsValid) return;
            if (isPractice) {
                ReplyToUserCommand(player, "Uncoach command can only be used in match mode!");
                return;
            }

            if (matchzyTeam1.coach.Contains(player)) {
                player.Clan = "";
                matchzyTeam1.coach.Remove(player);
                SetPlayerVisible(player);
            }
            else if (matchzyTeam2.coach.Contains(player)) {
                player.Clan = "";
                matchzyTeam2.coach.Remove(player);
                SetPlayerVisible(player);
            }
            else {
                ReplyToUserCommand(player, "You are not coaching any team!");
                return;
            }

            if (player.InGameMoneyServices != null) player.InGameMoneyServices.Account = 0;

            ReplyToUserCommand(player, "You are now not coaching any team!");
        }

        [ConsoleCommand("matchzy_addplayer", "Adds player to the provided team")]
        [ConsoleCommand("get5_addplayer", "Adds player to the provided team")]
        public void OnAddPlayerCommand(CCSPlayerController? player, CommandInfo? command)
        {
            if (player != null || command == null) return;
            if (!isMatchSetup) {
                command.ReplyToCommand("No match is setup!");
                return;
            }
            if (IsHalfTimePhase())
            {
                command.ReplyToCommand("Cannot add players during halftime. Please wait until the next round starts.");
                return;
            }
            if (command.ArgCount < 3)
            {
                command.ReplyToCommand("Usage: matchzy_addplayer <steam64> <team> \"<name>\"");
                return; 
            }

            string playerSteamId = command.ArgByIndex(1);
            string playerTeam = command.ArgByIndex(2);
            string playerName = command.ArgByIndex(3);
            bool success;
            if (playerTeam == "team1")
            {
                success = AddPlayerToTeam(playerSteamId, playerName, matchzyTeam1.teamPlayers);
            } else if (playerTeam == "team2")
            {
                success = AddPlayerToTeam(playerSteamId, playerName, matchzyTeam2.teamPlayers);
            } else if (playerTeam == "spec")
            {
                success = AddPlayerToTeam(playerSteamId, playerName, matchConfig.Spectators);
            } else 
            {
                command.ReplyToCommand("Unknown team: must be one of team1, team2, spec");
                return; 
            }
            if (!success)
            {
                command.ReplyToCommand($"Failed to add player {playerName} to {playerTeam}. They may already be on a team or you provided an invalid Steam ID.");
                return;
            }
            command.ReplyToCommand($"Player {playerName} added to {playerTeam} successfully!");
        }

        [ConsoleCommand("matchzy_removeplayer", "Removes the player from all the teams")]
        [ConsoleCommand("get5_removeplayer", "Removes the player from all the teams")]
        [CommandHelper(minArgs: 1, usage: "<steam64>")]
        public void OnRemovePlayerCommand(CCSPlayerController? player, CommandInfo? command)
        {
            if (player != null || command == null) return;
            if (!isMatchSetup) {
                command.ReplyToCommand("No match is setup!");
                return;
            }
            if (IsHalfTimePhase())
            {
                command.ReplyToCommand("Cannot remove players during halftime. Please wait until the next round starts.");
                return;
            }

            string arg = command.GetArg(1);

            if (!ulong.TryParse(arg, out ulong steamId))
            {
                command.ReplyToCommand($"Invalid Steam64");
            }

            bool success = RemovePlayerFromTeam(steamId.ToString());
            if (success)
            {
                command.ReplyToCommand($"Successfully removed player {steamId}");
                CCSPlayerController? removedPlayer = Utilities.GetPlayerFromSteamId(steamId);
                if (IsPlayerValid(removedPlayer))
                {
                    Log($"Kicking player {removedPlayer!.PlayerName} - Not a player in this game (removed).");
                    PrintToAllChat($"Kicking player {removedPlayer!.PlayerName} - Not a player in this game.");
                    KickPlayer(removedPlayer);
                }
            }
            else
            {
                command.ReplyToCommand($"Player {steamId} not found in any team or the Steam ID was invalid.");
            }
        }

        public bool AddPlayerToTeam(string steamId, string name, JToken? team)
        {
            if (matchzyTeam1.teamPlayers != null && matchzyTeam1.teamPlayers[steamId] != null) return false;
            if (matchzyTeam2.teamPlayers != null && matchzyTeam2.teamPlayers[steamId] != null) return false;
            if (matchConfig.Spectators != null && matchConfig.Spectators[steamId] != null) return false;

            if (team is JObject jObjectTeam)
            {
                jObjectTeam.Add(steamId, name);
                LoadClientNames();
                return true;
            }
            else if (team is JArray jArrayTeam)
            {
                jArrayTeam.Add(name);
                LoadClientNames();
                return true;
            }
            return false;
        }

        public bool RemovePlayerFromTeam(string steamId)
        {
            List<JToken?> teams = [matchzyTeam1.teamPlayers, matchzyTeam2.teamPlayers, matchConfig.Spectators];

            foreach (var team in teams)
            {
                if (team is null) continue;
                if (team is JObject jObjectTeam)
                {
                    jObjectTeam.Remove(steamId);
                    return true;
                }
                else if (team is JArray jArrayTeam)
                {
                    jArrayTeam.Remove(steamId);
                    return true;
                }
            }
            return false;
        }

        // ---- Open rosters (OpenRosterLogic) ----

        private static int RosterCount(JToken? players) => players is JObject o ? o.Count : 0;

        private Team? TeamOnSide(CsTeam side)
        {
            string key = side == CsTeam.CounterTerrorist ? "CT" : side == CsTeam.Terrorist ? "TERRORIST" : "";
            return key != "" && reverseTeamSides.TryGetValue(key, out var team) ? team : null;
        }

        /// <summary>
        /// Whether a player on no roster may stay on the server: only while an open team
        /// still has a place for them. Otherwise they are kicked as before.
        /// </summary>
        private bool OpenRosterAdmits() =>
            OpenRosterLogic.Admits(
                matchzyTeam1.openRoster, RosterCount(matchzyTeam1.teamPlayers),
                matchzyTeam2.openRoster, RosterCount(matchzyTeam2.teamPlayers),
                matchConfig.PlayersPerTeam);

        /// <summary>
        /// A player on no roster joining a side: if the team on that side is open and has
        /// room, write them into its roster. Returns whether they now have a team.
        /// </summary>
        private bool TryClaimOpenRosterPlace(CCSPlayerController player, CsTeam side)
        {
            Team? team = TeamOnSide(side);
            if (team == null) return false;

            var decision = OpenRosterLogic.Decide(team.openRoster, RosterCount(team.teamPlayers), matchConfig.PlayersPerTeam);
            if (decision == OpenRosterLogic.Claim.Full)
            {
                PrintToPlayerChat(player, $"{team.teamName} already has {matchConfig.PlayersPerTeam} players.");
            }
            if (decision != OpenRosterLogic.Claim.Take) return false;

            team.teamPlayers ??= new JObject();
            if (!AddPlayerToTeam(player.SteamID.ToString(), player.PlayerName, team.teamPlayers)) return false;

            Log($"[OpenRoster] {player.PlayerName} ({player.SteamID}) joins {team.teamName} ({RosterCount(team.teamPlayers)}/{matchConfig.PlayersPerTeam}).");
            PrintToAllChat($"{player.PlayerName} joins {team.teamName}.");
            return true;
        }

        /// <summary>
        /// A player who claimed a place on one open team joining the other team's side
        /// before the match is live: move their claim. Returns whether they moved.
        /// </summary>
        private bool TryMoveOpenRosterPlace(CCSPlayerController player, CsTeam side)
        {
            Team? to = TeamOnSide(side);
            if (to == null) return false;
            Team from = to == matchzyTeam1 ? matchzyTeam2 : matchzyTeam1;

            string steamId = player.SteamID.ToString();
            if (from.teamPlayers is not JObject fromPlayers || fromPlayers[steamId] == null) return false;
            if (!OpenRosterLogic.CanMove(matchStarted, from.openRoster, to.openRoster, RosterCount(to.teamPlayers), matchConfig.PlayersPerTeam))
            {
                return false;
            }

            to.teamPlayers ??= new JObject();
            if (to.teamPlayers is not JObject toPlayers) return false;

            JToken name = fromPlayers[steamId]!;
            fromPlayers.Remove(steamId);
            toPlayers.Add(steamId, name);
            LoadClientNames();

            Log($"[OpenRoster] {player.PlayerName} ({steamId}) moves from {from.teamName} to {to.teamName}.");
            PrintToAllChat($"{player.PlayerName} joins {to.teamName}.");
            return true;
        }
    }
}
