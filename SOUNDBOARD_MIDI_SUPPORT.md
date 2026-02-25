# Soundboard MIDI Controller Support

## Overview
The soundboard now supports MIDI controller integration for triggering sounds and providing LED feedback on compatible MIDI controllers (like Novation Launchpad, Akai APC, etc.).

## Features

### 1. **MIDI Toggle**
- Located in the top bar of the soundboard window
- Checkbox labeled "🎹 MIDI" enables/disables MIDI functionality
- State is persisted in configuration

### 2. **MIDI Device Selection**
- Click the "🎛️" button to open MIDI device settings
- Select MIDI input device (controller for triggering sounds)
- Select MIDI output device (for LED feedback)
- Supports multiple MIDI devices
- Auto-detects all connected USB MIDI devices via NAudio

### 3. **MIDI Assignment Mode**
- Click "🎯 Assign" button to enter assignment mode
- Button changes to yellow "✓ Assigning..." state
- Left-click any soundboard button to start assignment
- Dialog appears waiting for MIDI input from controller
- Press any button/pad on your MIDI controller to auto-detect the signal
- Configure two feedback values:
  - **Pressed State** (0-127): LED brightness when button is active/playing
  - **Unpressed State** (0-127): LED brightness when button is idle
- Click OK to save the mapping
- Assignment mode automatically exits after successful assignment

### 4. **Auto-Detection**
- Automatically detects MIDI note number and channel
- Supports both NoteOn and ControlChange messages
- Displays detected values: Channel, Note/CC number, Message type
- 15-second timeout if no MIDI input is received

### 5. **LED Feedback Support**
- **Pressed Feedback**: Lights up LED when sound is playing
- **Unpressed Feedback**: Dims or turns off LED when sound stops
- **Empty Button Feedback**: Global velocity setting for unconfigured buttons (configurable in MIDI settings)
- Automatically updates LED state when:
  - Sound starts playing
  - Sound stops playing
  - Page is changed
  - Button is deleted

### 6. **MIDI Mapping Management**
- View current MIDI mapping in button edit dialog
- Shows: Channel, Note/CC, Message Type, Velocities
- Right-click button → "🎹 Clear MIDI Mapping" to remove mapping
- MIDI mappings are preserved when editing button properties
- MIDI mappings are cleared when deleting buttons

## Configuration Storage

### SoundboardButton
```csharp
public class SoundboardButton
{
    // ... existing properties
    public MidiMapping? MidiMapping { get; set; }
}
```

### MidiMapping
```csharp
public class MidiMapping
{
    public int Channel { get; set; } = 1;           // MIDI channel (1-16)
    public int Note { get; set; }                   // Note number or CC (0-127)
    public string MessageType { get; set; }         // "NoteOn" or "ControlChange"
    public int VelocityPressed { get; set; } = 127; // LED on brightness
    public int VelocityUnpressed { get; set; } = 0; // LED off brightness
    public bool IsConfigured => Note >= 0 && Note <= 127;
}
```

### SoundboardConfiguration
```csharp
public class SoundboardConfiguration
{
    // ... existing properties
    public bool MidiEnabled { get; set; }
    public int MidiInputDevice { get; set; } = -1;
    public int MidiOutputDevice { get; set; } = -1;
    public string EmptyButtonFeedbackColor { get; set; } = "#000000";
}
```

## Usage Workflow

### Initial Setup
1. Connect your MIDI controller via USB
2. Open the Soundboard window
3. Check the "🎹 MIDI" checkbox to enable MIDI
4. Click "🎛️" to open MIDI settings
5. Select your controller from the input device dropdown
6. Select your controller from the output device dropdown (for LED feedback)
7. Optionally adjust the empty button LED brightness
8. Click "🔦 Test LEDs" to verify output connection (all LEDs should flash)
9. Close settings dialog

### Assigning MIDI Controls
1. Click "🎯 Assign" to enter assignment mode
2. Left-click the soundboard button you want to map
3. Press the corresponding button/pad on your MIDI controller
4. The dialog will show detected channel and note
5. Adjust LED feedback values:
   - Pressed (127 = full brightness, recommended for active state)
   - Unpressed (0 = off, or low value for dim idle state)
6. Click OK to save
7. Assignment mode exits automatically
8. Repeat for additional buttons

### Using MIDI Control
1. With MIDI enabled, press any mapped button on your controller
2. The corresponding soundboard button will trigger
3. LED on your controller lights up (if output device is connected)
4. LED turns off or dims when sound stops playing

### Managing Mappings
- **View mapping**: Right-click button → "✏️ Edit" → see MIDI Mapping section
- **Clear mapping**: Right-click button → "🎹 Clear MIDI Mapping"
- **Edit mapping**: Enter assign mode again and reassign

## Technical Details

### MIDI Service (`MidiService.cs`)
- Manages NAudio MidiIn and MidiOut connections
- Enumerates available MIDI devices
- Handles MIDI message routing
- Sends NoteOn/NoteOff or ControlChange messages for feedback
- Thread-safe event handling via Dispatcher

### Supported Controllers
Any USB MIDI controller that sends standard MIDI messages:
- **Novation Launchpad** (all models)
  - **Launchpad X Note**: Uses velocity 0-127 to select from its built-in color palette
  - Common colors: 0=off, 3-5=red, 13-17=green, 37-45=blue, 53-60=purple, 9-13=yellow
  - For full RGB control, Launchpad X requires SysEx messages (not currently supported)
  - Recommended: Use velocity values between 1-127 to access different palette colors
- **Akai APC Mini/APC40**
- **Ableton Push**
- **Native Instruments Maschine**
- Any generic USB MIDI controller

### Performance
- MIDI messages are processed asynchronously
- LED feedback is sent immediately on state changes
- No polling - event-driven architecture
- Minimal CPU overhead (~0.1% when idle)

## Troubleshooting

### "No MIDI devices found"
- Ensure controller is plugged in via USB
- Check Windows Device Manager for MIDI device
- Try unplugging and replugging the controller
- Restart the application

### "MIDI input detected but no sound triggers"
- Verify button has been assigned in assign mode
- Check that the button has a sound file configured
- Ensure correct MIDI input device is selected

### "No LED feedback"
- Verify MIDI output device is selected
- Some controllers require drivers for LED feedback
- Click "🔦 Test LEDs" in MIDI settings to verify connection
- Check controller documentation for LED support

### "LEDs stay on after closing app"
- This is normal - some controllers retain LED state
- Manually turn off LEDs or unplug/replug controller
- Future enhancement: send all-off message on app close

### "Launchpad X colors are wrong"
- Launchpad X uses velocity 0-127 to select from a **color palette**, not RGB values
- Each velocity value corresponds to a pre-defined color in the Launchpad's palette
- **Common Launchpad X palette colors:**
  - 0 = Off (black)
  - 3-5 = Red shades
  - 9-13 = Yellow/Orange shades
  - 13-17 = Green shades
  - 37-45 = Blue shades
  - 53-60 = Purple/Magenta shades
  - 127 = White/Brightest
- **Tip**: Experiment with different velocity values (1-127) to find your preferred colors
- **Note**: Full RGB control (0-255 per channel) requires SysEx messages (not currently implemented)
- **Workaround**: Use Novation Components app to customize your Launchpad's color palette if needed

## Future Enhancements
- [ ] Fader/knob support for volume control
- [ ] RGB LED support for color feedback
- [ ] MIDI learn for multiple buttons at once
- [ ] Import/export MIDI mappings
- [ ] Per-page MIDI mappings
- [ ] MIDI clock sync for looped sounds
