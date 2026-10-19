using System;
using SongRequestDesktopV2Rewrite;

namespace SongRequest.Mobile;

/// <summary>
/// Ties the stage together for a phone: serve the presentation page over the LAN
/// (for a TV) and mirror it onto a connected HDMI display, then tell the page on
/// the phone which URL to advertise.
/// </summary>
internal static class StageMirror
{
    private static StageServer? _server;

    /// <summary>The URL a TV should open, or null while the stage is not running.</summary>
    public static string? Url => _server?.Url;

    /// <summary>Starts the server if needed and pushes the stage to a second display.</summary>
    public static void Publish()
    {
        try
        {
            if (_server == null)
            {
                if (!StageAssets.EnsureExtracted()) return;
                _server = new StageServer(AppHost.Current!.Host.Music, StageAssets.WebRoot)
                {
                    // A TV changing the zoom updates the setting the in-app page and
                    // the HDMI mirror read too.
                    ZoomChanged = zoom => AppHost.Current?.Interop.SetPresentationZoom(zoom),
                };
            }

            var url = _server.Start();
            if (string.IsNullOrEmpty(url)) return;

            // A connected projector/TV gets the page directly.
            StageDisplay.Show(url);

            // And the phone's own presentation page advertises the URL (with a QR)
            // so a TV without a cable can open it.
            AppHost.Current?.Shell.PresentationSendMessage?.Invoke(Newtonsoft.Json.JsonConvert.SerializeObject(new
            {
                type = "event",
                eventName = "stageServer",
                data = new { url, qr = QrDataUrl(url) },
            }));
        }
        catch (Exception ex)
        {
            Android.Util.Log.Debug("SongRequest", $"stage publish failed: {ex.Message}");
        }
    }

    public static void Stop()
    {
        _server?.Stop();
        _server = null;
        StageDisplay.Hide();
    }

    /// <summary>QR code for the TV URL, as a data URL (the phone shows it).</summary>
    private static string? QrDataUrl(string url)
    {
        try
        {
            using var generator = new QRCoder.QRCodeGenerator();
            using var data = generator.CreateQrCode(url, QRCoder.QRCodeGenerator.ECCLevel.Q);
            var png = new QRCoder.PngByteQRCode(data).GetGraphic(6);
            return "data:image/png;base64," + Convert.ToBase64String(png);
        }
        catch
        {
            return null;
        }
    }
}
