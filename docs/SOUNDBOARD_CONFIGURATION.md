# Soundboard Configuration System

## Overview
Complete save/load system for soundboard pages with 12×12 button grids stored in JSON format.

## Folder Structure
```
data/
├── soundboard_config.json          # Main configuration file
└── soundboard/                      # Audio files folder
    ├── sound1.mp3
    ├── sound2.wav
    └── ...
```

## JSON Structure

### soundboard_config.json
```json
{
  "Pages": [
    {
      "Id": "guid-string",
      "Name": "Page 1",
      "Columns": 4,
      "Rows": 3,
      "Buttons": [
        // Array of 144 buttons (12×12 grid)
        {
          "Index": 0,
          "Name": "Explosion",
          "SoundFile": "explosion.mp3",
          "Icon": "💥",
          "Color": "#FF5733",
          "Length": 2.5,
          "FadeIn": false,
          "FadeOut": true,
          "RepeatMode": "none",
          "IsEnabled": true
        },
        {
          "Index": 1,
          "Name": "Empty",
          "SoundFile": "",
          "Icon": "🔊",
          "Color": "#2A2A2A",
          "Length": 0.0,
          "FadeIn": false,
          "FadeOut": false,
          "RepeatMode": "none",
          "IsEnabled": false
        }
        // ... 142 more buttons
      ]
    }
  ],
  "CurrentPageIndex": 0
}
```

## Button Properties

| Property | Type | Description | Example |
|----------|------|-------------|---------|
| `Index` | int | Position in 12×12 grid (0-143) | `0` |
| `Name` | string | Display name | `"Explosion"` |
| `SoundFile` | string | Filename only (in soundboard folder) | `"explosion.mp3"` |
| `Icon` | string | Optional emoji or icon | `"💥"` |
| `Color` | string | Hex color code | `"#FF5733"` |
| `Length` | double | Duration in seconds | `2.5` |
| `FadeIn` | bool | Fade in on start | `false` |
| `FadeOut` | bool | Fade out on end | `true` |
| `RepeatMode` | string | `"none"`, `"loop"`, `"repeat-n"` | `"none"` |
| `IsEnabled` | bool | Button enabled state | `true` |

## Repeat Modes
- **`"none"`** - Play once
- **`"loop"`** - Loop indefinitely until stopped
- **`"repeat-n"`** - Repeat N times (future: add RepeatCount property)

## Grid Mapping

Buttons are stored in a flat array of 144 elements (12×12), indexed as:
```
Index = Row × 12 + Column
```

### Example Mappings
- Button at (0, 0) → Index 0
- Button at (0, 1) → Index 1
- Button at (1, 0) → Index 12
- Button at (2, 3) → Index 27
- Button at (11, 11) → Index 143 (last)

### Grid Size Independence
Even if a page uses a 4×3 grid (12 buttons), all 144 slots are saved:
- Slots 0-11: Used (visible in UI)
- Slots 12-143: Empty (not displayed, but saved)

This allows:
- Easy grid resizing without data loss
- Consistent file structure
- Simple indexing logic

## Auto-Save Triggers

Configuration saves automatically when:
1. **Grid size changed** (`GridSettingsButton_Click`)
2. **Page added** (`AddPage`)
3. **Page deleted** (`DeletePage`)
4. **Page moved** (`MovePage`)
5. **Page renamed** (via PageManagementDialog)
6. **Current page changed** (navigation)

## Auto-Load

Configuration loads automatically:
1. **On window initialization** (`SoundboardWindow` constructor)
2. **Default config created** if file doesn't exist
3. **Validation** ensures all pages have 144-button arrays

## API

### SoundboardConfiguration Methods

```csharp
// Load from file (static)
var config = SoundboardConfiguration.Load();

// Save to file
config.Save();

// Get current page
var page = config.GetCurrentPage();

// Add new page
var newPage = config.AddPage("Page 2", 6, 4);

// Delete page
bool deleted = config.DeletePage(1);

// Move page
bool moved = config.MovePage(0, 2);

// Get soundboard folder path
string folder = SoundboardConfiguration.GetSoundboardFolder();
```

### SoundboardPage Methods

