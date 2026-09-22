using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace MatchZy;

/// <summary>
/// What the plugin knows about itself at one moment, captured on the game thread.
///
/// The health listener answers from a background thread, and nothing in CounterStrikeSharp may
/// be touched from there, so the plugin refreshes one of these every couple of seconds and the
/// listener serialises the last one it was handed. That indirection is also the check: a
/// snapshot that stops being refreshed means the game thread has stopped ticking, which is the
/// one failure a process that still accepts connections cannot report any other way.
/// </summary>
public sealed class HealthSnapshot
{
    public string Plugin { get; init; } = "MatchZy";
    public string Variant { get; init; } = "enhanced";
    public string Version { get; init; } = "";
    public string Scope { get; init; } = "";
    public string ScopeSource { get; init; } = "";
    public long LoadedAt { get; init; }
    public long SnapshotAt { get; init; }
    public string Map { get; init; } = "";
    public int Players { get; init; }

    public bool MatchLoaded { get; init; }
    public long? MatchId { get; init; }
    public string Status { get; init; } = "idle";
    public int MapNumber { get; init; }
    public bool Paused { get; init; }
    public bool Simulation { get; init; }

    public bool DatabaseOk { get; init; } = true;
    public string DatabaseType { get; init; } = "";
    public string? DatabaseError { get; init; }

    public bool EventsEnabled { get; init; }
    public bool RemoteLogConfigured { get; init; }
    /// <summary>Events waiting in the retry queue; null when the count could not be read.</summary>
    public int? EventsQueued { get; init; }
    public bool DemoUploadConfigured { get; init; }
}

/// <summary>The wire shape of <c>GET /health</c>. Field names are the contract with the agent.</summary>
public sealed class HealthReport
{
    [JsonPropertyName("ok")] public bool Ok { get; init; }
    [JsonPropertyName("problems")] public List<string> Problems { get; init; } = new();
    [JsonPropertyName("plugin")] public string Plugin { get; init; } = "";
    [JsonPropertyName("variant")] public string Variant { get; init; } = "";
    [JsonPropertyName("version")] public string Version { get; init; } = "";
    [JsonPropertyName("scope")] public string Scope { get; init; } = "";
    [JsonPropertyName("scope_source")] public string ScopeSource { get; init; } = "";
    [JsonPropertyName("loaded_at")] public long LoadedAt { get; init; }
    [JsonPropertyName("uptime_seconds")] public long UptimeSeconds { get; init; }
    [JsonPropertyName("now")] public long Now { get; init; }
    [JsonPropertyName("snapshot_at")] public long SnapshotAt { get; init; }
    [JsonPropertyName("snapshot_age_seconds")] public long SnapshotAgeSeconds { get; init; }
    [JsonPropertyName("map")] public string Map { get; init; } = "";
    [JsonPropertyName("players")] public int Players { get; init; }
    [JsonPropertyName("match")] public HealthMatch Match { get; init; } = new();
    [JsonPropertyName("database")] public HealthDatabase Database { get; init; } = new();
    [JsonPropertyName("events")] public HealthEvents Events { get; init; } = new();
    [JsonPropertyName("demo_upload_configured")] public bool DemoUploadConfigured { get; init; }
}

public sealed class HealthMatch
{
    [JsonPropertyName("loaded")] public bool Loaded { get; init; }
    [JsonPropertyName("id")] public long? Id { get; init; }
    [JsonPropertyName("status")] public string Status { get; init; } = "idle";
    [JsonPropertyName("map_number")] public int MapNumber { get; init; }
    [JsonPropertyName("paused")] public bool Paused { get; init; }
    [JsonPropertyName("simulation")] public bool Simulation { get; init; }
}

public sealed class HealthDatabase
{
    [JsonPropertyName("ok")] public bool Ok { get; init; }
    [JsonPropertyName("type")] public string Type { get; init; } = "";
    [JsonPropertyName("error")] public string? Error { get; init; }
}

