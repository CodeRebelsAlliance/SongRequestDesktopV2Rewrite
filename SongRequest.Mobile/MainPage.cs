using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using SongRequestDesktopV2Rewrite;

namespace SongRequest.Mobile;

/// <summary>
/// The mobile shell: one <see cref="HybridWebView"/> per UI page, a native tab
/// bar, and the host side of the JSON bridge the web UI already speaks.
///
/// The tabs depend on the <b>operating mode</b> chosen at startup (see
/// <see cref="ConfigureMode"/>): music mode gets Home/Library/Player/Queue, the
/// request and soundboard modes keep the request-oriented pages.
/// </summary>
public sealed class MainPage : ContentPage
{
    /// <summary>The touch-first music page used by every player tab on mobile.</summary>
    public const string MobileMusicFile = "mobilemusic.html";

    /// <summary>The touch-first song-request/moderation page.</summary>
    public const string MobileRequestsFile = "mobilerequests.html";

    /// <summary>The touch-first soundboard page.</summary>
    public const string MobileSoundboardFile = "mobilesoundboard.html";

    /// <summary>The desktop music page (not used on mobile any more).</summary>
    private const string DesktopMusicFile = "musicplayer.html";

    /// <summary>The mobile stage console (presentation controls + preview).</summary>
    public const string MobileStageFile = "mobilestage.html";

    /// <summary>
    /// How many web views stay alive. All tabs (and the alternate stage pages)
    /// stay resident, so switching never reloads a page and loses its state.
    /// <see cref="TrimResidentViews"/> drops the hidden ones under memory pressure.
    /// </summary>
    private const int MaxResidentViews = 6;

    // Material Design 3 dark-theme tokens for the navigation bar.
    private static readonly Color SurfaceContainer = Color.FromArgb("#211F26");
    private static readonly Color ActiveIndicator = Color.FromArgb("#4A4458");
    private static readonly Color ActiveIcon = Color.FromArgb("#E8DEF8");
    private static readonly Color InactiveIcon = Color.FromArgb("#CAC4D0");
    private static readonly Color ActiveLabel = Color.FromArgb("#E6E1E5");
    private static readonly Color InactiveLabel = Color.FromArgb("#CAC4D0");

    private readonly Grid _content = new();
    private readonly Grid _tabBar = new();
    private readonly Dictionary<string, TabItem> _tabs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HybridWebView> _views = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _recent = new();
    private readonly Dictionary<string, string> _destinationFile = new(StringComparer.Ordinal);

    private string _mode = "SongRequests";
    private string _activeKey = "requests";
    private string _baseKey = "requests";
    private string _currentFile = MobileRequestsFile;
    private bool _suppressNextPlayerOpen;
    private bool _navigationVisible;

    // Animated navigation-bar colours (derived from the current song's artwork).
    private Color _barColor = SurfaceContainer;
    private Color _pillColor = ActiveIndicator;
    private Color _iconColor = ActiveIcon;
    private Color _labelColor = ActiveLabel;
    private string? _themePrimary;
    private string? _themeSecondary;
    private bool _carMode;
    private readonly Grid _carWaiting;

    /// <summary>Raised for every message a page sends to the host (page file, raw JSON).</summary>
    public event Action<string, string>? HostMessageReceived;

    public MainPage()
    {
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
        root.Add(_tabBar, 0, 1);
        Content = root;

        _tabBar.ColumnSpacing = 0;
        _tabBar.Padding = new Thickness(0, 10, 0, 10);
        _tabBar.BackgroundColor = SurfaceContainer;
        _tabBar.HeightRequest = 80;

        _carWaiting = new Grid
        {
            BackgroundColor = Color.FromArgb("#121212"),
            IsVisible = false,
            Children =
            {
                new VerticalStackLayout
                {
                    Spacing = 14,
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions = LayoutOptions.Center,
                    Children =
                    {
                        new ActivityIndicator
                        {
                            IsRunning = true,
                            Color = Color.FromArgb("#D0BCFF"),
                            WidthRequest = 42,
                            HeightRequest = 42,
                        },
                        new Label
                        {
                            Text = "Waiting for SongRequest on your phone…",
                            TextColor = Color.FromArgb("#ECE7F0"),
                            FontSize = 20,
                            HorizontalTextAlignment = TextAlignment.Center,
                        },
                        new Label
                        {
                            Text = "The music player will appear here when loading is complete.",
                            TextColor = Color.FromArgb("#A9A4B3"),
                            FontSize = 14,
                            HorizontalTextAlignment = TextAlignment.Center,
                        }
                    }
                }
            }
        };
        _carWaiting.SetValue(Grid.RowSpanProperty, 2);
        root.Add(_carWaiting, 0, 0);

        // The tabs are rebuilt from the operating mode; this shows the default
        // (request) shell until the startup flow reports the chosen mode.
        ConfigureMode(StartupMode.SongRequests);

        // The onboarding splash/launch/auth flow owns the whole screen. The native
        // navigation appears only after completeSetup (see SetNavigationVisible).
        SetNavigationVisible(false);
    }

