namespace SongRequestDesktopV2Rewrite;

/// <summary>MIDI device information (shared by every MIDI backend).</summary>
public class MidiDeviceInfo
{
    public int DeviceNumber { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsInput { get; set; }

    public override string ToString()
    {
        return string.IsNullOrWhiteSpace(Name) ? $"Device {DeviceNumber}" : Name;
    }
}