public sealed class HealthEvents
{
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }
    [JsonPropertyName("remote_log_configured")] public bool RemoteLogConfigured { get; init; }
    [JsonPropertyName("queued")] public int? Queued { get; init; }
}

/// <summary>
/// The rules of the health endpoint, free of CounterStrikeSharp so they can be unit tested:
/// where it listens, what it accepts, what it answers and when it calls itself unhealthy.
/// </summary>
public static class HealthProtocol
{
    /// <summary>
    /// A snapshot older than this means the game thread has stopped refreshing it. The plugin
    /// refreshes every <see cref="RefreshIntervalSeconds"/>, so a whole match's worth of hitches
    /// fits inside this window and only a real stall crosses it.
    /// </summary>
    public const int StaleAfterSeconds = 20;

    /// <summary>How often the plugin captures a new snapshot on the game thread.</summary>
    public const float RefreshIntervalSeconds = 2.0f;

    /// <summary>How often the snapshot re-checks the database, which costs a connection.</summary>
    public const int DatabaseCheckIntervalSeconds = 15;

    /// <summary>Longest request head the listener reads before answering 400.</summary>
    public const int MaxRequestBytes = 8192;

    private const string SocketPrefix = "matchzy-health-";

    /// <summary>
    /// The abstract Unix socket name for a config scope, without the leading NUL that marks the
    /// abstract namespace. The agent on the same host computes the same name from the ref it put
    /// in <c>+matchzy_config_scope</c>, which is why no port has to be agreed on, and an operator
    /// can read it with <c>curl --abstract-unix-socket matchzy-health-&lt;scope&gt; http://x/health</c>.
    /// Bounded well inside the 108-byte limit on socket names.
    /// </summary>
    public static string SocketName(string scope)
    {
        string name = SocketPrefix + ServerIdentity.Sanitize(scope);
        return name.Length > 100 ? name.Substring(0, 100) : name;
    }

    /// <summary>Abstract socket names exist on Linux only.</summary>
    public static bool AbstractSocketsSupported => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    /// <summary>
    /// Reads the request line out of an HTTP request head. Null method when the head is not a
    /// request at all.
    /// </summary>
    public static (string? Method, string Path) ParseRequestLine(string head)
    {
        if (string.IsNullOrEmpty(head)) return (null, "");
        int eol = head.IndexOf('\n');
        string line = (eol < 0 ? head : head.Substring(0, eol)).TrimEnd('\r');
        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // "METHOD /path" or "METHOD /path HTTP/x.y"; anything else is not a request line.
        if (parts.Length < 2 || parts.Length > 3 || !parts[1].StartsWith("/", StringComparison.Ordinal)
            || (parts.Length == 3 && !parts[2].StartsWith("HTTP/", StringComparison.Ordinal)))
        {
            return (null, "");
        }
        string path = parts[1];
        int q = path.IndexOf('?');
        if (q >= 0) path = path.Substring(0, q);
        return (parts[0].ToUpperInvariant(), path);
    }

    /// <summary>
    /// Builds the whole HTTP response for a request. <paramref name="body"/> is only asked for
    /// when the request is one the endpoint answers, so a bad request costs no serialisation.
    /// </summary>
    public static byte[] Respond(string? method, string path, Func<string> body)
    {
        if (method == null)
        {
            return Message(400, "Bad Request", "{\"error\":\"malformed request\"}", true);
        }
        if (method != "GET" && method != "HEAD")
        {
            return Message(405, "Method Not Allowed", "{\"error\":\"GET only\"}", true);
        }
        if (path != "/health" && path != "/" && path != "/health/")
        {
            return Message(404, "Not Found", "{\"error\":\"not found; try /health\"}", true);
        }
        return Message(200, "OK", body(), method == "GET");
    }