    /// <summary>The page file currently shown (used by the shell to route messages).</summary>
    public string CurrentFile => _currentFile;

    /// <summary>The selected tab key (the music page uses it for its initial view).</summary>
    public string CurrentTabKey => _activeKey;

    /// <summary>The current operating mode (StartupMode name).</summary>
    public string Mode => _mode;

    /// <summary>
    /// Rebuilds the navigation for an operating mode. Music mode gets the
    /// touch-first pages (Home, Library, Player, Queue); the other modes keep the
    /// request workflow.
    /// </summary>
    public void ConfigureMode(StartupMode mode)
    {
        ConfigureMode(mode, force: false);
    }

    public void ConfigureMode(StartupMode mode, bool force)
    {
        if (!MainThread.IsMainThread)
        {
            MainThread.BeginInvokeOnMainThread(() => ConfigureMode(mode, force));
            return;
        }

        if (_carMode) mode = StartupMode.MusicPlayer;
        var name = mode.ToString();
        if (!force && string.Equals(name, _mode, StringComparison.Ordinal) && _tabs.Count > 0) return;

        _mode = name;
        var defs = TabsFor(mode);

        _destinationFile.Clear();
        foreach (var def in defs)
            _destinationFile[def.Key] = def.File;

        _tabs.Clear();
        _tabBar.Children.Clear();
        _tabBar.ColumnDefinitions.Clear();

        for (int i = 0; i < defs.Count; i++)
            _tabBar.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

        for (int i = 0; i < defs.Count; i++)
            AddTab(defs[i], i);

        // Music starts at home; the startup flow's "show the player" request must
        // not pull the user out of it (see ConsumeInitialPlayerOpen).
        _suppressNextPlayerOpen = mode is StartupMode.MusicPlayer or StartupMode.MusicShare;

        _baseKey = defs[0].Key;
        Select(defs[0].Key);
    }

    /// <summary>
    /// Switches the shell into the car-optimized music-only layout: Home,
    /// Library and Player, no Stage/Presentation tabs. The native navigation stays
    /// hidden behind a waiting screen until the normal startup flow finishes.
    /// </summary>
    public void EnableCarMode()
    {
        if (!MainThread.IsMainThread)
        {
            MainThread.BeginInvokeOnMainThread(EnableCarMode);
            return;
        }

        _carMode = true;
        var alreadyReady = _navigationVisible;
        _carWaiting.IsVisible = !alreadyReady;
        if (!alreadyReady) SetNavigationVisible(false);
        ConfigureMode(StartupMode.MusicPlayer, force: true);
        if (alreadyReady)
        {
            _carWaiting.IsVisible = false;
            SetNavigationVisible(true);
        }
        BroadcastCarMode(true);
    }

    /// <summary>Leaves car mode and rebuilds the normal mobile navigation.</summary>
    public void DisableCarMode(string? previousMode = null)
    {
        if (!MainThread.IsMainThread)
        {
            MainThread.BeginInvokeOnMainThread(() => DisableCarMode(previousMode));
            return;
        }

        _carMode = false;
        _carWaiting.IsVisible = false;
        SetNavigationVisible(true);
        var mode = Enum.TryParse<StartupMode>(previousMode, out var parsed)
            ? parsed
            : StartupMode.SongRequests;
        ConfigureMode(mode, force: true);
        BroadcastCarMode(false);
    }

