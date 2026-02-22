# Music Share - Audio Toggle Feature

## Overview
Added the ability for users to receive only metadata (song info, lyrics) without streaming audio.

## Changes Made

### 1. UI Changes (MusicShare.xaml)
- **Added CheckBox** `ReceiveAudioCheckBox` in the Receiving Mode panel
- Label: "🔊 Receive Audio"
- **Default state: Checked (enabled)**
- Position: Between status display and Start Receiving button
- Tooltip explaining the feature

### 2. Service Layer (MusicShareService.cs)
- **Added `ReceiveAudio` property** (bool, default `true`)
  ```csharp
  public bool ReceiveAudio { get; set; } = true;
  ```
- **Modified `StartReceivingAsync()`** to conditionally start audio polling:
  - Always polls for metadata
  - Only polls for audio if `ReceiveAudio = true`
  - Logs audio reception status to debug output

### 3. UI Logic (MusicShare.xaml.cs)

#### StartReceivingAsync()
- Reads checkbox state: `_shareService.ReceiveAudio = ReceiveAudioCheckBox.IsChecked ?? true`
- Only calls `InitializeAudioPlayback()` if audio is enabled
- Disables checkbox while receiving (prevents mid-stream changes)

#### StopReceivingAsync()
- Re-enables checkbox after stopping

#### ShareService_AudioChunkReceived()
- **Early exit** if `!_shareService.ReceiveAudio`
- Skips all audio processing when disabled

#### ReceiveStatsUpdateTimer_Tick()
- Shows "Metadata Only" instead of buffer level when audio disabled
- Format: `"Playing • MM:SS elapsed • Metadata Only"`

## How It Works

### When Audio is ENABLED (default):
```
✅ Metadata polling active
✅ Audio polling active  
✅ Audio buffer initialized
✅ Playback starts when buffered
📊 Stats show: "Buffer: X chunks"
```

### When Audio is DISABLED:
```
✅ Metadata polling active
❌ Audio polling disabled
❌ Audio buffer NOT initialized
❌ No audio playback
📊 Stats show: "Metadata Only"
```

## User Experience

1. **Default Behavior**: Audio is enabled (checked)
2. **To disable audio**: Uncheck before clicking "Start Receiving"
3. **Cannot change mid-stream**: Checkbox is disabled while receiving
4. **Clear feedback**: Status text shows "Metadata Only" when audio off

## Use Cases

### Metadata-Only Mode (Audio Disabled)
- **Use Case 1**: Preview what's playing without sound
- **Use Case 2**: Low bandwidth situations
- **Use Case 3**: Just want to see lyrics/song info
- **Use Case 4**: Silent monitoring of shared stream

### Full Stream Mode (Audio Enabled)
- **Use Case 1**: Listen along with the sharer
- **Use Case 2**: Karaoke with synced lyrics
- **Use Case 3**: Remote music listening experience

## Technical Details

### Bandwidth Savings
When audio is disabled:
- **Saved**: ~176 KB/s audio stream (44.1kHz stereo)
- **Still used**: ~1-2 KB/s metadata
- **Total reduction**: ~99% bandwidth usage

### Memory Usage
When audio is disabled:
- **No audio buffer allocation** (saves ~5MB for 5-second buffer)
- **No audio player instance**
- **No NAudio resources**

### Debug Logging
```
🔊 Audio reception enabled
🔇 Audio reception disabled (metadata only mode)
```

## Code Highlights

### Conditional Polling Start
```csharp
if (_receiveAudio)
{
    _ = Task.Run(() => PollAudioAsync(_cancellationTokenSource.Token));
    System.Diagnostics.Debug.WriteLine("🔊 Audio reception enabled");
}
else
{
    System.Diagnostics.Debug.WriteLine("🔇 Audio reception disabled (metadata only)");
}
```

### Early Exit in Audio Handler
```csharp
private void ShareService_AudioChunkReceived(object? sender, AudioChunk chunk)
{
    // Skip audio processing if audio reception is disabled
    if (!_shareService.ReceiveAudio)
    {
        return;
    }
    // ... rest of audio processing
}
```

## Testing Checklist

- [x] Build compiles without errors
- [ ] Default checkbox state is checked
- [ ] Audio plays when checked
- [ ] Metadata updates when unchecked (no audio)
- [ ] Checkbox disabled while receiving
- [ ] Checkbox re-enabled after stopping
- [ ] Status text shows "Metadata Only" when audio off
- [ ] Presentation window still updates (metadata only)
- [ ] Tooltip displays correctly

## Future Enhancements

1. **Dynamic toggle**: Allow changing audio reception while streaming
2. **Quality selector**: Choose audio quality (low/medium/high bitrate)
3. **Auto-disable**: Automatically disable audio on poor connection
4. **Bandwidth indicator**: Show real-time bandwidth usage

## Related Files

- `MusicShare.xaml` - UI definition
- `MusicShare.xaml.cs` - UI logic
- `MusicShareService.cs` - Service layer
- `MusicSharePresentation.xaml.cs` - Presentation window (unaffected by audio toggle)
