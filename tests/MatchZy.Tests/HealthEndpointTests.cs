using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using MatchZy;
using Xunit;

namespace MatchZy.Tests;

public class HealthEndpointTests
{
    private static HealthSnapshot Fresh(long now) => new()
    {
        Version = "1.4.34",
        Scope = "fesk4",
        ScopeSource = "from start argument",
        LoadedAt = now - 100,
        SnapshotAt = now - 1,
        Map = "de_mirage",
        Players = 10,
        MatchLoaded = true,
        MatchId = 42,
        Status = "playing",
        MapNumber = 1,
        DatabaseOk = true,
        DatabaseType = "sqlite",
        EventsEnabled = true,
        RemoteLogConfigured = true,
        EventsQueued = 0,
        DemoUploadConfigured = true,
    };

    [Fact]
    public void SocketNameIsTheScopeWithAPrefix()
    {
        // The agent computes the same name from the ref it passed as +matchzy_config_scope.
        Assert.Equal("matchzy-health-fesk4", HealthProtocol.SocketName("fesk4"));
        Assert.Equal("matchzy-health-fesk4", HealthProtocol.SocketName("FESK4"));
        Assert.Equal("matchzy-health-10.0.0.5:27015", HealthProtocol.SocketName("10.0.0.5:27015"));
        Assert.True(HealthProtocol.SocketName(new string('x', 500)).Length <= 100);
    }

    [Fact]
    public void HealthPortIsReadFromTheLaunchLine()
    {
        // Same reason the scope is read from argv: a +cvar on the launch line runs before
        // the plugin registers the convar.
        Assert.Equal(27200, ServerIdentity.ParseHealthPort(new[] { "cs2", "-port", "27015", "+matchzy_health_port", "27200" }));
        Assert.Equal(27200, ServerIdentity.ParseHealthPort(new[] { "cs2", "+matchzy_health_port=27200" }));
        Assert.Equal(0, ServerIdentity.ParseHealthPort(new[] { "cs2", "+matchzy_health_port", "0" }));
        Assert.Null(ServerIdentity.ParseHealthPort(new[] { "cs2", "-port", "27015" }));
        Assert.Null(ServerIdentity.ParseHealthPort(new[] { "cs2", "+matchzy_health_port", "+map", "de_dust2" }));
        Assert.Null(ServerIdentity.ParseHealthPort(new[] { "cs2", "+matchzy_health_port", "70000" }));
        Assert.Null(ServerIdentity.ParseHealthPort(null));
    }