    /// <summary>Called when the startup flow completes: the car may show the player.</summary>
    public void EndCarWaiting()
    {
        if (!MainThread.IsMainThread)
        {
            MainThread.BeginInvokeOnMainThread(EndCarWaiting);
            return;
        }

        if (!_carMode) return;
        _carWaiting.IsVisible = false;
        SetNavigationVisible(true);
    }

    public bool IsCarMode => _carMode;

    private void BroadcastCarMode(bool enabled)
    {
        try
        {
            var json = "{\"type\":\"event\",\"eventName\":\"carMode\",\"data\":{\"enabled\":" +
                       (enabled ? "true" : "false") + "}}";
            foreach (var view in _views.Values)
            {
                try { view.SendRawMessage(json); } catch { }
            }
        }
        catch { }
    }

    /// <summary>True once for the startup flow: the mode was just applied and its first tab
    /// is already the right screen.
    /// </summary>
    public bool ConsumeInitialPlayerOpen()
    {
        if (!_suppressNextPlayerOpen) return false;
        _suppressNextPlayerOpen = false;
        return true;
    }

    /// <summary>Selects a tab by key ("home", "library", "player", "queue", …).</summary>
    public void Select(string key)
    {
        if (!MainThread.IsMainThread)
        {
            // Reached from the interop (ShowMusicPlayerWindow etc.), which runs on
            // worker threads: creating a web view there has no Looper.
            MainThread.BeginInvokeOnMainThread(() => Select(key));
            return;
        }

        if (!_destinationFile.ContainsKey(key)) key = _tabs.Keys.FirstOrDefault() ?? "requests";

        if (key == "queue")
        {
            // The queue is a sidebar: tapping its tab again closes it and returns
            // to the screen the user came from.
            if (_activeKey == "queue")
            {
                Select(_baseKey);
                return;
            }

            _baseKey = _activeKey;
        }
        else
        {
            _baseKey = key;
        }

        _activeKey = key;
        ShowFile(_destinationFile[key]);
        UpdateTabBar();
    }