```csharp
// Get button at grid position
var button = page.GetButton(row, col);

// Set button at grid position
page.SetButton(row, col, buttonData);

// Total visible slots
int slots = page.TotalSlots; // Columns × Rows
```

### SoundboardButton Properties

```csharp
var button = new SoundboardButton
{
    Name = "Explosion",
    SoundFile = "explosion.mp3",
    Icon = "💥",
    Color = "#FF5733",
    Length = 2.5,
    FadeIn = false,
    FadeOut = true,
    RepeatMode = "loop",
    IsEnabled = true
};

// Check if button is empty
bool isEmpty = button.IsEmpty; // true if SoundFile is empty
```

## File Locations

**Windows Paths:**
```
C:\Users\[User]\source\repos\SongRequestDesktopV2Rewrite\
└── SongRequestDesktopV2Rewrite\
    └── bin\
        └── x64\
            └── Debug\
                └── net10.0-windows\
                    ├── data\
                    │   ├── soundboard_config.json
                    │   └── soundboard\
                    │       ├── explosion.mp3
                    │       └── applause.wav
```

**Relative to executable:**
```
./data/soundboard_config.json
./data/soundboard/[audio files]
```

## Empty Button Defaults

When a button slot is empty:
```json
{
  "Index": 5,
  "Name": "Empty",
  "SoundFile": "",
  "Icon": "🔊",
  "Color": "#2A2A2A",
  "Length": 0.0,
  "FadeIn": false,
  "FadeOut": false,
  "RepeatMode": "none",
  "IsEnabled": false
}
```

## Validation

### On Load
- Ensures all pages have 144-button arrays
- Initializes missing buttons with empty defaults
- Creates default page if config is empty

### On Save
- Creates `data/` folder if missing
- Creates `data/soundboard/` folder if missing
- Uses JSON indented formatting for readability

## Debug Logging

```csharp
// Load
✓ Loaded soundboard config: 2 pages
✓ Created data folder: C:\...\data
✓ Created soundboard folder: C:\...\data\soundboard

// Save
✓ Soundboard configuration saved to C:\...\data\soundboard_config.json

// Initialize
✓ Initialized soundboard grid: 4×3 with 12 buttons
```

## Future Enhancements

### Planned Features
- [ ] Button editor dialog
- [ ] Audio file picker
- [ ] Color picker for buttons
- [ ] Icon selector (emoji picker)
- [ ] Import/export pages
- [ ] Duplicate page
- [ ] Button copy/paste

### Button Properties to Add
- [ ] `RepeatCount` (int) - for "repeat-n" mode
- [ ] `FadeInDuration` (double) - custom fade time
- [ ] `FadeOutDuration` (double) - custom fade time
- [ ] `VolumeMultiplier` (double) - per-button volume
- [ ] `HotkeyBinding` (string) - keyboard shortcut

## Example Usage

### Creating a Sound Button Programmatically
```csharp
var page = config.GetCurrentPage();
var button = new SoundboardButton
{
    Name = "Victory",
    SoundFile = "victory.mp3",
    Icon = "🏆",
    Color = "#FFD700",
    Length = 3.2,
    FadeIn = true,
    FadeOut = true,
    RepeatMode = "none",
    IsEnabled = true
};
page.SetButton(0, 0, button); // Top-left corner
config.Save();
```

### Resizing Grid Without Data Loss
```csharp
// Change from 4×3 to 6×6
var page = config.GetCurrentPage();
page.Columns = 6;
page.Rows = 6;
config.Save();

// All button data preserved in 12×12 array
// Now showing 36 buttons instead of 12
// Previous buttons 0-11 still there
// New buttons 12-35 are empty defaults
```

## Testing Checklist

- [x] Config file saves to `data/soundboard_config.json`
- [x] Soundboard folder created at `data/soundboard/`
- [x] 12×12 array structure (144 buttons per page)
- [x] All button properties included
- [x] Auto-load on window open
- [x] Auto-save on changes
- [x] Grid size changes preserve data
- [x] Page navigation works
- [ ] Button data displays in UI
- [ ] Empty buttons show correctly
- [ ] Validation handles corrupt files

## Related Files

- `SoundboardModels.cs` - Data models and save/load logic
- `SoundboardWindow.xaml.cs` - UI integration
- `PageManagementDialog.cs` - Page management UI
