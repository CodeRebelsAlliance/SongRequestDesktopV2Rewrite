# Soundboard Button Colors

## Overview
Complete color system implementation with predefined color swatches and custom hex color input.

## Features

### ✨ Color Application

**On Load:**
- Buttons display their configured color from JSON
- Empty buttons show default gray (#2A2A2A)
- Invalid colors fall back to default

**On Edit:**
- Changes apply immediately on save
- Grid refreshes with new colors
- Colors persist across sessions

### 🎨 Color Picker

**10 Predefined Colors:**
- Gray `#2A2A2A` - Default
- Red-Orange `#FF5733`
- Green `#33FF57`
- Blue `#3357FF`
- Magenta `#FF33F5`
- Yellow `#F5FF33`
- Cyan `#33FFF5`
- Orange `#FF8C33`
- Purple `#8C33FF`
- Pink `#FF3383`

**Custom Color Input:**
- Hex color format: `#RRGGBB`
- Live preview as you type
- Validation with real-time feedback
- Uppercase normalization

## User Experience

### Using Predefined Colors

1. **Open Edit Dialog** (right-click → Edit)
2. **Click a color swatch** (40×40px button)
3. **Selected color** gets white border
4. **Preview updates** immediately
5. **Click Save** → Button color changes!

### Using Custom Colors

1. **Open Edit Dialog**
2. **Click in "Custom:" text box**
3. **Type hex color** (e.g., `#FF0000`)
4. **Preview updates** as you type
5. **Valid format** → Color applied
6. **Click Save** → Button uses custom color!

### Visual Feedback
```
Predefined:  [■] [■] [■] [■] [■]  (Click any)
             Selected: White border

Custom:      Custom: [#FF0000] [■]
                      ^         ^
                   Input     Preview
```

## Color Format Validation

### Valid Formats
- `#FF0000` - Red
- `#00FF00` - Green  
- `#0000FF` - Blue
- `#123ABC` - Mixed hex
- `#FFFFFF` - White
- `#000000` - Black

### Invalid Formats
- `FF0000` - Missing #
- `#FF00` - Too short
- `#FF00000` - Too long
- `#GGHHII` - Invalid hex
- `rgb(255,0,0)` - Wrong format

## Implementation Details

### Button Color Application

```csharp
// On grid initialization
if (!string.IsNullOrEmpty(buttonData.Color))
{
    try
    {
        button.Background = (Brush)new BrushConverter()
            .ConvertFromString(buttonData.Color);
    }
    catch (Exception ex)
    {
        Debug.WriteLine($"⚠ Invalid color: {ex.Message}");
        // Falls back to style default
    }
}
```

### Custom Color Validation

```csharp
private void CustomColorTextBox_TextChanged(object sender, TextChangedEventArgs e)
{
    var hexColor = _customColorTextBox.Text;
    
    // Regex: #RRGGBB format
    if (Regex.IsMatch(hexColor, @"^#[0-9A-Fa-f]{6}$"))
    {
        try
        {
            var brush = (Brush)new BrushConverter()
                .ConvertFromString(hexColor);
            _customColorPreview.Background = brush;
            ButtonColor = hexColor.ToUpperInvariant();
            
            // Clear predefined selection
            ClearPredefinedSelection();
        }
        catch
        {
            // Show gray if invalid
            _customColorPreview.Background = Brushes.DarkGray;
        }
    }
}
```

### Predefined vs Custom Detection

```csharp
private bool IsCustomColor(string color)
{
    return !PredefinedColors.Contains(color?.ToUpperInvariant());
}
```

## Edit Dialog Layout

```
┌─────────────────────────────────────────┐
│ Edit Sound Button                       │
├─────────────────────────────────────────┤
│ Name:                                   │
│ [Big Boom              ]                │
│                                         │
│ Icon (emoji):                           │
│ [💥]                                    │
│                                         │
│ Button Color:                           │
│ [■][■][■][■][■][■][■][■][■][■]        │
│  ↑ White border = Selected              │
│                                         │
│ Custom: [#FF5733] [■] Format: #RRGGBB  │
│          ^        ^                     │
│        Input   Preview                  │
│                                         │
│ Sound File:                             │
│ [explosion.mp3      ] [Browse...]       │
│                                         │
│ Audio Effects:                          │
│ ☑ Fade In    ☑ Fade Out                │
│                                         │
│ Playback Mode:                          │
│ [Play-Stop (once)     ▼]                │
│                                         │
│                    [Save] [Cancel]      │
└─────────────────────────────────────────┘
```

## Color Behavior

### On Button Click
1. **Predefined color** clicked
2. **ButtonColor** updates to clicked color
3. **White border** shows on clicked swatch
4. **Custom input** resets to `#`
5. **Preview** shows selected color

### On Custom Input
1. **User types** in custom field
2. **Validation** runs on each keystroke
3. **Valid hex** → Preview updates
4. **Invalid hex** → Preview shows gray
5. **Predefined borders** all reset to gray

### On Save
1. **ButtonColor** property saved
2. **Config** written to JSON
3. **Grid** refreshes
4. **New color** displays on button

## Configuration Impact

### Before Color Edit
```json
{
  "Name": "explosion",
  "Color": "#2A2A2A",  // Default gray
  "Icon": "🔊"
}
```

### After Predefined Color
```json
{
  "Name": "explosion",
  "Color": "#FF5733",  // Red-Orange
  "Icon": "💥"
}
```

### After Custom Color
```json
{
  "Name": "explosion",
  "Color": "#FF1493",  // DeepPink (custom)
  "Icon": "💥"
}
```

## Color Persistence

**Colors are saved to:**
- JSON config file
- Per button
- Per page
- Survives app restart

**Colors are loaded:**
- On window open
- On page change
- On grid resize
- After editing

## Debug Output

### Color Applied
```
✓ Applied color #FF5733 to button 'explosion'
```

### Invalid Color
```
⚠ Invalid color '#GGHHII': System.FormatException
```

### Color Changed
```
✓ Button edited: explosion
   Color: #FF5733 → #00FF00
✓ Soundboard configuration saved
```

## Testing Checklist

- [x] Build compiles without errors
- [ ] Predefined colors display on load
- [ ] Custom colors display on load
- [ ] Default gray for empty buttons
- [ ] Clicking predefined color selects it
- [ ] Selected color shows white border
- [ ] Custom input validates hex format
- [ ] Preview updates with valid hex
- [ ] Preview shows gray with invalid hex
- [ ] Custom input clears predefined selection
- [ ] Predefined selection clears custom input
- [ ] Save applies color to button
- [ ] Color persists after grid refresh
- [ ] Color persists after app restart
- [ ] Invalid colors fall back gracefully

## Color Utilities

### Hex to RGB Conversion
```csharp
// Input: "#FF5733"
// Output: R=255, G=87, B=51

var color = (Color)ColorConverter.ConvertFromString("#FF5733");
byte r = color.R; // 255
byte g = color.G; // 87
byte b = color.B; // 51
```

### RGB to Hex Conversion
```csharp
// Input: R=255, G=87, B=51
// Output: "#FF5733"

string hex = $"#{r:X2}{g:X2}{b:X2}";
```

## Future Enhancements

### Planned Features
- [ ] Color picker dialog (visual picker)
- [ ] Recently used colors
- [ ] Color palettes/themes
- [ ] Alpha channel support (#RRGGBBAA)
- [ ] Color name suggestions
- [ ] Copy/paste colors between buttons

### Advanced Features
- [ ] Gradient backgrounds
- [ ] Color animation on press
- [ ] Theme-based color sets
- [ ] Color blindness modes
- [ ] HSL/HSV input formats

## Related Files

- `SoundboardWindow.xaml.cs` - Color application on load
- `ButtonEditDialog.cs` - Color picker UI
- `SoundboardModels.cs` - Color property storage
- `soundboard_config.json` - Color persistence

## Color Best Practices

### Readability
- Use high contrast colors
- White text on dark backgrounds
- Dark text on light backgrounds

### Categorization
- Group similar sounds with similar colors
- Red/Orange for alerts/explosions
- Blue/Cyan for water/tech sounds
- Green for nature/success sounds
- Purple/Pink for special effects

### Accessibility
- Avoid red-green combinations (color blind)
- Use distinct colors for adjacent buttons
- Test colors with different screen settings
