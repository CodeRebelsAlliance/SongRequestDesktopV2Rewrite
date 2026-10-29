using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SongRequestDesktopV2Rewrite;
using SongRequestDesktopV2Rewrite.Integration;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

/// <summary>
/// End-to-end checks for the optional local API/MCP servers: HTTP, auth, MCP
/// JSON-RPC and the bidirectional WebSocket.
/// </summary>
public class IntegrationServerTests
{
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string url,
        string? token = null,
        string? json = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (!string.IsNullOrEmpty(token))
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        if (json != null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task RawHttpServer_ServesRequestsAndEchoesWebSocketMessages()
    {
        using var server = new IntegrationHttpServer(
            IPAddress.Loopback,
            0,
            request => Task.FromResult<HttpResponse?>(request.Path == "/ws"
                ? null
                : HttpResponse.Json(new { path = request.Path, method = request.Method })),
            connected: client => client.SendTextAsync("{\"type\":\"hello\"}"),
            message: (client, text) => client.SendTextAsync("{\"echo\":" + text + "}"));

        server.Start();
        var baseUrl = $"http://127.0.0.1:{server.Port}";

        using var http = new HttpClient();
        var response = await http.GetAsync(baseUrl + "/hello");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JObject.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("/hello", body["path"]?.ToString());

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/ws"), CancellationToken.None);

        var buffer = new byte[4096];
        var hello = await ws.ReceiveAsync(buffer, CancellationToken.None);
        Assert.Contains("hello", Encoding.UTF8.GetString(buffer, 0, hello.Count));

        await ws.SendAsync(Encoding.UTF8.GetBytes("{\"ping\":1}"), WebSocketMessageType.Text, true, CancellationToken.None);
        var echoed = await ws.ReceiveAsync(buffer, CancellationToken.None);
        Assert.Contains("ping", Encoding.UTF8.GetString(buffer, 0, echoed.Count));

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteCommandAsync_ReturnsTheSameResultAsTheWebUi()
    {
        var host = new HeadlessHost(new List<Cookie>());
        var interop = new YoutubeFormInterop(host, host.YoutubeService);

        var version = await interop.ExecuteCommandAsync("getVersion");
        Assert.Equal(AppInfo.version, version?["version"]?.ToString());

        var unknown = await interop.ExecuteCommandAsync("doesNotExist");
        Assert.Contains("Unknown method", unknown?["error"]?.ToString());

        var settings = await interop.ExecuteCommandAsync("getSettings");
        Assert.NotNull(settings?["apiEnabled"]);
        Assert.False(settings!["apiEnabled"]!.ToObject<bool>());

        // Advanced audio commands share the same dispatcher as the web UI.
        var equalizer = await interop.ExecuteCommandAsync("getEqualizer");
        Assert.Equal(10, (equalizer?["bands"] as JArray)?.Count);
        Assert.NotNull(equalizer?["presets"]);

        var enabled = await interop.ExecuteCommandAsync("setEqualizerEnabled",
            new JObject { ["enabled"] = true });
        Assert.True(enabled?["enabled"]?.ToObject<bool>());
        await interop.ExecuteCommandAsync("resetEqualizer");

        var sleep = await interop.ExecuteCommandAsync("getSleepTimer");
        Assert.False(sleep?["isActive"]?.ToObject<bool>());
        var started = await interop.ExecuteCommandAsync("startSleepTimer",
            new JObject { ["minutes"] = 5, ["playLastSongToEnd"] = true });
        Assert.True(started?["isActive"]?.ToObject<bool>());
        var cancelled = await interop.ExecuteCommandAsync("cancelSleepTimer");
        Assert.False(cancelled?["isActive"]?.ToObject<bool>());

        var missingAnalysis = await interop.ExecuteCommandAsync("analyzeSong",
            new JObject { ["songPath"] = Path.Combine(Path.GetTempPath(), "does-not-exist.mp3") });
        Assert.False(missingAnalysis?["success"]?.ToObject<bool>() ?? true);

        var originalAutoAnalyze = ConfigService.Instance.Current.AutoAnalyzeSongs;
        try
        {
            await interop.ExecuteCommandAsync("saveSettings", new JObject
            {
                ["settings"] = new JObject { ["autoAnalyzeSongs"] = false }
            });
            var settingsAfter = await interop.ExecuteCommandAsync("getSettings");
            Assert.False(settingsAfter?["autoAnalyzeSongs"]?.ToObject<bool>());
        }
        finally
        {
            ConfigService.Instance.Update(c => c.AutoAnalyzeSongs = originalAutoAnalyze);
        }
    }

    [Fact]
    public async Task ApiAndMcp_StartFromConfig_EnforceTheToken_AndExposeTools()
    {
        var cfg = ConfigService.Instance.Current;
        var saved = new
        {
            cfg.ApiEnabled, cfg.ApiBindAddress, cfg.ApiPort, cfg.ApiToken,
            cfg.McpEnabled, cfg.McpBindAddress, cfg.McpPort, cfg.McpToken
        };

        var apiPort = FreePort();
        var mcpPort = FreePort();
        var host = new HeadlessHost(new List<Cookie>());
        var interop = new YoutubeFormInterop(host, host.YoutubeService);

        try
        {
            ConfigService.Instance.Update(c =>
            {
                c.ApiEnabled = true;
                c.ApiBindAddress = "127.0.0.1";
                c.ApiPort = apiPort;
                c.ApiToken = "api-secret";
                c.McpEnabled = true;
                c.McpBindAddress = "127.0.0.1";
                c.McpPort = mcpPort;
                c.McpToken = "mcp-secret";
            });

            AppIntegrationServer.Instance.Initialize(host, interop);

            using var http = new HttpClient();
            var apiBase = $"http://127.0.0.1:{apiPort}";
            var mcpBase = $"http://127.0.0.1:{mcpPort}/mcp";

            // Unauthenticated -> 401; authenticated -> status JSON.
            var unauthorized = await SendAsync(http, HttpMethod.Get, apiBase + "/api/v1/status");
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

            var statusResponse = await SendAsync(http, HttpMethod.Get, apiBase + "/api/v1/status", "api-secret");
            Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
            var status = JObject.Parse(await statusResponse.Content.ReadAsStringAsync());
            Assert.Equal(AppInfo.version, status["app"]?["version"]?.ToString());
            Assert.True(status["integration"]?["api"]?["running"]?.ToObject<bool>());
            Assert.Equal(apiPort, status["integration"]?["api"]?["port"]?.ToObject<int>());

            var openApiResponse = await SendAsync(http, HttpMethod.Get, apiBase + "/openapi.json", "api-secret");
            Assert.Equal(HttpStatusCode.OK, openApiResponse.StatusCode);
            var openApi = JObject.Parse(await openApiResponse.Content.ReadAsStringAsync());
            Assert.Equal(AppInfo.version, openApi["info"]?["version"]?.ToString());

            var capabilitiesResponse = await SendAsync(http, HttpMethod.Get, apiBase + "/api/v1/capabilities", "api-secret");
            Assert.Equal(HttpStatusCode.OK, capabilitiesResponse.StatusCode);
            var capabilities = JObject.Parse(await capabilitiesResponse.Content.ReadAsStringAsync());
            Assert.Contains("getVersion", (capabilities["commands"] as JArray)?.Select(c => c["name"]?.ToString()) ?? Array.Empty<string>());

            var playerResponse = await SendAsync(http, HttpMethod.Get, apiBase + "/api/v1/player", "api-secret");
            Assert.Equal(HttpStatusCode.OK, playerResponse.StatusCode);

            var equalizerResponse = await SendAsync(http, HttpMethod.Get, apiBase + "/api/v1/equalizer", "api-secret");
            Assert.Equal(HttpStatusCode.OK, equalizerResponse.StatusCode);
            var equalizerJson = JObject.Parse(await equalizerResponse.Content.ReadAsStringAsync());
            Assert.Equal(10, (equalizerJson["bands"] as JArray)?.Count);

            var sleepResponse = await SendAsync(http, HttpMethod.Get, apiBase + "/api/v1/sleep-timer", "api-secret");
            Assert.Equal(HttpStatusCode.OK, sleepResponse.StatusCode);
            Assert.False(JObject.Parse(await sleepResponse.Content.ReadAsStringAsync())["isActive"]?.ToObject<bool>());

            var startSleep = await SendAsync(http, HttpMethod.Post, apiBase + "/api/v1/sleep-timer", "api-secret",
                "{\"minutes\":5,\"fadeOutSeconds\":10,\"playLastSongToEnd\":true}");
            Assert.Equal(HttpStatusCode.OK, startSleep.StatusCode);
            Assert.True(JObject.Parse(await startSleep.Content.ReadAsStringAsync())["isActive"]?.ToObject<bool>());

            var stopSleep = await SendAsync(http, HttpMethod.Delete, apiBase + "/api/v1/sleep-timer", "api-secret");
            Assert.Equal(HttpStatusCode.OK, stopSleep.StatusCode);
            Assert.False(JObject.Parse(await stopSleep.Content.ReadAsStringAsync())["isActive"]?.ToObject<bool>());

            var analysisResponse = await SendAsync(http, HttpMethod.Get, apiBase + "/api/v1/analysis/cache", "api-secret");
            Assert.Equal(HttpStatusCode.OK, analysisResponse.StatusCode);
            Assert.NotNull(JObject.Parse(await analysisResponse.Content.ReadAsStringAsync())["analyses"]);

            // Song search works without the companion server for the library source.
            var songsResponse = await SendAsync(http, HttpMethod.Get,
                apiBase + "/api/v1/songs?source=library&limit=5", "api-secret");
            var songsBody = await songsResponse.Content.ReadAsStringAsync();
            Assert.True(songsResponse.StatusCode == HttpStatusCode.OK, songsBody);
            var songs = JObject.Parse(songsBody);
            Assert.Equal("library", songs["source"]?.ToString());

            // MCP: initialize + tools/list + a tool call.
            var unauthorizedMcp = await SendAsync(http, HttpMethod.Post, mcpBase,
                json: "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}");
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedMcp.StatusCode);

            var init = await SendAsync(http, HttpMethod.Post, mcpBase, "mcp-secret",
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}");
            Assert.Equal(HttpStatusCode.OK, init.StatusCode);
            var initJson = JObject.Parse(await init.Content.ReadAsStringAsync());
            Assert.Equal("songrequest", initJson["result"]?["serverInfo"]?["name"]?.ToString());

            var list = await SendAsync(http, HttpMethod.Post, mcpBase, "mcp-secret",
                "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\",\"params\":{}}");
            var listJson = JObject.Parse(await list.Content.ReadAsStringAsync());
            var toolNames = (listJson["result"]?["tools"] as JArray)?.Select(t => t["name"]?.ToString()).ToList() ?? new List<string?>();
            Assert.Contains("search_songs", toolNames);
            Assert.Contains("run_songrequest_command", toolNames);
            Assert.Contains("get_status", toolNames);

            var resources = await SendAsync(http, HttpMethod.Post, mcpBase, "mcp-secret",
                "{\"jsonrpc\":\"2.0\",\"id\":20,\"method\":\"resources/list\",\"params\":{}}");
            var resourcesJson = JObject.Parse(await resources.Content.ReadAsStringAsync());
            var resourceUris = (resourcesJson["result"]?["resources"] as JArray)?.Select(r => r["uri"]?.ToString()).ToList()
                ?? new List<string?>();
            Assert.Contains("songrequest://status", resourceUris);
            Assert.Contains("songrequest://requests", resourceUris);

            var readResource = await SendAsync(http, HttpMethod.Post, mcpBase, "mcp-secret",
                "{\"jsonrpc\":\"2.0\",\"id\":21,\"method\":\"resources/read\",\"params\":{\"uri\":\"songrequest://status\"}}");
            var readJson = JObject.Parse(await readResource.Content.ReadAsStringAsync());
            Assert.Contains(AppInfo.version, readJson["result"]?["contents"]?[0]?["text"]?.ToString());

            var call = await SendAsync(http, HttpMethod.Post, mcpBase, "mcp-secret",
                "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"get_status\",\"arguments\":{}}}");
            var callJson = JObject.Parse(await call.Content.ReadAsStringAsync());
            Assert.False(callJson["result"]?["isError"]?.ToObject<bool>() ?? true);
            Assert.Contains(AppInfo.version, callJson["result"]?["content"]?[0]?["text"]?.ToString());

            var searchCall = await SendAsync(http, HttpMethod.Post, mcpBase, "mcp-secret",
                "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"search_songs\",\"arguments\":{\"source\":\"library\",\"limit\":5}}}");
            var searchJson = JObject.Parse(await searchCall.Content.ReadAsStringAsync());
            Assert.False(searchJson["result"]?["isError"]?.ToObject<bool>() ?? true);
            Assert.Contains("library", searchJson["result"]?["content"]?[0]?["text"]?.ToString());

            var genericCall = await SendAsync(http, HttpMethod.Post, mcpBase, "mcp-secret",
                "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/call\",\"params\":{\"name\":\"run_songrequest_command\",\"arguments\":{\"command\":\"getVersion\",\"arguments\":{}}}}");
            var genericJson = JObject.Parse(await genericCall.Content.ReadAsStringAsync());
            Assert.Contains(AppInfo.version, genericJson["result"]?["content"]?[0]?["text"]?.ToString());

            // WebSocket: authenticate with ?token=, receive hello, then run a command.
            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{apiPort}/api/v1/ws?token=api-secret"), CancellationToken.None);
            var buffer = new byte[16 * 1024];
            var hello = await ws.ReceiveAsync(buffer, CancellationToken.None);
            var helloJson = JObject.Parse(Encoding.UTF8.GetString(buffer, 0, hello.Count));
            Assert.Equal("hello", helloJson["type"]?.ToString());

            // Live event from the interop event sink reaches the socket.
            interop.SendEvent("test.event", new { value = 42 });
            JObject? eventJson = null;
            for (int i = 0; i < 50 && eventJson == null; i++)
            {
                var message = await ws.ReceiveAsync(buffer, CancellationToken.None);
                var json = JObject.Parse(Encoding.UTF8.GetString(buffer, 0, message.Count));
                if (json["type"]?.ToString() == "event" && json["event"]?.ToString() == "test.event")
                    eventJson = json;
            }

            Assert.NotNull(eventJson);
            Assert.Equal(42, eventJson["data"]?["value"]?.ToObject<int>());

            await ws.SendAsync(
                Encoding.UTF8.GetBytes("{\"type\":\"request\",\"id\":\"v1\",\"method\":\"getVersion\"}"),
                WebSocketMessageType.Text, true, CancellationToken.None);

            JObject? responseJson = null;
            for (int i = 0; i < 50 && responseJson == null; i++)
            {
                var message = await ws.ReceiveAsync(buffer, CancellationToken.None);
                var json = JObject.Parse(Encoding.UTF8.GetString(buffer, 0, message.Count));
                if (json["type"]?.ToString() == "response" && json["id"]?.ToString() == "v1")
                    responseJson = json;
            }

            Assert.NotNull(responseJson);
            Assert.Equal(AppInfo.version, responseJson!["result"]?["version"]?.ToString());

            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        }
        finally
        {
            AppIntegrationServer.Instance.Stop();
            ConfigService.Instance.Update(c =>
            {
                c.ApiEnabled = saved.ApiEnabled;
                c.ApiBindAddress = saved.ApiBindAddress;
                c.ApiPort = saved.ApiPort;
                c.ApiToken = saved.ApiToken;
                c.McpEnabled = saved.McpEnabled;
                c.McpBindAddress = saved.McpBindAddress;
                c.McpPort = saved.McpPort;
                c.McpToken = saved.McpToken;
            });
        }
    }
}
