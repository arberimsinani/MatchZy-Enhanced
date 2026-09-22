using System;
using System.Net;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Timers;

namespace MatchZy
{
    public partial class MatchZy
    {
        // Health endpoint: the plugin's own answer to "is MatchZy loaded and working on this
        // server", for a control plane that until now had to infer it from journal lines.
        //
        // It listens on an abstract Unix socket named after the config scope as soon as that
        // scope is settled, with no configuration at all, and additionally on a TCP port when
        // matchzy_health_port is set. The listener threads never touch the game; they serialise
        // the snapshot the game thread refreshes every couple of seconds.
        public FakeConVar<int> healthPort = new("matchzy_health_port", "TCP port for the plugin's HTTP health endpoint (GET /health). 0 (default) = off. The abstract Unix socket @matchzy-health-<scope> is always on regardless. Prefer the start argument +matchzy_health_port <port> on a shared install.", 0);
        public FakeConVar<string> healthBind = new("matchzy_health_bind", "Address the TCP health endpoint binds to. Default 127.0.0.1; use 0.0.0.0 to answer on the LAN. The response carries no secrets.", "127.0.0.1");

        private HealthListener? healthSocketListener;
        private HealthListener? healthTcpListener;
        private CounterStrikeSharp.API.Modules.Timers.Timer? healthRefreshTimer;
        private volatile HealthSnapshot? healthSnapshot;
        private readonly long healthLoadedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        private long healthLastDbCheckAt = 0;
        private volatile bool healthDbCheckInFlight = false;
        private volatile HealthDbState healthDb = new(true, "", null, null);

        /// <summary>The last database check, written whole by the pool thread that ran it.</summary>
        private sealed record HealthDbState(bool Ok, string Type, string? Error, int? Queued);
        private string healthScope = "";
        private string healthScopeSource = "";

        /// <summary>
        /// Starts whatever is not running yet. Idempotent, and called from Load and again after
        /// the persistent config loads, because the socket name needs the final scope and that
        /// can arrive at either point.
        /// </summary>
        private void StartHealthEndpoint()
        {
            try
            {
                if (healthSocketListener == null && HealthProtocol.AbstractSocketsSupported)
                {
                    healthScope = database.ServerScope;
                    try { healthScopeSource = ResolveServerConfigScope().Description; } catch { /* optional */ }
                    string name = HealthProtocol.SocketName(healthScope);
                    healthSocketListener = HealthListener.ListenAbstract(name, RenderHealth, HealthLog);
                    Console.WriteLine($"[MatchZy] Health endpoint listening on abstract socket @{name} (curl --abstract-unix-socket {name} http://matchzy/health)");
                }

                if (healthRefreshTimer == null)
                {
                    RefreshHealthSnapshot();
                    healthRefreshTimer = AddTimer(HealthProtocol.RefreshIntervalSeconds, RefreshHealthSnapshot, TimerFlags.REPEAT);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MatchZy] Health endpoint (socket) could not start: {ex.Message}");
            }

            StartHealthTcpEndpoint();
        }

        /// <summary>
        /// The TCP listener follows the port: on when the command line or the convar names one,
        /// rebound when the convar changes, off at 0.
        /// </summary>
        private void StartHealthTcpEndpoint()
        {
            int port = ServerIdentity.ParseHealthPort(processCommandLineArgs) ?? healthPort.Value;
            try
            {
                if (healthTcpListener != null)
                {
                    if (port != 0 && healthTcpListener.Port == port) return;
                    healthTcpListener.Dispose();
                    healthTcpListener = null;
                }
                if (port <= 0) return;
                if (port > 65535)
                {
                    Console.WriteLine($"[MatchZy] matchzy_health_port {port} is not a valid port; health TCP endpoint stays off.");
                    return;
                }
                string bind = string.IsNullOrWhiteSpace(healthBind.Value) ? "127.0.0.1" : healthBind.Value.Trim();
                if (!IPAddress.TryParse(bind, out var address))
                {
                    Console.WriteLine($"[MatchZy] matchzy_health_bind '{bind}' is not an IP address; health TCP endpoint stays off.");
                    return;
                }
                healthTcpListener = HealthListener.ListenTcp(address, port, RenderHealth, HealthLog);
                Console.WriteLine($"[MatchZy] Health endpoint listening on http://{healthTcpListener.Description}/health");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MatchZy] Health endpoint (tcp :{port}) could not start: {ex.Message}");
            }
        }

        private void StopHealthEndpoint()
        {
            try { healthSocketListener?.Dispose(); } catch { /* best effort */ }
            try { healthTcpListener?.Dispose(); } catch { /* best effort */ }
            healthSocketListener = null;
            healthTcpListener = null;
            healthRefreshTimer?.Kill();
            healthRefreshTimer = null;
        }

        /// <summary>Called from a listener thread: only the snapshot is read, never the game.</summary>
        private string RenderHealth()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var s = healthSnapshot;
            return s == null
                ? HealthProtocol.RenderStarting(ModuleVersion, healthScope, healthLoadedAt, now)
                : HealthProtocol.Render(s, now);
        }

        private void HealthLog(string line) => Log(line);

        /// <summary>Game thread. Everything CounterStrikeSharp-backed is read here and nowhere else.</summary>
        private void RefreshHealthSnapshot()
        {
            try
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (now - healthLastDbCheckAt >= HealthProtocol.DatabaseCheckIntervalSeconds && !healthDbCheckInFlight)
                {
                    // Opening a connection is a file open on SQLite and a TCP connect with a
                    // 10 s timeout on MySQL; neither belongs on the game thread. The scope is
                    // resolved here, where convars may be read, and the check runs on the pool
                    // and publishes its result whole; the snapshot reads whatever is latest.
                    healthLastDbCheckAt = now;
                    healthDbCheckInFlight = true;
                    string scope = database.ServerScope;
                    Task.Run(() =>
                    {
                        try
                        {
                            var (ok, dbType, error) = database.CheckHealth();
                            int queued = ok ? database.CountPendingEvents(scope) : -1;
                            healthDb = new HealthDbState(ok, dbType, ok ? null : error, queued < 0 ? null : queued);
                        }
                        catch (Exception ex)
                        {
                            healthDb = new HealthDbState(false, healthDb.Type, ex.Message, null);
                        }
                        finally
                        {
                            healthDbCheckInFlight = false;
                        }
                    });
                }
                var db = healthDb;

                string map;
                try { map = Server.MapName ?? ""; } catch { map = ""; }

                healthSnapshot = new HealthSnapshot
                {
                    Version = ModuleVersion,
                    Scope = healthScope,
                    ScopeSource = healthScopeSource,
                    LoadedAt = healthLoadedAt,
                    SnapshotAt = now,
                    Map = map,
                    Players = connectedPlayers,
                    MatchLoaded = isMatchSetup,
                    MatchId = liveMatchId > 0 ? liveMatchId : null,
                    Status = tournamentStatus?.Value ?? "idle",
                    MapNumber = matchConfig.CurrentMapNumber,
                    Paused = isPaused,
                    Simulation = isSimulationMode,
                    DatabaseOk = db.Ok,
                    DatabaseType = db.Type,
                    DatabaseError = db.Error,
                    EventsEnabled = eventsEnabled.Value,
                    RemoteLogConfigured = !string.IsNullOrEmpty(matchConfig.RemoteLogURL),
                    EventsQueued = db.Queued,
                    DemoUploadConfigured = !string.IsNullOrEmpty(demoUploadURL),
                };
            }
            catch (Exception ex)
            {
                Log($"[Health] snapshot failed: {ex.Message}");
            }
        }
    }
}