    /// <summary>Closes the queue sidebar by returning to the underlying tab.</summary>
    public void RestoreBaseTab()
    {
        if (!MainThread.IsMainThread)
        {
            MainThread.BeginInvokeOnMainThread(RestoreBaseTab);
            return;
        }

        if (_activeKey != "queue") return;
        Select(_baseKey);
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

    /// <summary>
    /// Sends to whichever music page this mode uses (the touch UI, or the desktop
    /// page in Music Share mode).
    /// </summary>
    public bool SendToMusic(string json)
    {
        if (_currentFile is MobileMusicFile or DesktopMusicFile)
            return SendTo(_currentFile, json);

        return SendTo(MobileMusicFile, json) || SendTo(DesktopMusicFile, json);
    }

    /// <summary>Sends a JSON message to the currently visible page.</summary>
    public bool SendToCurrent(string json) => SendTo(_currentFile, json);

    /// <summary>Hides or shows the native navigation bar (hidden during startup).</summary>
    public void SetNavigationVisible(bool visible)
    {
        if (!MainThread.IsMainThread)
        {
            MainThread.BeginInvokeOnMainThread(() => SetNavigationVisible(visible));
            return;
        }

        _navigationVisible = visible;
        _tabBar.IsVisible = visible;
    }

    /// <summary>Hides the native tab bar for fullscreen sub-windows (stage/visualization).</summary>
    public void SetImmersive(bool immersive)
    {
        if (!MainThread.IsMainThread)
        {
            MainThread.BeginInvokeOnMainThread(() => SetImmersive(immersive));
            return;
        }

        _tabBar.IsVisible = _navigationVisible && !immersive;
    }

    /// <summary>
    /// Tints the navigation bar with the two colours extracted from the song's
    /// artwork. Null/empty restores the Material defaults.
    /// </summary>
    public void SetNavColors(string? primary, string? secondary)
    {
        if (!MainThread.IsMainThread)
        {
            MainThread.BeginInvokeOnMainThread(() => SetNavColors(primary, secondary));
            return;
        }

        var first = ParseColor(primary);
        var second = ParseColor(secondary) ?? first;
        _themePrimary = first != null ? primary : null;
        _themeSecondary = second != null ? (secondary ?? primary) : null;

        Color targetBar, targetPill, targetIcon, targetLabel;
        if (first == null)
        {
            targetBar = SurfaceContainer;
            targetPill = ActiveIndicator;
            targetIcon = ActiveIcon;
            targetLabel = ActiveLabel;
        }
        else
        {
            targetPill = EnsureContrast(first, 0.28, 0.72);
            targetBar = Mix(SurfaceContainer, second ?? first, 0.22);
            targetIcon = Luminance(targetPill) > 0.55 ? Color.FromArgb("#141218") : Colors.White;
            targetLabel = Mix(ActiveLabel, first, 0.30);
        }

        var fromBar = _barColor;
        var fromPill = _pillColor;
        var fromIcon = _iconColor;
        var fromLabel = _labelColor;

        this.AbortAnimation("navcolors");
        new Animation(t =>
        {
            _barColor = Mix(fromBar, targetBar, t);
            _pillColor = Mix(fromPill, targetPill, t);
            _iconColor = Mix(fromIcon, targetIcon, t);
            _labelColor = Mix(fromLabel, targetLabel, t);

            _tabBar.BackgroundColor = _barColor;
            foreach (var (key, tab) in _tabs)
            {
                var active = key == _activeKey;
                tab.Indicator.BackgroundColor = active ? _pillColor : Colors.Transparent;
                tab.Icon.Color = active ? _iconColor : InactiveIcon;
                tab.Label.TextColor = active ? _labelColor : InactiveLabel;
            }
        }, 0, 1).Commit(this, "navcolors", 16, 650, Easing.CubicInOut);

        BroadcastThemeColors(_themePrimary, _themeSecondary);
    }

    /// <summary>
    /// Sends the artwork palette to every resident page so their Material accents
    /// follow the current song, not just the native navigation bar.
    /// </summary>
    public void BroadcastThemeColors(string? primary, string? secondary)
    {
        if (!MainThread.IsMainThread)
        {
            MainThread.BeginInvokeOnMainThread(() => BroadcastThemeColors(primary, secondary));
            return;
        }

        try
        {
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(new
            {
                type = "event",
                eventName = "mobileThemeColors",
                data = new { primary, secondary }
            });

            foreach (var view in _views.Values)
            {
                try { view.SendRawMessage(json); } catch { }
            }
        }
        catch { }
    }

    // ── Tab bar ─────────────────────────────────────────────────────────────

    private IReadOnlyList<TabDef> TabsFor(StartupMode mode)
    {
        if (_carMode)
        {
            return new[]
            {
                new TabDef("home", "Home", MobileMusicFile, "\uf015"),
                new TabDef("library", "Library", MobileMusicFile, "\uf51f"),
                new TabDef("player", "Player", MobileMusicFile, "\uf025"),
            };
        }

        return TabsForMode(mode);
    }

    private static IReadOnlyList<TabDef> TabsForMode(StartupMode mode) => mode switch
    {
        StartupMode.MusicPlayer => new[]
        {
            new TabDef("home", "Home", MobileMusicFile, "\uf015"),       // fa-house
            new TabDef("library", "Library", MobileMusicFile, "\uf51f"), // fa-compact-disc
            new TabDef("player", "Player", MobileMusicFile, "\uf025"),   // fa-headphones
            new TabDef("stage", "Stage", MobileStageFile, "\uf26c"),     // fa-tv
        },
        StartupMode.MusicShare => new[]
        {
            new TabDef("share", "Share", MobileMusicFile, "\uf519"),     // fa-tower-broadcast
            new TabDef("player", "Player", MobileMusicFile, "\uf025"),   // fa-headphones
            new TabDef("stage", "Stage", MobileStageFile, "\uf26c"),     // fa-tv
        },
        StartupMode.Soundboard => new[]
        {
            new TabDef("board", "Board", MobileSoundboardFile, "\uf569"),          // fa-drum
            new TabDef("player", "Player", MobileMusicFile, "\uf025"),            // fa-headphones
            new TabDef("stage", "Stage", MobileStageFile, "\uf26c"),              // fa-tv
        },
        _ => new[]
        {
            new TabDef("requests", "Requests", MobileRequestsFile, "\uf0ca"),     // fa-list-ul
            new TabDef("player", "Player", MobileMusicFile, "\uf025"),            // fa-headphones
            new TabDef("board", "Board", MobileSoundboardFile, "\uf569"),          // fa-drum
            new TabDef("stage", "Stage", MobileStageFile, "\uf26c"),              // fa-tv
        },
    };

    private void AddTab(TabDef def, int column)
    {
        var icon = new FontImageSource
        {
            FontFamily = "FaSolid",
            Glyph = def.Glyph,
            Size = 24,
            Color = InactiveIcon,
        };

        var indicator = new Border
        {
            WidthRequest = 64,
            HeightRequest = 32,
            Padding = 0,
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 16 },
            BackgroundColor = Colors.Transparent,
            HorizontalOptions = LayoutOptions.Center,
            Content = new Image
            {
                Source = icon,
                WidthRequest = 24,
                HeightRequest = 24,
                Aspect = Aspect.AspectFit,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
            },
        };

        var label = new Label
        {
            Text = def.Title,
            FontSize = 12,
            TextColor = InactiveLabel,
            HorizontalTextAlignment = TextAlignment.Center,
            HorizontalOptions = LayoutOptions.Center,
            LineBreakMode = LineBreakMode.NoWrap,
        };

        var item = new Grid
        {
            BackgroundColor = Colors.Transparent,
            Children =
            {
                new VerticalStackLayout
                {
                    Spacing = 4,
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions = LayoutOptions.Center,
                    Children = { indicator, label },
                },
            },
        };

        SemanticProperties.SetDescription(item, def.Title + " tab");
        AutomationProperties.SetIsInAccessibleTree(item, true);

        var captured = def.Key;
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) =>
        {
            Select(captured);

            // M3 has a state-layer ripple; a short press-in animation is the
            // closest cross-platform equivalent without a native handler.
            try
            {
                await item.ScaleToAsync(0.94, 60, Easing.CubicOut);
                await item.ScaleToAsync(1.0, 90, Easing.CubicIn);
            }
            catch { }
        };
        item.GestureRecognizers.Add(tap);

