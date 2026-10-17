using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

public class HotkeyGestureTests
{
    [Theory]
    [InlineData("Ctrl+Alt+P", true, true, false, false, "P")]
    [InlineData("PageUp", false, false, false, false, "PageUp")]
    [InlineData("Shift+F5", false, false, true, false, "F5")]
    [InlineData("ctrl+shift+pageup", true, false, true, false, "PageUp")]
    [InlineData("Win+D1", false, false, false, true, "D1")]
    [InlineData("Ctrl+Alt+Shift+Win+NumPad3", true, true, true, true, "NumPad3")]
    public void TryParse_ParsesModifiersAndKey(string gesture, bool ctrl, bool alt, bool shift, bool win, string key)
    {
        Assert.True(HotkeyGesture.TryParse(gesture, out var parsed));
        Assert.Equal(ctrl, parsed.Ctrl);
        Assert.Equal(alt, parsed.Alt);
        Assert.Equal(shift, parsed.Shift);
        Assert.Equal(win, parsed.Win);
        Assert.Equal(key, parsed.Key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("Ctrl+Alt")]   // no key
    [InlineData("Ctrl+A+B")]   // two keys
    public void TryParse_RejectsInvalidGestures(string? gesture)
        => Assert.False(HotkeyGesture.TryParse(gesture, out _));

    [Fact]
    public void ToString_UsesCanonicalModifierOrder()
    {
        var gesture = new HotkeyGesture(Ctrl: true, Alt: true, Shift: true, Win: true, Key: "P");
        Assert.Equal("Ctrl+Alt+Shift+Win+P", gesture.ToString());
        Assert.True(gesture.HasModifiers);

        var plain = new HotkeyGesture(false, false, false, false, "F5");
        Assert.Equal("F5", plain.ToString());
        Assert.False(plain.HasModifiers);
    }

    [Theory]
    [InlineData("enter", "Return")]
    [InlineData("Enter", "Return")]
    [InlineData("esc", "Escape")]
    [InlineData("spacebar", "Space")]
    [InlineData("backspace", "Back")]
    [InlineData("numpad3", "NumPad3")]
    [InlineData("d1", "D1")]
    [InlineData("a", "A")]
    public void NormalizeKey_CanonicalisesNames(string input, string expected)
        => Assert.Equal(expected, HotkeyGesture.NormalizeKey(input));
}

public class HotkeyKeyTableTests
{
    [Theory]
    [InlineData("A", 0x41)]
    [InlineData("D1", 0x31)]
    [InlineData("F5", 0x74)]
    [InlineData("NumPad3", 0x63)]
    [InlineData("PageUp", 0x21)]
    [InlineData("OemOpenBrackets", 0xDB)]
    [InlineData("Space", 0x20)]
    public void WindowsVirtualKeys_MapsKnownKeys(string key, int expected)
        => Assert.Equal(expected, WindowsVirtualKeys.ToVirtualKey(key));

    [Theory]
    [InlineData("Unknown")]
    [InlineData("")]
    public void WindowsVirtualKeys_ReturnsNullForUnknown(string key)
        => Assert.Null(WindowsVirtualKeys.ToVirtualKey(key));

    [Theory]
    [InlineData("A", "a")]
    [InlineData("D1", "1")]
    [InlineData("F5", "F5")]
    [InlineData("NumPad3", "KP_3")]
    [InlineData("PageUp", "Prior")]
    [InlineData("OemOpenBrackets", "bracketleft")]
    [InlineData("Space", "space")]
    public void X11KeysymNames_MapsKnownKeys(string key, string expected)
        => Assert.Equal(expected, X11KeysymNames.ToKeysymName(key));

    [Theory]
    [InlineData("Unknown")]
    [InlineData("")]
    public void X11KeysymNames_ReturnsNullForUnknown(string key)
        => Assert.Null(X11KeysymNames.ToKeysymName(key));
}

public class HotkeyActionMapTests
{
    [Fact]
    public void EveryActionId_MapsToARemoteActionAndKeybind()
    {
        var remote = RemoteControlConfiguration.Ensure(new RemoteControlConfiguration());

        foreach (var actionId in HotkeyActionMap.ActionIds)
        {
            Assert.NotNull(HotkeyActionMap.ToRemoteAction(actionId));
            Assert.NotNull(HotkeyActionMap.KeybindFor(remote, actionId));
        }
    }

    [Fact]
    public void ToRemoteAction_UnknownReturnsNull()
        => Assert.Null(HotkeyActionMap.ToRemoteAction("not-an-action"));

    [Fact]
    public void EnabledGestures_ReturnsOnlyEnabledNonEmptyKeybinds()
    {
        var remote = RemoteControlConfiguration.Ensure(new RemoteControlConfiguration());
        remote.PlayPauseKeybind = new KeyboardShortcutConfig { Enabled = true, Gesture = "Ctrl+Alt+P" };
        remote.SkipNextKeybind = new KeyboardShortcutConfig { Enabled = false, Gesture = "Ctrl+Alt+N" };
        remote.StopKeybind = new KeyboardShortcutConfig { Enabled = true, Gesture = "" };

        var gestures = HotkeyActionMap.EnabledGestures(remote).ToList();

        Assert.Single(gestures);
        Assert.Equal("PlayPause", gestures[0].ActionId);
        Assert.Equal("Ctrl+Alt+P", gestures[0].Gesture);
    }
}

public class GlobalHotkeyServiceTests
{
    [Fact]
    public void RegisterAll_RegistersParseableEnabledGestures_AndSkipsInvalid()
    {
        var backend = new FakeGlobalHotkeyBackend();
        using var service = new GlobalHotkeyService(backend);

        int count = service.RegisterAll(new[]
        {
            ("PlayPause", "Ctrl+Alt+P"),
            ("SkipNext", "Ctrl+Alt+N"),
            ("Stop", "Ctrl+Alt+"),   // invalid (no key)
        });

        Assert.Equal(2, count);
        Assert.Equal(2, service.RegisteredCount);
        Assert.Equal(2, backend.Registered.Count);
        Assert.Equal("P", backend.Registered[0].Gesture.Key);
    }

    [Fact]
    public void ActionTriggered_RaisedWithActionId()
    {
        var backend = new FakeGlobalHotkeyBackend();
        using var service = new GlobalHotkeyService(backend);
        service.RegisterAll(new[] { ("VolumeUp", "Ctrl+Up") });

        string? triggered = null;
        service.ActionTriggered += (_, actionId) => triggered = actionId;

        backend.Raise(backend.Registered[0].Id);

        Assert.Equal("VolumeUp", triggered);
    }

    [Fact]
    public void UnsupportedBackend_RegistersNothing()
    {
        var backend = new FakeGlobalHotkeyBackend { IsSupported = false };
        using var service = new GlobalHotkeyService(backend);

        Assert.False(service.IsSupported);
        Assert.Equal(0, service.RegisterAll(new[] { ("PlayPause", "Ctrl+Alt+P") }));
        Assert.Empty(backend.Registered);
    }

    [Fact]
    public void RegisterAll_ReplacesPreviousRegistrations()
    {
        var backend = new FakeGlobalHotkeyBackend();
        using var service = new GlobalHotkeyService(backend);

        service.RegisterAll(new[] { ("PlayPause", "Ctrl+Alt+P") });
        service.RegisterAll(new[] { ("SkipNext", "Ctrl+Alt+N") });

        Assert.Equal(1, service.RegisteredCount);
        Assert.Single(backend.Registered);
        Assert.True(backend.UnregisterAllCount >= 1);
    }
}
