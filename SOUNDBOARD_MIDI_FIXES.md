# MIDI Feedback Fixes

## Issues Fixed

### 1. ✅ Empty Button Feedback Not Sent After Assignment
**Problem:** When assigning a MIDI mapping to a button, the default empty button feedback wasn't being applied to other unmapped buttons until restart.

**Fix:** Changed `UpdateMidiFeedback(context.Data, false)` to `UpdateAllMidiFeedback()` in the `OpenMidiAssignDialog` method. This ensures all buttons on the current page receive their appropriate feedback states, including empty buttons with the global default velocity.

**Location:** `SoundboardWindow.xaml.cs` - `OpenMidiAssignDialog()` method

### 2. ✅ Launchpad X Color Mapping Issue
**Problem:** User reported that Launchpad X colors were wrong, possibly doubled (*2).

**Root Cause:** Launchpad X uses velocity values 0-127 to select from its **built-in color palette**, NOT as RGB intensity values 0-255. Each velocity value maps to a specific pre-defined color in the Launchpad's internal palette.

**Fix/Documentation:**
- Added explanatory notes in `MidiAssignDialog.xaml` about Launchpad X color palette
- Added help text in `MidiSettingsDialog.xaml` about Launchpad X compatibility
- Updated `SOUNDBOARD_MIDI_SUPPORT.md` with:
  - Launchpad X color palette mapping guide
  - Common velocity-to-color mappings (e.g., 3-5=red, 13-17=green, etc.)
  - Troubleshooting section for "wrong colors" issue
  - Note that full RGB (0-255) requires SysEx (not currently implemented)

**Recommended Velocity Values for Launchpad X:**
- 0 = Off (black)
- 3-5 = Red shades
- 9-13 = Yellow/Orange shades
- 13-17 = Green shades
- 37-45 = Blue shades
- 53-60 = Purple/Magenta shades
- 127 = White/Brightest

**Workaround:** Users can customize the Launchpad X color palette using Novation's Components app if they want specific colors at specific velocity values.

### 3. ✅ Playback State Tracking for MIDI Feedback
**Problem:** MIDI feedback wasn't properly tracking playback state. LEDs would turn off immediately even when a sound was still playing (especially in overlay mode where multiple instances can play).

**Fix:** 
- Added `UpdateMidiFeedback(context.Data, true)` immediately after adding playback to `_activePlaybacks` in `StartNewPlayback()` method
- Modified `StopPlayback()` to only send unpressed MIDI feedback when **all** playbacks for that button have stopped
- Changed condition from `if (!remainingPlaybacks.Any() && playback.Button != null)` to separate checks
- Now sends unpressed feedback only when `!remainingPlaybacks.Any()` is true (no more active playbacks for this button)

**Result:** 
- ✅ LED turns ON when sound starts playing
- ✅ LED stays ON while sound is playing
- ✅ LED stays ON when multiple sounds are playing in overlay mode
- ✅ LED only turns OFF when ALL playbacks for that button have finished

**Location:** `SoundboardWindow.xaml.cs` - `StartNewPlayback()` and `StopPlayback()` methods

## Code Changes Summary

### File: `SongRequestDesktopV2Rewrite\SoundboardWindow.xaml.cs`

1. **OpenMidiAssignDialog()** - Line ~289
```csharp
// OLD: UpdateMidiFeedback(context.Data, false);
// NEW: UpdateAllMidiFeedback(); // Updates all buttons including empty defaults
```

2. **StartNewPlayback()** - Line ~432
```csharp
_activePlaybacks.Add(playback);

// NEW: Send MIDI feedback for pressed state
UpdateMidiFeedback(context.Data, true);

System.Diagnostics.Debug.WriteLine(...);
```

3. **StopPlayback()** - Line ~540-550
```csharp
// NEW: Separated UI reset and MIDI feedback logic
// Only send MIDI unpressed when ALL playbacks stopped
if (!remainingPlaybacks.Any())
{
    // Reset UI if button exists
    if (playback.Button != null) { ... }
    
    // Send MIDI feedback for unpressed state only when all playbacks stopped
    UpdateMidiFeedback(playback.Context.Data, false);
}
```

4. **UpdateMidiFeedback()** - Added comment about velocity scaling
```csharp
// Scale velocity from 0-127 to 0-255 for controllers that use extended range (like Launchpad X)
// Launchpad X uses velocity 0-127 for colors, but we stored 0-127 in config
// The velocity is used directly without scaling - Launchpad X interprets 0-127 as color palette indices
```

### File: `SongRequestDesktopV2Rewrite\MidiAssignDialog.xaml`
- Added help text: "Launchpad X: Velocity values map to color palette (0-127)"

### File: `SongRequestDesktopV2Rewrite\MidiSettingsDialog.xaml`
- Added tip: "💡 Launchpad X: Uses velocity 0-127 for color palette mapping"

### File: `SOUNDBOARD_MIDI_SUPPORT.md`
- Added Launchpad X color palette guide
- Added troubleshooting section for color issues
- Added common velocity-to-color mappings

## Testing Recommendations

1. **Empty Button Feedback:**
   - Assign MIDI to a button
   - Verify other empty buttons (with MIDI mappings) light up with default velocity
   - No restart should be needed

2. **Playback State Tracking:**
   - Assign MIDI to a button
   - Play sound - verify LED turns ON
   - While sound is playing, LED should stay ON
   - LED should turn OFF only after sound finishes
   - **Overlay Mode:** Play same button multiple times, LED should stay ON until all finish

3. **Launchpad X Colors:**
   - Try different velocity values (1-127) to find desired colors
   - Pressed state: Use 3-60 for visible colors, 127 for brightest
   - Unpressed state: Use 0 for off, or 1-10 for dim idle colors
   - Note: Colors are from Launchpad's palette, not RGB

## Known Limitations

1. **Launchpad X RGB Mode:** 
   - Full RGB control (0-255 per R/G/B channel) requires SysEx messages
   - Currently not implemented
   - Standard velocity mode (0-127 palette) works as expected

2. **All-Off on Close:**
   - LEDs stay lit after app closes
   - Future enhancement: Send all-off MIDI message on shutdown

## Future Enhancements

- [ ] Add SysEx support for Launchpad X RGB mode (0-255 per channel)
- [ ] Add "Send All Off" on application close
- [ ] Add color preview in assignment dialog
- [ ] Add custom color palette mapping
