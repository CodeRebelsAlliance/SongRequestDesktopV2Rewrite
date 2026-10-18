using System;
using System.Collections.Generic;
using Android.Content;
using Android.Media;
using CoreAudioDeviceInfo = SongRequestDesktopV2Rewrite.AudioDeviceInfo;

namespace SongRequest.Mobile;

/// <summary>
/// Maps the core's device numbering onto Android's audio outputs, so the player's
/// device menu (<c>getMusicAudioDevices</c> / <c>setMusicOutputDevice</c>) works
/// the same way it does on the desktop: index <c>-1</c> is the system default
/// (added by the interop), and the indices here address
/// <c>AudioManager.GetDevices(Outputs)</c>.
/// </summary>
internal static class AndroidAudioDevices
{
    private static AudioManager? Manager
    {
        get
        {
            try { return (AudioManager?)Android.App.Application.Context.GetSystemService(Context.AudioService); }
            catch { return null; }
        }
    }

    /// <summary>The selectable outputs, in the order their numbers refer to.</summary>
    public static IReadOnlyList<CoreAudioDeviceInfo> Enumerate()
    {
        var result = new List<CoreAudioDeviceInfo>();
        var manager = Manager;
        if (manager == null) return result;

        try
        {
            var devices = manager.GetDevices(GetDevicesTargets.Outputs);
            if (devices == null) return result;

            for (int i = 0; i < devices.Length; i++)
                result.Add(new CoreAudioDeviceInfo(i, Describe(devices[i])));
        }
        catch (Exception ex)
        {
            Android.Util.Log.Debug("SongRequest", $"Device enumeration failed: {ex.Message}");
        }

        return result;
    }

    /// <summary>Resolves a device number to the platform device (null = default).</summary>
    public static Android.Media.AudioDeviceInfo? ResolveDevice(int number)
    {
        if (number < 0) return null;

        var manager = Manager;
        if (manager == null) return null;

        try
        {
            var devices = manager.GetDevices(GetDevicesTargets.Outputs);
            return devices != null && number < devices.Length ? devices[number] : null;
        }
        catch
        {
            return null;
        }
    }

    private static string Describe(Android.Media.AudioDeviceInfo device)
    {
        var product = device.ProductName?.ToString();
        var type = device.Type.ToString();

        // "Speaker (built-in)", "WH-1000XM4 (Bluetooth)", "Headphones (wired)"…
        // Some enum members only exist from a certain API level, but we only read
        // the value the platform already gave us (no API call), so naming them is
        // safe on every supported version.
#pragma warning disable CA1416
        var kind = device.Type switch
        {
            AudioDeviceType.BuiltinSpeaker or AudioDeviceType.BuiltinSpeakerSafe => "built-in",
            AudioDeviceType.BluetoothA2dp or AudioDeviceType.BluetoothSco => "Bluetooth",
            AudioDeviceType.WiredHeadset or AudioDeviceType.WiredHeadphones => "wired",
            AudioDeviceType.UsbDevice or AudioDeviceType.UsbHeadset => "USB",
            AudioDeviceType.Hdmi or AudioDeviceType.HdmiArc or AudioDeviceType.HdmiEarc => "HDMI",
            _ => type,
        };
#pragma warning restore CA1416

        return string.IsNullOrWhiteSpace(product) ? kind : $"{product} ({kind})";
    }
}
