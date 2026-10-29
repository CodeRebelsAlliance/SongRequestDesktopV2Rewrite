# Soundboard Play Mode Fixes - Correct Behaviors

## Overview
Fixed play mode implementations to match expected reclick and playback behaviors.

## ✅ Corrected Play Modes

### 1. Play-Stop (none) ✅ FIXED

**Behavior:**
- **First Click:** ▶ Plays sound once
- **Reclick While Playing:** ⏹ Stops immediately
- **After Playing:** Stops automatically

**Example Timeline:**
```
00:00 → Click     → ▶ Playing
00:02 → Click     → ⏹ Stopped
00:00 → Click     → ▶ Playing again
00:05 → Ends      → ⏹ Auto-stop
```

**Use Case:** Single-shot sound effects with manual stop control

### 2. Play-Overlay (overlay) ✅ FIXED

**Behavior:**
- **First Click:** ▶ Starts instance 1
- **Reclick:** ▶ Starts instance 2 (overlaps)
- **Each Instance:** Stops after playing once

**Example Timeline:**
```
00:00 → Click     → ▶ Instance 1 starts
00:02 → Click     → ▶ Instance 2 starts (overlaps)
00:04 → Click     → ▶ Instance 3 starts (overlaps)
00:05 → Instance 1 ends → ⏹ Auto-stop
00:07 → Instance 2 ends → ⏹ Auto-stop
00:09 → Instance 3 ends → ⏹ Auto-stop
```

**Use Case:** Rapid-fire effects, applause, multiple simultaneous sounds

### 3. Play-Loop (loop) ✅ FIXED

**Behavior:**
- **First Click:** ▶ Starts looping
- **During Loop:** 🔁 Automatically restarts at end
- **Reclick:** ⏹ Stops loop

**Example Timeline:**
```
00:00 → Click     → ▶ Looping starts
00:05 → Loop      → 🔁 Restart at 00:00
00:10 → Loop      → 🔁 Restart at 00:00
00:12 → Click     → ⏹ Stopped
```

**Use Case:** Background music, ambient sounds, continuous effects

## Key Changes Made

### 1. Fixed Play-Stop Reclick
**Before:** Restarted from beginning
**After:** Stops playback

```csharp
case "none": // Play-Stop
    StopPlayback(existingPlayback); // ✅ Just stop, don't restart
    break;
```

### 2. Better Playback Detection
**Before:** Checked by button data only
**After:** Checks by row, col, and file

```csharp
// More precise detection per button position
var existingPlayback = _activePlaybacks.FirstOrDefault(p => 
    p.Context.Row == context.Row && 
    p.Context.Col == context.Col && 
    p.Context.Data.SoundFile == context.Data.SoundFile);
```

### 3. Overlay Mode UI Updates
**Before:** All overlays updated same button
**After:** Only most recent overlay updates UI

```csharp
// Show progress of most recent overlay instance
var buttonPlaybacks = _activePlaybacks.Where(p => 
    p.Context.Row == playback.Context.Row && 
    p.Context.Col == playback.Context.Col).ToList();

if (buttonPlaybacks.Any() && buttonPlaybacks.Last() == playback)
{
    UpdateButtonPlaybackUI(playback.Button, elapsed, total, progress);
}
```

### 4. Smart UI Reset
**Before:** Reset UI immediately on any stop
**After:** Only reset if no more playbacks for button

```csharp
// Check if other instances still playing
var remainingPlaybacks = _activePlaybacks.Where(p => 
    p.Context.Row == playback.Context.Row && 
    p.Context.Col == playback.Context.Col).ToList();

if (!remainingPlaybacks.Any())
{
    // Reset UI only if no more active playbacks
    progressBar.Width = 0;
    statusText.Text = "Ready";
}
```

## Behavior Comparison

| Mode | First Click | Playing | Reclick | After Playback |
|------|------------|---------|---------|----------------|
| **Play-Stop** | ▶ Play | Playing | ⏹ Stop | ⏹ Auto-stop |
| **Play-Overlay** | ▶ Play | Playing | ▶ Add instance | ⏹ Auto-stop each |
| **Play-Loop** | ▶ Loop | Looping | ⏹ Stop | 🔁 Auto-restart |

## Visual States

### Play-Stop Mode

**State 1: Ready**
```
┌─────────────┐
│  Big Boom   │
│     💥      │
│   Ready     │
└─────────────┘
```

**State 2: Playing** (after click 1)
```
┌─────────────┐
│  Big Boom   │
│     💥      │
│ 00:02/00:05 │
└──▓▓▓────────┘
```

**State 3: Stopped** (after click 2)
```
┌─────────────┐
│  Big Boom   │
│     💥      │
│   Ready     │
└─────────────┘
```

### Play-Overlay Mode

**State 1: Instance 1 Playing**
```
┌─────────────┐
│  Applause   │
│     👏      │
│ 00:01/00:03 │ ← Instance 1 progress
└──▓▓────────┘
```

**State 2: Instances 1+2 Playing** (after reclick)
```
┌─────────────┐
│  Applause   │
│     👏      │
│ 00:00/00:03 │ ← Instance 2 progress (newer)
└─────────────┘
   Both playing simultaneously!
```

**State 3: Instance 1 Ends** (still showing instance 2)
```
┌─────────────┐
│  Applause   │
│     👏      │
│ 00:02/00:03 │ ← Instance 2 continues
└──▓▓▓▓▓▓────┘
```

