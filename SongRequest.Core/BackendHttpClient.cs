using System;
using System.Net.Http;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Builds HTTP clients for the **SongRequest backend** so that every request
/// carries the same identifying <c>User-Agent</c>
/// (<c>SongRequest Desktop v&lt;version&gt;</c>) and the configured bearer token.
///
/// The backend uses the User-Agent to tell which app/version is talking to it, so
/// it must be present on every backend call (the WPF app set it; this keeps the
/// Photino/interop paths consistent with it).
/// </summary>
internal static class BackendHttpClient
{
    /// <summary>The identifying User-Agent sent with every backend request.</summary>
    public static string UserAgent => $"SongRequest Desktop v{AppInfo.version}";

    /// <summary>Applies the identifying User-Agent to an existing client (best effort).</summary>
    public static void ApplyUserAgent(HttpClient client)
    {
        if (client == null) return;
        try
        {
            client.DefaultRequestHeaders.UserAgent.Clear();
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        }
        catch
        {
            // A malformed version must never break backend requests.
        }
    }

    /// <summary>Applies the identifying User-Agent and the configured bearer token.</summary>
    public static void ApplyHeaders(HttpClient client, Config? config)
    {
        ApplyUserAgent(client);

        if (string.IsNullOrWhiteSpace(config?.BearerToken)) return;
        try
        {
            client.DefaultRequestHeaders.Remove("Authorization");
            client.DefaultRequestHeaders.Add("Authorization", config!.BearerToken);
        }
        catch
        {
            // Header already present in an incompatible form; keep going.
        }
    }

    /// <summary>Creates a client for the backend with the identifying User-Agent.</summary>
    /// <param name="config">Supplies the optional bearer token.</param>
    /// <param name="timeout">Optional request timeout.</param>
    public static HttpClient Create(Config? config, TimeSpan? timeout = null)
    {
        var client = new HttpClient();
        if (timeout is { } value) client.Timeout = value;
        ApplyHeaders(client, config);
        return client;
    }
}
