using System;
using System.Collections.Generic;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Maps the remote-control keybind config (action ids shared with the web UI) to
/// global hotkeys and to <see cref="RemoteControlService.RemoteAction"/>.
/// </summary>
public static class HotkeyActionMap
{
    public static readonly string[] ActionIds =
    {
        "PlayPause", "SkipNext", "Previous", "Stop", "VolumeUp", "VolumeDown",
        "CrossfadeUp", "CrossfadeDown", "Announcement", "AnnouncementPlaySoundToggle",
        "AnnouncementPushToTalkToggle", "AnnouncementDimDbUp", "AnnouncementDimDbDown",
    };

    /// <summary>Enabled, non-empty gestures from the remote-control configuration.</summary>
    public static IEnumerable<(string ActionId, string Gesture)> EnabledGestures(RemoteControlConfiguration remote)
    {
        foreach (var actionId in ActionIds)
        {
            var keybind = KeybindFor(remote, actionId);
            if (keybind is { Enabled: true } && !string.IsNullOrWhiteSpace(keybind.Gesture))
                yield return (actionId, keybind.Gesture);
        }
    }

    internal static KeyboardShortcutConfig? KeybindFor(RemoteControlConfiguration remote, string actionId) => actionId switch
    {
        "PlayPause" => remote.PlayPauseKeybind,
        "SkipNext" => remote.SkipNextKeybind,
        "Previous" => remote.PreviousKeybind,
        "Stop" => remote.StopKeybind,
        "VolumeUp" => remote.VolumeUpKeybind,
        "VolumeDown" => remote.VolumeDownKeybind,
        "CrossfadeUp" => remote.CrossfadeUpKeybind,
        "CrossfadeDown" => remote.CrossfadeDownKeybind,
        "Announcement" => remote.AnnouncementKeybind,
        "AnnouncementPlaySoundToggle" => remote.AnnouncementPlaySoundToggleKeybind,
        "AnnouncementPushToTalkToggle" => remote.AnnouncementPushToTalkToggleKeybind,
        "AnnouncementDimDbUp" => remote.AnnouncementDimDbUpKeybind,
        "AnnouncementDimDbDown" => remote.AnnouncementDimDbDownKeybind,
        _ => null,
    };

    internal static RemoteControlService.RemoteAction? ToRemoteAction(string actionId) => actionId switch
    {
        "PlayPause" => RemoteControlService.RemoteAction.PlayPause,
        "SkipNext" => RemoteControlService.RemoteAction.SkipNext,
        "Previous" => RemoteControlService.RemoteAction.Previous,
        "Stop" => RemoteControlService.RemoteAction.Stop,
        "VolumeUp" => RemoteControlService.RemoteAction.VolumeUp,
        "VolumeDown" => RemoteControlService.RemoteAction.VolumeDown,
        "CrossfadeUp" => RemoteControlService.RemoteAction.CrossfadeUp,
        "CrossfadeDown" => RemoteControlService.RemoteAction.CrossfadeDown,
        "Announcement" => RemoteControlService.RemoteAction.Announcement,
        "AnnouncementPlaySoundToggle" => RemoteControlService.RemoteAction.AnnouncementPlaySoundToggle,
        "AnnouncementPushToTalkToggle" => RemoteControlService.RemoteAction.AnnouncementPushToTalkToggle,
        "AnnouncementDimDbUp" => RemoteControlService.RemoteAction.AnnouncementDimDbUp,
        "AnnouncementDimDbDown" => RemoteControlService.RemoteAction.AnnouncementDimDbDown,
        _ => null,
    };
}