    [Theory]
    [InlineData("GET /health HTTP/1.1\r\nHost: x\r\n\r\n", "GET", "/health")]
    [InlineData("get /health?verbose=1 HTTP/1.0\r\n", "GET", "/health")]
    [InlineData("HEAD / HTTP/1.1\r\n\r\n", "HEAD", "/")]
    [InlineData("POST /health HTTP/1.1\r\n\r\n", "POST", "/health")]
    public void RequestLineIsParsed(string head, string method, string path)
    {
        var (m, p) = HealthProtocol.ParseRequestLine(head);
        Assert.Equal(method, m);
        Assert.Equal(path, p);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\r\n")]
    [InlineData("garbage")]
    public void NonRequestsHaveNoMethod(string head)
    {
        Assert.Null(HealthProtocol.ParseRequestLine(head).Method);
    }

    [Fact]
    public void OnlyGetHealthIsAnswered()
    {
        static string Status(byte[] b) => Encoding.ASCII.GetString(b).Split(' ')[1];
        Assert.Equal("200", Status(HealthProtocol.Respond("GET", "/health", () => "{}")));
        Assert.Equal("200", Status(HealthProtocol.Respond("GET", "/", () => "{}")));
        Assert.Equal("404", Status(HealthProtocol.Respond("GET", "/admin", () => "{}")));
        Assert.Equal("405", Status(HealthProtocol.Respond("POST", "/health", () => "{}")));
        Assert.Equal("400", Status(HealthProtocol.Respond(null, "", () => "{}")));

        bool asked = false;
        HealthProtocol.Respond("DELETE", "/health", () => { asked = true; return "{}"; });
        Assert.False(asked, "a refused request must not serialise a body");
    }

    [Fact]
    public void HeadCarriesTheLengthButNoBody()
    {
        string get = Encoding.UTF8.GetString(HealthProtocol.Respond("GET", "/health", () => "{\"ok\":true}"));
        string head = Encoding.UTF8.GetString(HealthProtocol.Respond("HEAD", "/health", () => "{\"ok\":true}"));
        Assert.EndsWith("\r\n\r\n{\"ok\":true}", get);
        Assert.EndsWith("\r\n\r\n", head);
        Assert.Contains("Content-Length: 11\r\n", head);
        Assert.Contains("Connection: close\r\n", get);
    }

    [Fact]
    public void FreshSnapshotIsOk()
    {
        long now = 1_700_000_000;
        var r = HealthProtocol.Report(Fresh(now), now);
        Assert.True(r.Ok);
        Assert.Empty(r.Problems);
        Assert.Equal(1, r.SnapshotAgeSeconds);
        Assert.Equal(100, r.UptimeSeconds);
        Assert.Equal(42, r.Match.Id);
        Assert.Equal("playing", r.Match.Status);
    }

    [Fact]
    public void StaleSnapshotMeansTheGameThreadStopped()
    {
        // A process that still accepts connections while its main loop is wedged is the one
        // failure nothing else can see; the age of the snapshot is what reports it.
        long now = 1_700_000_000;
        var s = new HealthSnapshot { SnapshotAt = now - HealthProtocol.StaleAfterSeconds - 5, LoadedAt = now - 500 };
        var r = HealthProtocol.Report(s, now);
        Assert.False(r.Ok);
        Assert.Contains(r.Problems, p => p.Contains("has not refreshed the snapshot"));
        Assert.Equal(HealthProtocol.StaleAfterSeconds + 5, r.SnapshotAgeSeconds);
    }

    [Fact]
    public void DatabaseFailureIsAProblem()
    {
        long now = 1_700_000_000;
        var s = new HealthSnapshot { SnapshotAt = now, LoadedAt = now, DatabaseOk = false, DatabaseError = "unable to open database file" };
        var r = HealthProtocol.Report(s, now);
        Assert.False(r.Ok);
        Assert.Contains("database: unable to open database file", r.Problems);
    }

    [Fact]
    public void RenderedJsonUsesTheAgreedFieldNames()
    {
        long now = 1_700_000_000;
        using var doc = JsonDocument.Parse(HealthProtocol.Render(Fresh(now), now));
        var root = doc.RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal("1.4.34", root.GetProperty("version").GetString());
        Assert.Equal("fesk4", root.GetProperty("scope").GetString());
        Assert.Equal(now, root.GetProperty("now").GetInt64());
        Assert.Equal(1, root.GetProperty("snapshot_age_seconds").GetInt64());
        Assert.Equal(42, root.GetProperty("match").GetProperty("id").GetInt64());
        Assert.True(root.GetProperty("match").GetProperty("loaded").GetBoolean());
        Assert.True(root.GetProperty("database").GetProperty("ok").GetBoolean());
        Assert.Equal("sqlite", root.GetProperty("database").GetProperty("type").GetString());
        Assert.Equal(0, root.GetProperty("events").GetProperty("queued").GetInt32());
        Assert.True(root.GetProperty("demo_upload_configured").GetBoolean());
        Assert.Equal(0, root.GetProperty("problems").GetArrayLength());
    }

    [Fact]
    public void StartingReportSaysLoadedWithNoMatch()
    {
        long now = 1_700_000_000;
        using var doc = JsonDocument.Parse(HealthProtocol.RenderStarting("1.4.34", "fesk4", now - 3, now));
        var root = doc.RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal(3, root.GetProperty("uptime_seconds").GetInt64());
        Assert.False(root.GetProperty("match").GetProperty("loaded").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("match").GetProperty("id").ValueKind);
    }

    [Fact]
    public void TcpListenerAnswersARealRequest()
    {
        using var l = HealthListener.ListenTcp(IPAddress.Loopback, 0, () => "{\"ok\":true}", _ => { });
        Assert.NotEqual(0, l.Port);

        string reply = Roundtrip(new IPEndPoint(IPAddress.Loopback, l.Port), "GET /health HTTP/1.1\r\nHost: x\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 200 OK\r\n", reply);
        Assert.EndsWith("\r\n\r\n{\"ok\":true}", reply);

        string notFound = Roundtrip(new IPEndPoint(IPAddress.Loopback, l.Port), "GET /nope HTTP/1.1\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 404", notFound);
    }

    [Fact]
    public void DisposeFreesThePort()
    {
        var l = HealthListener.ListenTcp(IPAddress.Loopback, 0, () => "{}", _ => { });
        int port = l.Port;
        l.Dispose();
        // Rebinding the same port proves the accept loop let go of it.
        using var again = HealthListener.ListenTcp(IPAddress.Loopback, port, () => "{}", _ => { });
        Assert.Equal(port, again.Port);
    }

    [Fact]
    public void AbstractSocketAnswersOnLinux()
    {
        if (!HealthProtocol.AbstractSocketsSupported) return;
        string name = HealthProtocol.SocketName("test-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
        using var l = HealthListener.ListenAbstract(name, () => "{\"ok\":true}", _ => { });

        using var c = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        c.Connect(new UnixDomainSocketEndPoint("\0" + name));
        string reply = Roundtrip(c, "GET /health HTTP/1.1\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 200 OK\r\n", reply);
        Assert.EndsWith("{\"ok\":true}", reply);
    }

    private static string Roundtrip(EndPoint ep, string request)
    {
        using var c = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        c.Connect(ep);
        return Roundtrip(c, request);
    }

    private static string Roundtrip(Socket c, string request)
    {
        c.ReceiveTimeout = 5000;
        c.Send(Encoding.ASCII.GetBytes(request));
        var buf = new byte[65536];
        int total = 0;
        while (true)
        {
            int n = c.Receive(buf, total, buf.Length - total, SocketFlags.None);
            if (n <= 0) break;
            total += n;
        }
        return Encoding.UTF8.GetString(buf, 0, total);
    }
}