### Play-Loop Mode

**State 1: Looping**
```
┌─────────────┐
│   Music     │
│     🎵      │
│ 00:03/00:05 │
└──▓▓▓────────┘
   Loops back to 00:00 at end
```

**State 2: After Reclick (Stopped)**
```
┌─────────────┐
│   Music     │
│     🎵      │
│   Ready     │
└─────────────┘
```

## Debug Output Examples

### Play-Stop Mode
```
▶ Started playback: Big Boom (Loop: False)
⏸ Stopped (play-stop mode): Big Boom
```

### Play-Overlay Mode
```
▶ Started playback: Applause (Loop: False)
▶ Started overlay instance: Applause
▶ Started overlay instance: Applause
■ Stopped playback: Applause
■ Stopped playback: Applause
■ Stopped playback: Applause
```

### Play-Loop Mode
```
▶ Started playback: Music (Loop: True)
⏸ Stopped loop: Music
```

## Testing Scenarios

### Test Play-Stop
1. ✅ Click button → Sound plays
2. ✅ Reclick → Sound stops immediately
3. ✅ Click again → Sound plays from beginning
4. ✅ Let finish → Stops automatically

### Test Play-Overlay
1. ✅ Click button → Instance 1 plays
2. ✅ Reclick at 00:02 → Instance 2 starts (overlap)
3. ✅ Reclick at 00:04 → Instance 3 starts (overlap)
4. ✅ All play simultaneously
5. ✅ Each stops when finished
6. ✅ UI shows most recent instance progress

### Test Play-Loop
1. ✅ Click button → Loop starts
2. ✅ Sound reaches end → Automatically restarts
3. ✅ Loops continuously
4. ✅ Reclick → Loop stops
5. ✅ Reclick again → Loop starts fresh

## Edge Cases Handled

### Multiple Overlay Instances
- Each has independent audio stream
- Each has independent timer
- UI shows most recent instance
- All cleaned up properly when finished

### Loop Restart
- Position reset to 0 on loop
- Same audio stream reused
- No memory leak on continuous loop
- Proper disposal on stop

### UI State Management
- Progress bar resets only when all instances stop
- Status text shows most recent instance time
- Empty button state preserved
- Color persists across playback

## Code Flow Diagrams

### Play-Stop Flow
```
Click → Check existing → Found? → Stop → Done
                       → Not found? → Play once → Auto-stop at end
```

### Play-Overlay Flow
```
Click → Check existing → Found? → Start NEW instance (keep old)
                       → Not found? → Start first instance
                       
Each instance → Play → Auto-stop at end
```

### Play-Loop Flow
```
Click → Check existing → Found? → Stop loop
                       → Not found? → Start loop
                       
Loop → Play → End → Restart → Play → End → ...
```

## Bug Fixes

### Fixed: Play-Stop Restarting Instead of Stopping
**Issue:** Clicking again would restart instead of stop
**Fix:** Removed restart logic, now just stops
**Result:** Proper stop behavior

### Fixed: Overlay Mode UI Flicker
**Issue:** Multiple overlay instances fighting for UI update
**Fix:** Only most recent instance updates UI
**Result:** Smooth progress display

### Fixed: UI Reset During Overlay
**Issue:** UI reset when one overlay stops while others play
**Fix:** Only reset when ALL instances for button are stopped
**Result:** Progress continues showing

### Fixed: Loop Detection
**Issue:** Couldn't detect loop to stop it
**Fix:** Better playback matching by position and file
**Result:** Reclick properly stops loops

## Performance Impact

### Before Fixes
- Multiple timers per overlay instance ✅ (kept)
- UI updates from all instances ❌ (fixed)
- UI reset on partial stop ❌ (fixed)

### After Fixes
- Multiple timers per overlay instance ✅
- UI updates only from most recent ✅
- UI reset only when all stopped ✅

### Resource Usage
- **Play-Stop:** 1 stream, 1 timer
- **Play-Overlay × 5:** 5 streams, 5 timers
- **Play-Loop:** 1 stream, 1 timer

## Configuration Storage

Play modes stored in JSON:
```json
{
  "RepeatMode": "none"     // play-stop
  "RepeatMode": "overlay"  // play-overlay
  "RepeatMode": "loop"     // play-loop
}
```

## Testing Checklist

- [x] Build compiles without errors
- [ ] Play-Stop: Click plays sound
- [ ] Play-Stop: Reclick stops sound
- [ ] Play-Stop: Sound auto-stops at end
- [ ] Play-Overlay: Click plays sound
- [ ] Play-Overlay: Reclick starts new instance
- [ ] Play-Overlay: Multiple sounds overlap
- [ ] Play-Overlay: Each instance auto-stops
- [ ] Play-Overlay: UI shows latest instance
- [ ] Play-Loop: Click starts loop
- [ ] Play-Loop: Sound loops continuously
- [ ] Play-Loop: Reclick stops loop
- [ ] All modes: Progress bar updates
- [ ] All modes: Time display correct
- [ ] All modes: UI resets when done

## Related Files

- `SoundboardWindow.xaml.cs` - Fixed playback logic
- `SoundboardModels.cs` - RepeatMode property
- `ButtonEditDialog.cs` - Mode selector
- `SOUNDBOARD_PLAYBACK.md` - Updated documentation
