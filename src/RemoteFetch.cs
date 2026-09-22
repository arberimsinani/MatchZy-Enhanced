using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace MatchZy;

/// <summary>
/// What came back from fetching a document over HTTP: the body on success, the status on a
/// refusal, the exception's message when the request never completed. One of the three, so
/// the caller's branches are the same three the blocking code had.
/// </summary>
public sealed class RemoteFetchResult
{
    /// <summary>The request completed with a success status and <see cref="Body"/> is the document.</summary>
    public bool Succeeded { get; init; }

    /// <summary>The HTTP status, when the server answered at all.</summary>
    public HttpStatusCode? StatusCode { get; init; }

    public string Body { get; init; } = "";

    /// <summary>Why the request did not complete: a timeout, a refused connection, a bad header.</summary>
    public string? Error { get; init; }

    public static RemoteFetchResult Ok(HttpStatusCode status, string body) =>
        new() { Succeeded = true, StatusCode = status, Body = body };

    public static RemoteFetchResult Refused(HttpStatusCode status) =>
        new() { Succeeded = false, StatusCode = status };

    public static RemoteFetchResult Failed(string error) =>
        new() { Succeeded = false, Error = error };
}

/// <summary>
/// Fetches a document without holding the thread that asked for it.
///
/// The match config, a queued match and a backup restore used to be fetched with a blocking
/// call on the game thread, with HttpClient's 100 s default timeout: with the control plane up
/// that was a few milliseconds, and with it down the whole server froze until the timeout ran
/// out. The fetch now runs on the thread pool and the plugin applies the outcome on the next
/// frame, so the game thread never waits on the network.
/// </summary>
public static class RemoteFetch
{
    /// <summary>
    /// Long enough for any LAN control plane and any reasonable internet one; short enough
    /// that a load against a control plane that is down is reported as such within a ready-up.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// GETs <paramref name="url"/> with one optional header. Never throws: every failure is a
    /// <see cref="RemoteFetchResult"/> with <see cref="RemoteFetchResult.Error"/> set.
    /// </summary>
    public static async Task<RemoteFetchResult> FetchAsync(HttpClient client, string url, string? headerName, string? headerValue)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(headerName))
            {
                // Add, not TryAddWithoutValidation: a malformed header name is an error the
                // caller reports, as it was when the blocking code set it on the client.
                request.Headers.Add(headerName, headerValue ?? "");
            }
            using var response = await client.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return RemoteFetchResult.Refused(response.StatusCode);
            }
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return RemoteFetchResult.Ok(response.StatusCode, body);
        }
        catch (Exception ex)
        {
            return RemoteFetchResult.Failed(ex.Message);
        }
    }
}
