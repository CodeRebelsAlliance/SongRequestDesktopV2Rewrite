using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using SongRequestDesktopV2Rewrite;

namespace SongRequest.Mobile;

/// <summary>
/// In-app YouTube sign-in: a web view at the Google login, and the cookies it
/// leaves behind.
///
/// The desktop imports cookies from an installed browser with yt-dlp; a phone has
/// neither a browser profile to read nor a way to run yt-dlp, so the app hosts the
/// login itself. Because we own the web view, its cookie store is ours to read —
/// which is exactly what the desktop's "paste a Cookie header" flow expects, so
/// the same parser and storage are reused.
/// </summary>
internal static class AndroidSignInPage
{
    private const string LoginUrl = "https://accounts.google.com/ServiceLogin?service=youtube&continue=https://www.youtube.com/";

    private static TaskCompletionSource<List<System.Net.Cookie>?>? _pending;

    /// <summary>Shows the sign-in page and returns the harvested cookies, or null.</summary>
    public static Task<List<System.Net.Cookie>?> ShowAsync()
    {
        if (!MainThread.IsMainThread)
            return MainThread.InvokeOnMainThreadAsync(ShowAsync);

        _pending = new TaskCompletionSource<List<System.Net.Cookie>?>();

        try
        {
            var page = BuildPage();
            var main = Application.Current?.Windows.Count > 0 ? Application.Current.Windows[0].Page : null;
            if (main?.Navigation == null)
            {
                Android.Util.Log.Debug("SongRequest", "sign-in unavailable: no navigation host");
                _pending.TrySetResult(null);
                return _pending.Task;
            }

            _ = main.Navigation.PushModalAsync(page);
        }
        catch (Exception ex)
        {
            Android.Util.Log.Debug("SongRequest", $"sign-in failed to open: {ex.Message}");
            _pending.TrySetResult(null);
        }

        return _pending.Task;
    }

    private static ContentPage BuildPage()
    {
        var web = new Microsoft.Maui.Controls.WebView { Source = LoginUrl };

        var status = new Label
        {
            Text = "Sign in to YouTube, then tap Done.",
            FontSize = 12,
            TextColor = Colors.Gray,
            VerticalOptions = LayoutOptions.Center,
        };

        var cancel = new Button { Text = "Cancel" };
        var done = new Button { Text = "Done", BackgroundColor = Color.FromArgb("#2a5a2a"), TextColor = Colors.White };

        cancel.Clicked += async (_, _) =>
        {
            await CloseAsync();
            _pending?.TrySetResult(null);
        };

        done.Clicked += async (_, _) =>
        {
            var cookies = HarvestCookies();
            await CloseAsync();
            _pending?.TrySetResult(cookies);
        };

        var page = new ContentPage
        {
            Title = "YouTube sign-in",
            BackgroundColor = Color.FromArgb("#121212"),
            Content = new Grid
            {
                RowDefinitions =
                {
                    new RowDefinition(GridLength.Star),
                    new RowDefinition(GridLength.Auto),
                },
                Children =
                {
                    web,
                    new VerticalStackLayout
                    {
                        Padding = new Thickness(12, 8, 12, 12),
                        Spacing = 8,
                        Children =
                        {
                            status,
                            new HorizontalStackLayout
                            {
                                Spacing = 8,
                                HorizontalOptions = LayoutOptions.End,
                                Children = { cancel, done },
                            },
                        },
                    },
                },
            },
        };

        Grid.SetRow((BindableObject)((Grid)page.Content).Children[0], 0);
        Grid.SetRow((BindableObject)((Grid)page.Content).Children[1], 1);

        return page;
    }

    private static async Task CloseAsync()
    {
        try
        {
            var main = Application.Current?.Windows.Count > 0 ? Application.Current.Windows[0].Page : null;
            if (main?.Navigation != null) await main.Navigation.PopModalAsync();
        }
        catch (Exception ex)
        {
            Android.Util.Log.Debug("SongRequest", $"sign-in close failed: {ex.Message}");
        }
    }

    /// <summary>Reads the web view's cookie store and parses it like a Cookie header.</summary>
    private static List<System.Net.Cookie>? HarvestCookies()
    {
        try
        {
            var manager = Android.Webkit.CookieManager.Instance;
            if (manager == null) return null;

            // The YouTube session cookies live on youtube.com; Google's own
            // consent/session cookies are useful for the same requests.
            var parts = new List<string>();
            foreach (var host in new[] { "https://www.youtube.com", "https://youtube.com", "https://accounts.google.com", "https://www.google.com" })
            {
                var header = manager.GetCookie(host);
                if (!string.IsNullOrWhiteSpace(header)) parts.Add(header);
            }

            if (parts.Count == 0)
            {
                Android.Util.Log.Debug("SongRequest", "sign-in: no cookies in the web view yet");
                return null;
            }

            var cookies = YoutubeCookieService.ParseCookies(string.Join("; ", parts));
            Android.Util.Log.Debug("SongRequest", $"sign-in: harvested {cookies.Count} cookies");
            return cookies.Count > 0 ? cookies : null;
        }
        catch (Exception ex)
        {
            Android.Util.Log.Debug("SongRequest", $"sign-in cookie harvest failed: {ex.Message}");
            return null;
        }
    }
}
