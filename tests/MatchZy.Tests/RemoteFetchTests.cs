using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using MatchZy;
using Xunit;

namespace MatchZy.Tests;

public class RemoteFetchTests
{
    // The health listener is a small real HTTP server, which makes it a fine stand-in for
    // the control plane: it answers 200 with a body on /health and 404 elsewhere.
    private static HealthListener Serve(string body) =>
        HealthListener.ListenTcp(IPAddress.Loopback, 0, () => body, _ => { });

    [Fact]
    public async Task ASuccessCarriesTheBody()
    {
        using var server = Serve("{\"maplist\":[\"de_dust2\"]}");
        using var client = new HttpClient { Timeout = RemoteFetch.Timeout };

        var r = await RemoteFetch.FetchAsync(client, $"http://127.0.0.1:{server.Port}/health", "X-XPBot-Token", "secret");
        Assert.True(r.Succeeded);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("{\"maplist\":[\"de_dust2\"]}", r.Body);
        Assert.Null(r.Error);
    }

    [Fact]
    public async Task ARefusalCarriesTheStatusAndNoBody()
    {
        using var server = Serve("{}");
        using var client = new HttpClient { Timeout = RemoteFetch.Timeout };

        var r = await RemoteFetch.FetchAsync(client, $"http://127.0.0.1:{server.Port}/match/1.json", null, null);
        Assert.False(r.Succeeded);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Null(r.Error);
    }

    [Fact]
    public async Task NothingListeningIsAnErrorNotAnException()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        // A port nothing listens on: bind one to learn a free number, then release it.
        int port;
        using (var probe = Serve("{}")) port = probe.Port;

        var r = await RemoteFetch.FetchAsync(client, $"http://127.0.0.1:{port}/health", null, null);
        Assert.False(r.Succeeded);
        Assert.Null(r.StatusCode);
        Assert.False(string.IsNullOrEmpty(r.Error));
    }

    [Fact]
    public async Task AMalformedHeaderIsAnError()
    {
        // The blocking code reported a bad header name through its FATAL branch; so does this.
        using var server = Serve("{}");
        using var client = new HttpClient { Timeout = RemoteFetch.Timeout };

        var r = await RemoteFetch.FetchAsync(client, $"http://127.0.0.1:{server.Port}/health", "bad header name", "x");
        Assert.False(r.Succeeded);
        Assert.Null(r.StatusCode);
        Assert.False(string.IsNullOrEmpty(r.Error));
    }
}
