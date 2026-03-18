using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace SongRequestDesktopV2Rewrite
{
    internal static class KeyboardShortcutHelper
    {
        public static string BuildGesture(Key key, ModifierKeys modifiers)
        {
            var parts = new List<string>();

            if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");

            parts.Add(GetKeyDisplayName(key));
            return string.Join("+", parts);
        }

        public static bool IsModifierKey(Key key)
        {
            return key == Key.LeftCtrl || key == Key.RightCtrl ||
                   key == Key.LeftAlt || key == Key.RightAlt ||
                   key == Key.LeftShift || key == Key.RightShift ||
                   key == Key.LWin || key == Key.RWin;
        }

        public static Key NormalizeKey(Key key, Key systemKey)
        {
            return key == Key.System ? systemKey : key;
        }

        public static bool IsShortcutMatch(KeyboardShortcutConfig? shortcut, KeyEventArgs e)
        {
            if (shortcut == null || !shortcut.Enabled || string.IsNullOrWhiteSpace(shortcut.Gesture))
            {
                return false;
            }

            if (!TryParseGesture(shortcut.Gesture, out var expectedModifiers, out var expectedKeys))
            {
                return false;
            }

            var actualKey = NormalizeKey(e.Key, e.SystemKey);
            var actualModifiers = Keyboard.Modifiers;

            if (actualModifiers != expectedModifiers)
            {
                return false;
            }

            return expectedKeys.Contains(actualKey);
        }

        private static bool TryParseGesture(string gesture, out ModifierKeys modifiers, out HashSet<Key> keys)
        {
            modifiers = ModifierKeys.None;
            keys = new HashSet<Key>();

            if (string.IsNullOrWhiteSpace(gesture))
            {
                return false;
            }

            var tokens = gesture.Split('+', StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                .ToArray();

            if (tokens.Length == 0)
            {
                return false;
            }

            var keyToken = tokens[^1];

            for (int i = 0; i < tokens.Length - 1; i++)
            {
                switch (tokens[i].ToLowerInvariant())
                {
                    case "ctrl":
                    case "control":
                        modifiers |= ModifierKeys.Control;
                        break;
                    case "alt":
                        modifiers |= ModifierKeys.Alt;
                        break;
                    case "shift":
                        modifiers |= ModifierKeys.Shift;
                        break;
                    case "win":
                    case "windows":
                        modifiers |= ModifierKeys.Windows;
                        break;
                    default:
                        return false;
                }
            }

            if (!TryMapKeyToken(keyToken, keys))
            {
                return false;
            }

            return keys.Count > 0;
        }

        private static bool TryMapKeyToken(string token, HashSet<Key> keys)
        {
            switch (token.ToLowerInvariant())
            {
                case "plus":
                    keys.Add(Key.OemPlus);
                    keys.Add(Key.Add);
                    return true;
                case "minus":
                    keys.Add(Key.OemMinus);
                    keys.Add(Key.Subtract);
                    return true;
            }

            if (Enum.TryParse<Key>(token, true, out var parsed))
            {
                keys.Add(parsed);
                return true;
            }

            return false;
        }

        private static string GetKeyDisplayName(Key key)
        {
            return key switch
            {
                Key.OemPlus => "Plus",
                Key.Add => "Plus",
                Key.OemMinus => "Minus",
                Key.Subtract => "Minus",
                _ => key.ToString()
            };
        }
    }
}
