# Launchpad X Color Reference for SongRequest Soundboard

## Understanding Launchpad X Colors

The Novation Launchpad X uses **velocity values 0-127** to select colors from its built-in palette, not RGB values. Each velocity number corresponds to a specific pre-defined color.

## Quick Color Guide

### Primary Colors (Recommended Values)

| Velocity | Color | Use Case |
|----------|-------|----------|
| 0 | **Off (Black)** | Unpressed/Idle state |
| 5 | **Red** | Pressed/Active state (danger/stop) |
| 13 | **Yellow** | Pressed/Active state (warning/caution) |
| 17 | **Green** | Pressed/Active state (success/go) |
| 41 | **Blue** | Pressed/Active state (info/cool) |
| 53 | **Purple** | Pressed/Active state (special) |
| 9 | **Orange** | Pressed/Active state (warm) |
| 127 | **White (Brightest)** | Maximum brightness |

### Dim Colors (For Unpressed State)

| Velocity | Color | Use Case |
|----------|-------|----------|
| 1-2 | **Very Dim White** | Subtle idle indicator |
| 3 | **Dark Red** | Dim red for armed state |
| 7-8 | **Dim Orange** | Subtle warm indicator |
| 10 | **Dim Yellow** | Low-brightness idle |
| 14 | **Dim Green** | Low-brightness ready state |

## Recommended Settings by Use Case

### Configuration 1: High Contrast (Recommended)
- **Pressed State:** 17 (Bright Green) or 5 (Bright Red)
- **Unpressed State:** 0 (Off)
- **Empty Buttons:** 1 (Very Dim White)

### Configuration 2: Always-On Dimmed
- **Pressed State:** 17 (Bright Green)
- **Unpressed State:** 10 (Dim Yellow)
- **Empty Buttons:** 1 (Very Dim White)

### Configuration 3: Color-Coded by Type
- **Pressed State:** Different colors per sound category
  - Drums: 5 (Red)
  - Bass: 41 (Blue)
  - Melody: 17 (Green)
  - FX: 53 (Purple)
- **Unpressed State:** 0 (Off)
- **Empty Buttons:** 1 (Very Dim White)

## Setup Instructions

### Initial Setup
1. Open Soundboard window
2. Enable MIDI (check "🎹 MIDI" checkbox)
3. Click "🎛️" for MIDI settings
4. Select "Launchpad X" as both Input and Output device
5. Set "Empty Button LED Color" slider to 1-10 (dim white)
6. Click "Close"

### Assigning Colors to Buttons
1. Click "🎯 Assign" button
2. Click a soundboard button you want to map
3. Press the corresponding pad on your Launchpad X
4. Adjust velocities:
   - **Pressed State:** 5 (red), 13 (yellow), 17 (green), 41 (blue), or 53 (purple)
   - **Unpressed State:** 0 (off) or 1-10 (dim)
5. Click OK

### Testing Colors
1. In MIDI settings, click "🔦 Test LEDs"
2. All pads should flash with medium brightness
3. Press mapped pads - they should light up when sound plays
4. Pads should turn off (or dim) when sound stops

## Troubleshooting

### "Colors are wrong/unexpected"
**Cause:** Launchpad X uses palette indices, not RGB values.
**Solution:** Try different velocity values (1-127) until you find the desired color. Use the reference table above as a starting point.

### "Colors look doubled or too bright"
**Cause:** May be using values that map to white/bright colors in the palette.
**Solution:** Use lower values (1-60) for more saturated colors. Avoid values near 127 unless you want white/bright.

### "Can't get specific RGB color"
**Limitation:** Standard MIDI mode uses pre-defined palette. Full RGB requires SysEx (not currently supported).
**Workaround:** Use Novation Components app to customize the Launchpad X's color palette, then map those palette positions.

### "LEDs stay on after closing app"
**Normal Behavior:** Launchpad X retains LED state. Simply unplug/replug to reset, or press pads to turn them off manually.

## Advanced: Full Color Palette

The Launchpad X has 128 palette colors (0-127). Here's the complete range:

- **0:** Off
- **1-11:** Grayscale (dim to bright white)
- **3-11:** Red spectrum
- **13-60:** Color wheel (green → cyan → blue → purple → pink → red → orange → yellow)
- **61-127:** Variations and special colors

**Tip:** Experiment with values in ranges:
- **Reds:** 3-11
- **Oranges:** 9-13
- **Yellows:** 13-17
- **Greens:** 17-37
- **Cyans:** 33-41
- **Blues:** 37-49
- **Purples:** 49-60
- **Whites:** 1-11, 127

## Reference Links

- [Novation Launchpad X Programmer's Reference](https://fael-downloads-prod.focusrite.com/customer/prod/s3fs-public/downloads/Launchpad%20X%20-%20Programmers%20Reference%20Manual.pdf)
- [Novation Components App](https://components.novationmusic.com/) - For customizing color palette

## Need More Colors?

If you need precise RGB colors (0-255 per R/G/B channel), this would require:
1. SysEx message support (feature request)
2. Launchpad X Programmer mode
3. RGB LED control commands

This is a potential future enhancement. For now, the 128-color palette provides good variety for most use cases.

---

**Last Updated:** 2026-01-12
**Tested With:** Novation Launchpad X Firmware 1.0+