    private static byte[] Message(int status, string reason, string json, bool includeBody)
    {
        byte[] payload = Encoding.UTF8.GetBytes(json);
        var head = new StringBuilder()
            .Append("HTTP/1.1 ").Append(status.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(reason).Append("\r\n")
            .Append("Content-Type: application/json; charset=utf-8\r\n")
            .Append("Content-Length: ").Append(payload.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("Connection: close\r\n\r\n");
        byte[] headBytes = Encoding.ASCII.GetBytes(head.ToString());
        if (!includeBody) return headBytes;
        byte[] all = new byte[headBytes.Length + payload.Length];
        Buffer.BlockCopy(headBytes, 0, all, 0, headBytes.Length);
        Buffer.BlockCopy(payload, 0, all, headBytes.Length, payload.Length);
        return all;
    }

    /// <summary>
    /// Turns the last snapshot into the report, judged at <paramref name="now"/>. The verdict is
    /// made here and not by the caller so every consumer agrees on what unhealthy means: a stale
    /// snapshot (the game thread is not ticking) or a database the plugin cannot reach.
    /// </summary>
    public static HealthReport Report(HealthSnapshot s, long now)
    {
        long age = Math.Max(0, now - s.SnapshotAt);
        var problems = new List<string>();
        if (age > StaleAfterSeconds)
        {
            problems.Add($"the game thread has not refreshed the snapshot for {age} s");
        }
        if (!s.DatabaseOk)
        {
            problems.Add("database: " + (string.IsNullOrWhiteSpace(s.DatabaseError) ? "unreachable" : s.DatabaseError));
        }
        return new HealthReport
        {
            Ok = problems.Count == 0,
            Problems = problems,
            Plugin = s.Plugin,
            Variant = s.Variant,
            Version = s.Version,
            Scope = s.Scope,
            ScopeSource = s.ScopeSource,
            LoadedAt = s.LoadedAt,
            UptimeSeconds = Math.Max(0, now - s.LoadedAt),
            Now = now,
            SnapshotAt = s.SnapshotAt,
            SnapshotAgeSeconds = age,
            Map = s.Map,
            Players = s.Players,
            Match = new HealthMatch
            {
                Loaded = s.MatchLoaded,
                Id = s.MatchId,
                Status = s.Status,
                MapNumber = s.MapNumber,
                Paused = s.Paused,
                Simulation = s.Simulation,
            },
            Database = new HealthDatabase { Ok = s.DatabaseOk, Type = s.DatabaseType, Error = s.DatabaseError },
            Events = new HealthEvents
            {
                Enabled = s.EventsEnabled,
                RemoteLogConfigured = s.RemoteLogConfigured,
                Queued = s.EventsQueued,
            },
            DemoUploadConfigured = s.DemoUploadConfigured,
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>The body of a 200: the report as JSON.</summary>
    public static string Render(HealthSnapshot s, long now) => JsonSerializer.Serialize(Report(s, now), JsonOptions);

    /// <summary>
    /// The body when the plugin has loaded but has not captured a snapshot yet — the first
    /// refresh is a couple of seconds after Load. Not an error: answering at all already says the
    /// plugin is running, which is what the agent asks first.
    /// </summary>
    public static string RenderStarting(string version, string scope, long loadedAt, long now) =>
        JsonSerializer.Serialize(new HealthReport
        {
            Ok = true,
            Problems = new List<string>(),
            Plugin = "MatchZy",
            Variant = "enhanced",
            Version = version,
            Scope = scope,
            ScopeSource = "",
            LoadedAt = loadedAt,
            UptimeSeconds = Math.Max(0, now - loadedAt),
            Now = now,
            SnapshotAt = 0,
            SnapshotAgeSeconds = 0,
            Match = new HealthMatch(),
            Database = new HealthDatabase { Ok = true },
            Events = new HealthEvents(),
        }, JsonOptions);
}

/// <summary>
/// A minimal HTTP/1.1 responder on one socket: accept, read the request head, answer, close.
///
/// Hand-rolled over <see cref="Socket"/> rather than <c>HttpListener</c> because the endpoint
/// has to sit on an abstract Unix socket — the only address the agent can find without a port
/// being agreed on — and <c>HttpListener</c> speaks TCP only. The same class serves the optional
/// TCP port. One connection is one request; there is no keep-alive to get wrong.
/// </summary>
public sealed class HealthListener : IDisposable
{
    private readonly Socket socket;
    private readonly Func<string> body;
    private readonly Action<string> log;
    private readonly Thread thread;
    private volatile bool stopped;

    /// <summary>Where this listener is bound, for the log line and the status page.</summary>
    public string Description { get; }

    /// <summary>The TCP port actually bound; 0 for a Unix socket. Lets a test bind port 0.</summary>
    public int Port => socket.LocalEndPoint is IPEndPoint ip ? ip.Port : 0;

    private HealthListener(Socket socket, string description, Func<string> body, Action<string> log)
    {
        this.socket = socket;
        this.body = body;
        this.log = log;
        Description = description;
        thread = new Thread(AcceptLoop) { IsBackground = true, Name = "MatchZy health " + description };
        thread.Start();
    }

    /// <summary>Listens on an abstract Unix socket. Linux only; see <see cref="HealthProtocol.SocketName"/>.</summary>
    public static HealthListener ListenAbstract(string name, Func<string> body, Action<string> log)
    {
        var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            // A leading NUL puts the name in the abstract namespace: no file on disk, nothing to
            // clean up, gone the moment the process is. The name is what the agent connects to.
            s.Bind(new UnixDomainSocketEndPoint("\0" + name));
            s.Listen(16);
        }
        catch
        {
            s.Dispose();
            throw;
        }
        return new HealthListener(s, "@" + name, body, log);
    }

    /// <summary>Listens on TCP for hand-built servers and operators with curl.</summary>
    public static HealthListener ListenTcp(IPAddress address, int port, Func<string> body, Action<string> log)
    {
        var s = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            s.Bind(new IPEndPoint(address, port));
            s.Listen(16);
        }
        catch
        {
            s.Dispose();
            throw;
        }
        var bound = (IPEndPoint)s.LocalEndPoint!;
        return new HealthListener(s, $"{bound.Address}:{bound.Port}", body, log);
    }

    private void AcceptLoop()
    {
        while (!stopped)
        {
            Socket client;
            try
            {
                client = socket.Accept();
            }
            catch (Exception ex)
            {
                if (stopped) return;
                log($"[Health] accept on {Description} failed: {ex.Message}");
                Thread.Sleep(250);
                continue;
            }
            ThreadPool.QueueUserWorkItem(_ => Serve(client));
        }
    }

    private void Serve(Socket client)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 3000;
                client.SendTimeout = 3000;
                // TCP only: the option does not exist on a Unix socket and setting it throws,
                // which closed the connection before the reply was written.
                if (client.ProtocolType == ProtocolType.Tcp) client.NoDelay = true;

                var head = new byte[HealthProtocol.MaxRequestBytes];
                int total = 0;
                while (total < head.Length && !HasHeadTerminator(head, total))
                {
                    int n = client.Receive(head, total, head.Length - total, SocketFlags.None);
                    if (n <= 0) break;
                    total += n;
                }
                string text = Encoding.ASCII.GetString(head, 0, total);
                var (method, path) = HealthProtocol.ParseRequestLine(text);
                byte[] response = HealthProtocol.Respond(method, path, body);
                int sent = 0;
                while (sent < response.Length)
                {
                    sent += client.Send(response, sent, response.Length - sent, SocketFlags.None);
                }
                try { client.Shutdown(SocketShutdown.Both); } catch { /* peer may be gone */ }
            }
            catch (Exception ex)
            {
                if (!stopped) log($"[Health] request on {Description} failed: {ex.Message}");
            }
        }
    }

    private static bool HasHeadTerminator(byte[] buf, int len)
    {
        for (int i = 3; i < len; i++)
        {
            if (buf[i] == '\n' && buf[i - 1] == '\r' && buf[i - 2] == '\n' && buf[i - 3] == '\r') return true;
            if (buf[i] == '\n' && buf[i - 1] == '\n') return true;
        }
        return false;
    }

    public void Dispose()
    {
        stopped = true;
        try { socket.Close(); } catch { /* already closed */ }
    }
}
