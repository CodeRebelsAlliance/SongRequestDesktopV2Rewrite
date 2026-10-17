using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.IntegrationTests;

/// <summary>
/// Every request the app makes to the SongRequest backend must carry the
/// identifying <c>User-Agent</c> ("SongRequest Desktop v&lt;version&gt;").
///
/// These tests stand up a loopback HTTP listener, point the config at it and
/// assert the exact wire value the server receives — for the shared
/// <see cref="FetchingService"/> path and for an interop command path (the code
/// the Photino UI drives).
/// </summary>
public class BackendUserAgentTests
{
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Minimal one-shot HTTP listener that captures the User-Agent.</summary>
    private sealed class LoopbackBackend : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly TaskCompletionSource<string?> _userAgent = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Port { get; }
        public string Url => $"http://127.0.0.1:{Port}";

        public LoopbackBackend(string body = "{}")
        {
            Port = FreePort();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();

            _ = Task.Run(async () =>
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    _userAgent.TrySetResult(context.Request.Headers["User-Agent"]);

                    var bytes = Encoding.UTF8.GetBytes(body);
                    context.Response.ContentType = "application/json";
                    context.Response.OutputStream.Write(bytes, 0, bytes.Length);
                    context.Response.Close();
                }
                catch
                {
                    _userAgent.TrySetResult(null);
                }
            });
        }

        public Task<string?> ReceivedUserAgent => _userAgent.Task.WaitAsync(TimeSpan.FromSeconds(15));

        public void Dispose()
        {
            try { _listener.Stop(); } catch { }
        }
    }

    private static IDisposable UseBackendAddress(string address)
    {
        var previous = ConfigService.Instance.Current?.Address;
        ConfigService.Instance.Update(c => c.Address = address);
        return new Restore(() => ConfigService.Instance.Update(c => c.Address = previous ?? "http://127.0.0.1:5000"));
    }

    private sealed class Restore : IDisposable
    {
        private readonly Action _action;

        public Restore(Action action) => _action = action;

        public void Dispose() => _action();
    }

    [Fact]
    public async Task FetchingService_SendsTheIdentifyingUserAgent()
    {
        using var backend = new LoopbackBackend("[]");
        using var _ = UseBackendAddress(backend.Url);

        var service = new FetchingService("Bearer test");
        await service.SendRequest("get-bad-words");

        Assert.Equal($"SongRequest Desktop v{AppInfo.version}", await backend.ReceivedUserAgent);
    }

    [Fact]
    public async Task InteropBackendCommand_SendsTheIdentifyingUserAgent()
    {
        using var backend = new LoopbackBackend("{\"songs\":[]}");
        using var _ = UseBackendAddress(backend.Url);

        var host = new HeadlessHost(new List<Cookie>());
        var interop = new YoutubeFormInterop(host, host.YoutubeService);
        var responses = new ConcurrentQueue<string>();
        interop.SendMessage = responses.Enqueue;

        var message = new JObject { ["type"] = "request", ["id"] = "ua", ["method"] = "getPlayedSongs" };
        interop.HandleMessage(message.ToString(Formatting.None));

        Assert.Equal($"SongRequest Desktop v{AppInfo.version}", await backend.ReceivedUserAgent);
    }
}
