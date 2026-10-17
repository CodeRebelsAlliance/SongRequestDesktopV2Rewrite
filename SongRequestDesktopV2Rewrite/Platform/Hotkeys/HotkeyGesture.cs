using System;
using System.Collections.Generic;
using System.Linq;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// A keyboard gesture as stored by the web UI (e.g. <c>Ctrl+Alt+P</c>,
/// <c>PageUp</c>, <c>Shift+F5</c>). Modifier order is Ctrl, Alt, Shift, Win;
/// key names use the WPF naming that <c>newui/keys.js</c> produces.
/// </summary>
public readonly record struct HotkeyGesture(bool Ctrl, bool Alt, bool Shift, bool Win, string Key)
{
    public bool HasModifiers => Ctrl || Alt || Shift || Win;

    public static bool TryParse(string? gesture, out HotkeyGesture result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(gesture)) return false;

        bool ctrl = false, alt = false, shift = false, win = false;
        string? key = null;

        foreach (var raw in gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    ctrl = true;
                    break;
                case "alt":
                    alt = true;
                    break;
                case "shift":
                    shift = true;
                    break;
                case "win":
                case "windows":
                case "meta":
                case "super":
                    win = true;
                    break;
                default:
                    if (key != null) return false; // more than one non-modifier key
                    key = NormalizeKey(raw);
                    break;
            }
        }

        if (string.IsNullOrEmpty(key)) return false;

        result = new HotkeyGesture(ctrl, alt, shift, win, key!);
        return true;
    }

    private static readonly string[] CanonicalKeyNames =
    {
        "PageUp", "PageDown", "Up", "Down", "Left", "Right", "Home", "End", "Insert", "Delete",
        "Space", "Return", "Escape", "Tab", "Back",
        "OemOpenBrackets", "OemCloseBrackets", "OemSemicolon", "OemQuotes", "OemComma", "OemPeriod",
        "OemQuestion", "OemPipe", "OemTilde", "Minus", "Plus", "CapsLock", "NumLock", "Scroll",
        "Snapshot", "Pause", "Apps", "Add", "Subtract", "Multiply", "Divide", "Decimal",
    };

    /// <summary>Normalizes key names (case, "NumPad" spellings, "Enter"/"Return", F-keys).</summary>
    internal static string NormalizeKey(string key)
    {
        var k = key.Trim();
        if (k.Length == 0) return k;
        if (k.Length == 1) return char.IsLetter(k[0]) ? k.ToUpperInvariant() : k;

        if ((k[0] == 'f' || k[0] == 'F') && int.TryParse(k.AsSpan(1), out var fn) && fn >= 1 && fn <= 24)
            return "F" + fn;

        if (k.StartsWith("numpad", StringComparison.OrdinalIgnoreCase))
        {
            var rest = k.Substring(6);
            if (rest.Equals("enter", StringComparison.OrdinalIgnoreCase)) return "Return";
            if (rest.Length == 1 && char.IsDigit(rest[0])) return "NumPad" + rest;
        }

        if (k.Length == 2 && (k[0] == 'd' || k[0] == 'D') && char.IsDigit(k[1]))
            return "D" + k[1];

        switch (k.ToLowerInvariant())
        {
            case "enter":
            case "return":
                return "Return";
            case "escape":
            case "esc":
                return "Escape";
            case "space":
            case "spacebar":
                return "Space";
            case "back":
            case "backspace":
                return "Back";
            case "del":
            case "delete":
                return "Delete";
            case "ins":
            case "insert":
                return "Insert";
            case "pgup":
            case "pageup":
                return "PageUp";
            case "pgdn":
            case "pagedown":
                return "PageDown";
            case "print":
            case "printscreen":
                return "Snapshot";
            case "contextmenu":
                return "Apps";
        }

        foreach (var name in CanonicalKeyNames)
        {
            if (string.Equals(name, k, StringComparison.OrdinalIgnoreCase)) return name;
        }

        return k;
    }

    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        if (Win) parts.Add("Win");
        parts.Add(Key);
        return string.Join('+', parts);
    }
}
