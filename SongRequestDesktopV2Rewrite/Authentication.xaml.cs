using CefSharp;
using CefSharp.Wpf;
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace SongRequestDesktopV2Rewrite
{
    /// <summary>
    /// Interaction logic for Authentication.xaml
    /// </summary>
    public partial class Authentication : Window
    {
        public event Action<IReadOnlyList<System.Net.Cookie>>? CookiesRetrieved;
        public Authentication()
        {
            InitializeComponent();
            InitializeBrowser();
        }

        private void InitializeBrowser()
        {
            if (Cef.IsInitialized != true)
            {
                var settings = new CefSettings();
                // Configure settings if needed
                Cef.Initialize(settings);
            }

            //TODO
            //LyricsLookup.firstlaunch = false;

            // Subscribe to loading state changed
            Browser.LoadingStateChanged += OnLoadingStateChanged;
        }

        private async void OnLoadingStateChanged(object? sender, LoadingStateChangedEventArgs e)
        {
            // event fires on a CEF thread — marshal to UI thread for UI ops
            if (!e.IsLoading)
            {
                string address = string.Empty;
                try
                {
                    // use the Cef browser instance from the event (safe on CEF thread)
                    address = e?.Browser?.MainFrame?.Url ?? string.Empty;
                }
                catch
                {
                    // fallback to the WPF control property if event-based access fails
                    try { address = Browser.Address ?? string.Empty; } catch { address = string.Empty; }
                }

                // If it's a valid URI, check the host for youtube (covers www.youtube.com, studio.youtube.com, youtube-nocookie.com, etc.)
                if (Uri.TryCreate(address, UriKind.Absolute, out var uri) &&
                    uri.Host.IndexOf("youtube", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    try
                    {
                        var cookies = await GetCookiesAsync().ConfigureAwait(false);
                        // marshal cookie handling back to UI thread if needed
                        Dispatcher.Invoke(() => HandleCookies(cookies));
                    }
                    catch
                    {
                        // swallow cookie errors - still want to close the window
                    }

                    // Unsubscribe to avoid repeated handling and close the window on the UI thread
                    Dispatcher.Invoke(() =>
                    {
                        try { Browser.LoadingStateChanged -= OnLoadingStateChanged; } catch { }
                        Close();
                    });
                }
            }
        }

        private async Task<IReadOnlyList<System.Net.Cookie>> GetCookiesAsync()
        {
            var cookieManager = Cef.GetGlobalCookieManager();
            var visitor = new CookieVisitor();
            // VisitAllCookies will call visitor.Visit for each cookie
            cookieManager.VisitAllCookies(visitor);
            await visitor.TaskCompletionSource.Task.ConfigureAwait(false);
            return visitor.Cookies;
        }

        private void HandleCookies(IReadOnlyList<System.Net.Cookie> cookies)
        {
            var sanitizedCookies = SanitizeCookies(cookies);
            CookiesRetrieved?.Invoke(sanitizedCookies);
        }

        private IReadOnlyList<System.Net.Cookie> SanitizeCookies(IReadOnlyList<System.Net.Cookie> cookies)
        {
            var sanitizedCookies = new List<System.Net.Cookie>();
            foreach (var cookie in cookies)
            {
                try
                {
                    var tempContainer = new CookieContainer();
                    tempContainer.Add(cookie); // validate
                    sanitizedCookies.Add(cookie);
                }
                catch (CookieException)
                {
                    // skip invalid cookies
                }
            }
            return sanitizedCookies;
        }

        private class CookieVisitor : ICookieVisitor
        {
            public List<System.Net.Cookie> Cookies { get; } = new();
            public TaskCompletionSource<bool> TaskCompletionSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public void Dispose() { }

            // CefSharp will call Visit on a CEF thread
            public bool Visit(CefSharp.Cookie cookie, int count, int total, ref bool deleteCookie)
            {
                try
                {
                    var netCookie = new System.Net.Cookie
                    {
                        Name = cookie.Name,
                        Value = cookie.Value,
                        Path = cookie.Path,
                        Domain = cookie.Domain
                    };
                    Cookies.Add(netCookie);
                }
                catch
                {
                    // ignore conversion errors for individual cookies
                }

                if (count == total - 1)
                {
                    TaskCompletionSource.TrySetResult(true);
                }

                return true;
            }
        }
    }
}