        _tabs[def.Key] = new TabItem { Indicator = indicator, Icon = icon, Label = label };
        _tabBar.Add(item, column, 0);
    }

    private void UpdateTabBar()
    {
        foreach (var (key, tab) in _tabs)
        {
            var active = key == _activeKey;
            tab.Indicator.BackgroundColor = active ? _pillColor : Colors.Transparent;
            tab.Icon.Color = active ? _iconColor : InactiveIcon;
            tab.Label.TextColor = active ? _labelColor : InactiveLabel;
        }
    }

    // ── Pages ───────────────────────────────────────────────────────────────

    private void ShowFile(string file)
    {
        var view = EnsureView(file);

        foreach (var (key, other) in _views)
            other.IsVisible = string.Equals(key, file, StringComparison.Ordinal);

        view.IsVisible = true;
        _currentFile = file;

        // The music page has several views in one file; tell it which one this tab
        // shows. (On its first load the page asks with mobileTabReady.)
        if (string.Equals(file, MobileMusicFile, StringComparison.Ordinal))
        {
            var tab = _activeKey is "home" or "library" or "player" or "queue" or "share" ? _activeKey : "player";
            try
            {
                view.SendRawMessage(
                    "{\"type\":\"event\",\"eventName\":\"mobileTab\",\"data\":{\"tab\":\"" + tab +
                    "\",\"mode\":\"" + _mode + "\"}}");
            }
            catch { }
        }

        // Android pauses JavaScript in a hidden web view, so a page can miss host
        // events while another tab is in front (its promise may even time out).
        // Nudge it to re-request whatever it shows; pages that have nothing to
        // refresh simply ignore the event.
        try { view.SendRawMessage("{\"type\":\"event\",\"eventName\":\"refresh\"}"); }
        catch { }

        if (_carMode)
        {
            try
            {
                view.SendRawMessage("{\"type\":\"event\",\"eventName\":\"carMode\",\"data\":{\"enabled\":true}}");
            }
            catch { }
        }

        // A page created after the last song started still needs the current palette.
        if (_themePrimary != null)
        {
            try
            {
                view.SendRawMessage(Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    type = "event",
                    eventName = "mobileThemeColors",
                    data = new { primary = _themePrimary, secondary = _themeSecondary }
                }));
            }
            catch { }
        }
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

