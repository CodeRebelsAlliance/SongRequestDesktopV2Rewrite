using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.OpenAL;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Enumerates OpenAL Soft output devices.
///
/// Silk.NET marshals <c>alcGetString(NULL, ...)</c> to a single string, so the raw
/// function is invoked through a function pointer to read the full
/// double-NUL-separated device list. <c>ALC_DEVICE_SPECIFIER</c> only returns the
/// backend/driver names ("OpenAL Soft"), while <c>ALC_ALL_DEVICES_SPECIFIER</c>
/// (from <c>ALC_ENUMERATE_ALL_EXT</c>) returns the actual playback endpoints on
/// Windows and Linux — the distinction behind the old "only OpenAL Soft" menu.
/// </summary>
internal static unsafe class OpenAlDevices
{
    private const int AlcDeviceSpecifier = 0x1005;
    private const int AlcAllDevicesSpecifier = 0x1013;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr AlcGetStringDelegate(IntPtr device, int param);

    private static readonly object Sync = new();
    private static bool _probed;
    private static IReadOnlyList<string> _names = Array.Empty<string>();

    public static IReadOnlyList<string> Names()
    {
        lock (Sync)
        {
            // Keep a successful list cached, but retry after a failed/empty probe:
            // on some desktops the audio server is not ready yet when the first
            // settings/menu call runs.
            if (_probed && _names.Count > 0) return _names;
            _probed = true;
            try { _names = Probe(); }
            catch { _names = Array.Empty<string>(); }
            return _names;
        }
    }

    public static string? NameAt(int index)
    {
        var names = Names();
        return index >= 0 && index < names.Count ? names[index] : null;
    }

    private static IReadOnlyList<string> Probe()
    {
        // Shares the playback gate: opening/closing a device here would otherwise
        // become the process-wide current context while the outputs are feeding.
        lock (OpenAlAudioOutput.AlGate)
        {
            var alc = ALContext.GetApi();
            var device = alc.OpenDevice(null);
            if (device == null) return Array.Empty<string>();

            try
            {
                var proc = (IntPtr)alc.GetProcAddress(device, "alcGetString");
                if (proc == IntPtr.Zero) return Array.Empty<string>();

                var getString = Marshal.GetDelegateForFunctionPointer<AlcGetStringDelegate>(proc);

                // Prefer the full endpoint list. Older builds without
                // ALC_ENUMERATE_ALL_EXT return null/empty here, so fall back to the
                // historical driver-name list.
                var names = ReadDoubleNullList(getString(IntPtr.Zero, AlcAllDevicesSpecifier));
                if (names.Count == 0)
                    names = ReadDoubleNullList(getString(IntPtr.Zero, AlcDeviceSpecifier));

                return names
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            finally
            {
                try { alc.CloseDevice(device); } catch { }
            }
        }
    }

    private static IReadOnlyList<string> ReadDoubleNullList(IntPtr ptr)
    {
        var list = new List<string>();
        if (ptr == IntPtr.Zero) return list;

        var buffer = new List<byte>();
        for (int i = 0; i < (1 << 16); i++)
        {
            byte b = Marshal.ReadByte(ptr, i);
            if (b == 0)
            {
                if (buffer.Count == 0) break; // list terminator
                list.Add(Encoding.UTF8.GetString(buffer.ToArray()));
                buffer.Clear();
            }
            else
            {
                buffer.Add(b);
            }
        }
        return list;
    }
}
