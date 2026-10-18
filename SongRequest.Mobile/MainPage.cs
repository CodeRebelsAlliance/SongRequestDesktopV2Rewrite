using System;
using System.Collections.Generic;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;

namespace SongRequest.Mobile;

/// <summary>
/// The mobile shell: one <see cref="HybridWebView"/> per UI page, a native tab
/// bar, and the host side of the JSON bridge the web UI already speaks.
///
/// Pages are created lazily and only <see cref="MaxResidentViews"/> of them stay
/// alive (LRU): the desktop shows each page in its own OS window, mobile must not
/// keep six web views resident on a phone.
/// </summary>
public sealed class MainPage : ContentPage
{
    /// <summary>Tab order of the native bottom bar.</summary>
    private static readonly string[] TabOrder = { "requests", "player", "board", "stage" };

    /// <summary>How many web views stay alive; the rest are disposed on tab switch.</summary>
    private const int MaxResidentViews = 2;

    private readonly Grid _content = new();
    private readonly Grid _tabBar = new();
    private readonly Dictionary<string, Button> _buttons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HybridWebView> _views = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _recent = new();
    private readonly Dictionary<string, string> _destinationFile = new(StringComparer.Ordinal);

    private string _activeKey = "requests";
    private string _currentFile = "index.html";

    /// <summary>Raised for every message a page sends to the host (page file, raw JSON).</summary>
    public event Action<string, string>? HostMessageReceived;

    public MainPage()
    {
        _destinationFile["requests"] = "index.html";
        _destinationFile["player"] = "musicplayer.html";
        _destinationFile["board"] = "soundboard.html";
        _destinationFile["stage"] = "presentation.html";

        BackgroundColor = Color.FromArgb("#121212");
        Title = "SongRequest";

#if IOS
        // Keep the tab bar above the home indicator.
        Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific.Page
            .SetUseSafeArea(this, true);
#endif

        var root = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition(GridLength.Star),
                new RowDefinition(GridLength.Auto),
            },
        };
        root.Add(_content, 0, 0);
        root.Add(BuildTabBar(), 0, 1);
        Content = root;

        Select("requests");
    }

    /// <summary>The page file currently shown (used by the shell to route messages).</summary>
    public string CurrentFile => _currentFile;

    /// <summary>Selects a tab by key ("requests", "player", "board", "stage").</summary>
    public void Select(string key)
    {
        if (!MainThread.IsMainThread)
        {
            // Reached from the interop (ShowMusicPlayerWindow etc.), which runs on
            // worker threads: creating a web view there has no Looper.
            MainThread.BeginInvokeOnMainThread(() => Select(key));
            return;
        }

        if (!_destinationFile.ContainsKey(key)) key = "requests";
        _activeKey = key;
        ShowFile(_destinationFile[key]);
        UpdateTabBar();
    }

    /// <summary>Shows a page file, mapping it onto a tab (used for the stage views).</summary>
    public void ShowFileInTab(string file, string tabKey)
    {
        if (!MainThread.IsMainThread)
        {
            MainThread.BeginInvokeOnMainThread(() => ShowFileInTab(file, tabKey));
            return;
        }

        if (!_destinationFile.ContainsKey(tabKey)) return;
        _destinationFile[tabKey] = file;
        Select(tabKey);
    }

    /// <summary>Sends a JSON message to the page with this file name, if it is resident.</summary>
    public bool SendTo(string file, string json)
    {
        if (!MainThread.IsMainThread)
        {
            // The interop answers on its own worker threads; the web view may only
            // be touched from the UI thread.
            MainThread.BeginInvokeOnMainThread(() => SendTo(file, json));
            return true;
        }

        if (!_views.TryGetValue(file, out var view)) return false;
        try { view.SendRawMessage(json); return true; }
        catch { return false; }
    }

    /// <summary>Sends a JSON message to the currently visible page.</summary>
    public bool SendToCurrent(string json) => SendTo(_currentFile, json);

    /// <summary>Hides the native tab bar for fullscreen sub-windows (stage/visualization).</summary>
    public void SetImmersive(bool immersive)
    {
        if (!MainThread.IsMainThread)
        {
            MainThread.BeginInvokeOnMainThread(() => SetImmersive(immersive));
            return;
        }

        _tabBar.IsVisible = !immersive;
    }

    private Grid BuildTabBar()
    {
        _tabBar.ColumnSpacing = 6;
        _tabBar.Padding = new Thickness(8, 6, 8, 10);
        _tabBar.BackgroundColor = Color.FromArgb("#1a1a1a");

        for (int i = 0; i < TabOrder.Length; i++)
            _tabBar.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

        for (int i = 0; i < TabOrder.Length; i++)
        {
            var key = TabOrder[i];
            var button = new Button
            {
                Text = TitleFor(key),
                FontSize = 12,
                CornerRadius = 10,
                HeightRequest = 46,
                Padding = new Thickness(4, 0),
                BackgroundColor = Color.FromArgb("#2a2a2a"),
                TextColor = Color.FromArgb("#a0a0a0"),
            };
            var captured = key;
            button.Clicked += (_, _) => Select(captured);

            _buttons[key] = button;
            _tabBar.Add(button, i, 0);
        }

        return _tabBar;
    }

    private static string TitleFor(string key) => key switch
    {
        "player" => "Play",
        "board" => "Board",
        "stage" => "Stage",
        _ => "Requests",
    };

    private void UpdateTabBar()
    {
        foreach (var (key, button) in _buttons)
        {
            var active = key == _activeKey;
            button.BackgroundColor = Color.FromArgb(active ? "#2a5a2a" : "#2a2a2a");
            button.TextColor = Color.FromArgb(active ? "#ffffff" : "#a0a0a0");
        }
    }

    private void ShowFile(string file)
    {
        var view = EnsureView(file);

        foreach (var (key, other) in _views)
            other.IsVisible = string.Equals(key, file, StringComparison.Ordinal);

        view.IsVisible = true;
        _currentFile = file;

        // Android pauses JavaScript in a hidden web view, so a page can miss host
        // events while another tab is in front (its promise may even time out).
        // Nudge it to re-request whatever it shows; pages that have nothing to
        // refresh simply ignore the event.
        try { view.SendRawMessage("{\"type\":\"event\",\"eventName\":\"refresh\"}"); }
        catch { }
    }

    private HybridWebView EnsureView(string file)
    {
        if (_views.TryGetValue(file, out var existing))
        {
            Touch(file);
            return existing;
        }

        var view = new HybridWebView
        {
            DefaultFile = file,
            HybridRoot = "wwwroot",
        };
        view.RawMessageReceived += (_, e) => HostMessageReceived?.Invoke(file, e.Message ?? string.Empty);

        _views[file] = view;
        _content.Add(view, 0, 0);
        Touch(file);
        EvictViews(keep: file);

        return view;
    }

    private void Touch(string file)
    {
        _recent.Remove(file);
        _recent.AddFirst(file);
    }

    /// <summary>Disposes the least recently used views beyond the residency budget.</summary>
    private void EvictViews(string keep)
    {
        while (_recent.Count > MaxResidentViews)
        {
            var oldest = _recent.Last!.Value;
            if (oldest == keep || oldest == _currentFile) break;

            _recent.RemoveLast();
            if (_views.Remove(oldest, out var view))
            {
                try { _content.Remove(view); } catch { }
                try { view.Handler?.DisconnectHandler(); } catch { }
            }
        }
    }
}
