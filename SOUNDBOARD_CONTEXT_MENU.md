# Soundboard Button Context Menu & Editor

## Overview
Complete right-click context menu system with Edit and Delete functionality, plus a comprehensive button editor dialog.

## Features

### ✨ Right-Click Context Menu

**Available Actions:**
- ✏️ **Edit** - Open button editor dialog
- 🗑️ **Delete** - Remove sound and optionally delete file

**Behavior:**
- Only appears on **non-empty buttons**
- Styled to match application theme
- Dark background (#202020), white text

### 🎨 Button Editor Dialog

Comprehensive editing interface with:

1. **Name Field**
   - Text input for button display name
   - Required field

2. **Icon Picker**
   - Emoji/icon input (max 4 characters)
   - Default: 🔊

3. **Color Picker**
   - 10 predefined colors
   - Visual color swatches (40×40px)
   - Selected color has white border
   - One-click selection

4. **Sound File Selector**
   - Browse button to select audio file
   - Auto-copies to soundboard folder if needed
   - Won't copy if already exists

5. **Audio Effects**
   - Fade In checkbox
   - Fade Out checkbox

6. **Playback Mode Selector**
   - **Play-Stop** (once) - `"none"`
   - **Play-Loop** (repeat) - `"loop"`
   - **Play-Overlay** (multiple) - `"overlay"`

## Predefined Colors

| Color | Hex Code | Description |
|-------|----------|-------------|
| Gray | `#2A2A2A` | Default |
| Red-Orange | `#FF5733` | Energetic |
| Green | `#33FF57` | Success |
| Blue | `#3357FF` | Cool |
| Magenta | `#FF33F5` | Vibrant |
| Yellow | `#F5FF33` | Bright |
| Cyan | `#33FFF5` | Electric |
| Orange | `#FF8C33` | Warm |
| Purple | `#8C33FF` | Royal |
| Pink | `#FF3383` | Sweet |

## User Experience

### Editing a Button

1. **Right-click** on a non-empty button
2. **Click "✏️ Edit"** in context menu
3. **Edit Dialog Opens** with current values
4. **Modify fields:**
   - Change name
   - Select new color
   - Change icon
   - Toggle fade effects
   - Change playback mode
   - Browse for different sound file
5. **Click "Save"**
6. **Button updates** immediately
7. **Config auto-saved**

### Visual Flow
```
Right-Click → Context Menu → Edit Dialog → Modify → Save → Refresh
```

### Deleting a Button

1. **Right-click** on a non-empty button
2. **Click "🗑️ Delete"** in context menu
3. **Confirmation dialog** appears
4. **Click "Yes"** to confirm
5. **Button reset** to empty state
6. **File check:**
   - If no other button uses the file → **Delete file**
   - If other buttons use it → **Keep file**
7. **Grid refreshes**

### Visual Flow
```
Right-Click → Delete → Confirm → Reset Button → Check File Usage → Delete/Keep → Refresh
```

## Smart File Deletion

The delete function checks all pages before removing files:

```csharp
// Pseudo-code
foreach (page in all_pages) {
    foreach (button in page.buttons) {
        if (button.soundFile == deletedFile) {
            fileUsedElsewhere = true;
            break;
        }
    }
}

if (!fileUsedElsewhere) {
    File.Delete(soundFile);
}
```

**Example:**
- Button A uses `explosion.mp3`
- Button B uses `explosion.mp3`
- Delete Button A → File **NOT deleted** (B still uses it)
- Delete Button B → File **IS deleted** (no more references)

## Button Editor Dialog Layout

```
┌─────────────────────────────────────────┐
│ Edit Sound Button                       │
├─────────────────────────────────────────┤
│ Name:                                   │
│ [explosion              ]               │
│                                         │
│ Icon (emoji):                           │
│ [💥]                                    │
│                                         │
│ Button Color:                           │
│ [■][■][■][■][■][■][■][■][■][■]        │
│                                         │
│ Sound File:                             │
│ [explosion.mp3      ] [Browse...]       │
│                                         │
│ Audio Effects:                          │
│ ☐ Fade In    ☐ Fade Out                │
│                                         │
│ Playback Mode:                          │
│ [Play-Stop (once)     ▼]                │
│                                         │
│                    [Save] [Cancel]      │
└─────────────────────────────────────────┘
```

## Technical Implementation

### Context Menu Creation

```csharp
private void SoundboardButton_RightClick(object sender, MouseButtonEventArgs e)
{
    if (sender is Button button && button.Tag is ButtonContext context)
    {
        if (context.Data.IsEmpty) return; // Don't show for empty
        
        var contextMenu = new ContextMenu
        {
            Background = new SolidColorBrush(Color.FromRgb(32, 32, 32)),
            Foreground = Brushes.White
        };
        
        // Add menu items...
        contextMenu.IsOpen = true;
    }
}
```

### Edit Handler

```csharp
private void EditButton_Click(ButtonContext context)
{
    var dialog = new ButtonEditDialog(context.Data) { Owner = this };
    
    if (dialog.ShowDialog() == true)
    {
        // Update all button properties
        context.Data.Name = dialog.ButtonName;
        context.Data.Color = dialog.ButtonColor;
        // ... etc
        
        // Save and refresh
        _config.Save();
        InitializeSoundboardGrid();
    }
}
```

### Delete with File Check

```csharp
private void DeleteButton_Click(ButtonContext context)
{
    // Confirm
    var result = MessageBox.Show(...);
    if (result != MessageBoxResult.Yes) return;
    
    var soundFile = context.Data.SoundFile;
    
    // Reset button
    context.Data = new SoundboardButton();
    
    // Check if file used elsewhere
    bool fileUsedElsewhere = CheckFileUsage(soundFile);
    
    // Delete file if safe
    if (!fileUsedElsewhere) {
        File.Delete(filePath);
    }
}
```

### Color Picker Implementation

```csharp
private void ColorButton_Click(object sender, RoutedEventArgs e)
{
    if (sender is Button button)
    {
        ButtonColor = (string)button.Tag;
        
        // Update borders
        foreach (Button colorBtn in _colorPanel.Children)
        {
            colorBtn.BorderBrush = (string)colorBtn.Tag == ButtonColor
                ? Brushes.White
                : new SolidColorBrush(Color.FromRgb(64, 64, 64));
        }
    }
}
```

## File Management

### Sound File Browser

- Opens with soundboard folder as default
- Filters: `*.mp3;*.wav;*.m4a;*.aac;*.ogg;*.flac;*.wma`
- Smart copy:
  - If file in soundboard folder → Use directly
  - If file elsewhere → Copy to soundboard folder

```csharp
if (!File.Exists(destPath)) {
    File.Copy(selectedFile, destPath, false);
}
SoundFilePath = destPath;
```

### File Usage Check

Scans all pages and all buttons:
```csharp
bool CheckFileUsage(string filename) {
    foreach (var page in _config.Pages) {
        for (int i = 0; i < page.Buttons.Length; i++) {
            if (!page.Buttons[i].IsEmpty && 
                page.Buttons[i].SoundFile == filename) {
                return true;
            }
        }
    }
    return false;
}
```

## Validation

### Edit Dialog Validation

1. **Name Required**
   ```
   Error: "Please enter a button name."
   ```

2. **Sound File Required**
   ```
   Error: "Please select a sound file."
   ```

3. **Icon Max Length**
   - Limited to 4 characters
   - Allows emojis (multi-byte)

### Delete Confirmation

```
Warning: "Are you sure you want to delete 'explosion'?"
Buttons: [Yes] [No]
Icon: Warning
```

## Playback Modes

### Play-Stop (none)
- Plays sound once
- Stops at end
- New click restarts from beginning
- Default mode

### Play-Loop (loop)
- Plays sound repeatedly
- Loops until manually stopped
- Useful for ambient sounds

### Play-Overlay (overlay)
- Allows multiple simultaneous plays
- Each click starts new instance
- Sounds can overlap
- Useful for sound effects

## Configuration Impact

### Before Edit
```json
{
  "Name": "explosion",
  "Color": "#2A2A2A",
  "Icon": "🔊",
  "FadeIn": false,
  "FadeOut": false,
  "RepeatMode": "none"
}
```

### After Edit
```json
{
  "Name": "Big Boom",
  "Color": "#FF5733",
  "Icon": "💥",
  "FadeIn": true,
  "FadeOut": true,
  "RepeatMode": "loop"
}
```

## Debug Output

### Edit Operation
```
✓ Button edited: Big Boom
✓ Soundboard configuration saved
✓ Initialized soundboard grid: 4×3 with 12 buttons
```

### Delete Operation
```
✓ Button deleted and reset to empty
⚠ File 'explosion.mp3' not deleted - used by other buttons
✓ Soundboard configuration saved
```

Or if file unused:
```
✓ Button deleted and reset to empty
✓ Deleted file: explosion.mp3
✓ Soundboard configuration saved
```

## Error Handling

### File Copy Error
```
Error copying file:
[Exception message]

Action: Show error dialog, keep old file
```

### File Delete Error
```
⚠ Could not delete file: [message]

Action: Log warning, continue (button still reset)
```

### Duration Read Error
```
⚠ Could not read audio duration: [message]

Action: Set duration to 0, continue
```

## Testing Checklist

- [x] Build compiles without errors
- [ ] Right-click shows context menu
- [ ] Context menu only on non-empty buttons
- [ ] Edit dialog opens with current values
- [ ] Can change button name
- [ ] Color picker selects colors
- [ ] Selected color shows white border
- [ ] Icon field accepts emojis
- [ ] Fade checkboxes toggle
- [ ] Playback mode selector works
- [ ] Browse button opens file dialog
- [ ] File copies to soundboard folder
- [ ] Save button updates button
- [ ] Cancel button discards changes
- [ ] Delete shows confirmation
- [ ] Delete resets button
- [ ] File deleted if unused
- [ ] File kept if used elsewhere
- [ ] Grid refreshes after edit/delete

## Future Enhancements

### Planned Features
- [ ] Undo/Redo for edits
- [ ] Bulk edit multiple buttons
- [ ] Copy/Paste button settings
- [ ] Duplicate button
- [ ] Custom color picker (hex input)
- [ ] Volume slider per button
- [ ] Audio preview in editor
- [ ] Waveform visualization
- [ ] Hotkey assignment

### Advanced Features
- [ ] Audio trimming in editor
- [ ] Format conversion
- [ ] Effects presets
- [ ] Button templates
- [ ] Import/export button configs

## Related Files

- `SoundboardWindow.xaml.cs` - Context menu and handlers
- `ButtonEditDialog.cs` - Edit dialog implementation
- `SoundboardModels.cs` - Data models
- `SoundboardConfiguration.cs` - Config save/load

## Keyboard Shortcuts (Future)

Potential shortcuts:
- **F2** - Edit selected button
- **Delete** - Delete selected button
- **Ctrl+E** - Open editor
- **Ctrl+D** - Duplicate button
- **Escape** - Close editor without saving