#if ANDROID
        // The stage console embeds the presentation served over http:// from this
        // device. The HybridWebView page itself is https://, so Android would block
        // the frame as mixed content without this.
        view.HandlerChanged += (_, _) =>
        {
            try
            {
                if (view.Handler?.PlatformView is Android.Webkit.WebView web)
                    web.Settings.MixedContentMode = Android.Webkit.MixedContentHandling.AlwaysAllow;
            }
            catch { }
        };
#endif

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

    /// <summary>
    /// Disposes views beyond the residency budget, preferring an alternate stage
    /// page over a tab destination: switching to Visualization/Announcement must
    /// not cost the Requests/Player/Board view (and with it their state).
    /// </summary>
    private void EvictViews(string keep)
    {
        while (_views.Count > MaxResidentViews)
        {
            var candidates = _recent
                .Where(file => file != keep && file != _currentFile)
                .ToList();                      // newest first

            var victim = candidates.FirstOrDefault(file => !IsTabDestination(file));
            if (victim == null) break;      // never evict a tab destination

            _recent.Remove(victim);
            DisposeView(victim);
        }
    }

    private bool IsTabDestination(string file)
        => _destinationFile.Values.Contains(file, StringComparer.Ordinal);

    /// <summary>
    /// Drops hidden web views under memory pressure. Tab destinations are kept:
    /// recreating the Requests page in particular used to restart the onboarding
    /// flow when the user came back to the tab. Alternate stage pages (visualize/
    /// announce) are the safe things to drop and reload.
    /// </summary>
    public void TrimResidentViews()
    {
        if (!MainThread.IsMainThread)
        {
            MainThread.BeginInvokeOnMainThread(TrimResidentViews);
            return;
        }

        foreach (var key in _views.Keys.ToList())
        {
            if (string.Equals(key, _currentFile, StringComparison.Ordinal)) continue;
            if (IsTabDestination(key)) continue;
            DisposeView(key);
        }
    }

    private void DisposeView(string key)
    {
        if (!_views.Remove(key, out var view)) return;

        _recent.Remove(key);
        try { _content.Remove(view); } catch { }
        try { view.Handler?.DisconnectHandler(); } catch { }
    }

    // ── Colour helpers ──────────────────────────────────────────────────────

    private static Color? ParseColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();

        try
        {
            if (text.StartsWith('#')) return Color.FromArgb(text);

            if (text.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
            {
                var open = text.IndexOf('(');
                var close = text.IndexOf(')');
                if (open < 0 || close <= open) return null;

                var parts = text[(open + 1)..close].Split(',');
                if (parts.Length < 3) return null;

                byte Channel(string part)
                {
                    var value = double.Parse(part.Trim(), System.Globalization.CultureInfo.InvariantCulture);
                    return (byte)Math.Clamp(Math.Round(value), 0, 255);
                }

                return Color.FromRgb(Channel(parts[0]), Channel(parts[1]), Channel(parts[2]));
            }
        }
        catch
        {
            // Unparsable colour: keep the current scheme.
        }

        return null;
    }

    private static Color Mix(Color from, Color to, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return new Color(
            (float)(from.Red + (to.Red - from.Red) * t),
            (float)(from.Green + (to.Green - from.Green) * t),
            (float)(from.Blue + (to.Blue - from.Blue) * t),
            1f);
    }

    private static double Luminance(Color color)
        => 0.299 * color.Red + 0.587 * color.Green + 0.114 * color.Blue;

    /// <summary>Keeps a song colour readable on the small indicator pill.</summary>
    private static Color EnsureContrast(Color color, double min, double max)
    {
        var luminance = Luminance(color);

        if (luminance < min && luminance > 0.001)
        {
            var factor = min / luminance;
            return new Color(
                (float)Math.Min(1, color.Red * factor),
                (float)Math.Min(1, color.Green * factor),
                (float)Math.Min(1, color.Blue * factor),
                1f);
        }

        if (luminance > max)
        {
            var factor = max / luminance;
            return new Color(
                (float)(color.Red * factor),
                (float)(color.Green * factor),
                (float)(color.Blue * factor),
                1f);
        }

        return color;
    }

    private sealed record TabDef(string Key, string Title, string File, string Glyph);

    private sealed class TabItem
    {
        public Border Indicator { get; init; } = null!;
        public FontImageSource Icon { get; init; } = null!;
        public Label Label { get; init; } = null!;
    }
}
