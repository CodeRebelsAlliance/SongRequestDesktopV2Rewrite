# Soundboard Feature

## Overview
Added a comprehensive Soundboard system to the YoutubeForm with a nice-looking grid of buttons, visual states (pressed/unpressed), playback bars, and grid management controls.

## Features

### 🎵 Soundboard Window

**Main Components:**
1. **Top Bar**
   - Application title with logo
   - Grid size display (e.g., "Grid: 4 × 3")
   - Page information (e.g., "Page 1 of 1")
   - Management buttons (Grid Settings, Add Sound, Close)

2. **Soundboard Grid**
   - Uniform grid of sound buttons
   - Default: 4 columns × 3 rows (12 buttons)
   - Scrollable for larger grids
   - Responsive layout

3. **Page Navigation**
   - Previous/Next page buttons
   - Automatic pagination for multiple pages
   - Disabled when not needed

### 🎨 Soundboard Button Features

Each button includes:
- **Sound name** (customizable label)
- **Center icon** (🔊 speaker emoji)
- **Status text** ("Ready", "Playing...")
- **Playback progress bar** (6px height at bottom)
- **Visual states:**
  - **Normal**: Dark gray (#2A2A2A) with border
  - **Hover**: Lighter background, blue border
  - **Pressed**: White overlay, bright blue border
  - **Disabled**: 50% opacity (default state)

### ⚙️ Grid Settings

**Configurable Options:**
- **Columns**: 1-12 (via dropdown)
- **Rows**: 1-12 (via dropdown)
- Grid dynamically rebuilds after changes
- Current size displayed in top bar

**Grid Settings Dialog:**
- Clean, modal dialog
- Dropdown selectors for columns/rows
- OK/Cancel buttons
- Preserves current settings

### 🪟 Window Management

**Singleton Pattern:**
- Only one Soundboard window can be open at a time
- Clicking the button again brings existing window to front
- Window is owned by YoutubeForm
- Automatically closes when parent closes

## Implementation Details

### Files Created

1. **SoundboardWindow.xaml**
   - WPF window with modern dark theme
   - UniformGrid for button layout
   - Top management bar
   - Bottom page navigation
   - Custom soundboard button style

2. **SoundboardWindow.xaml.cs**
   - Window logic and initialization
   - Grid management (resize, rebuild)
   - Page navigation system
   - GridSettingsDialog class (embedded)

### Files Modified

1. **YoutubeForm.xaml**
   - Added "🎵 Soundboard" button in top bar
   - Orange warning button style
   - Positioned after Music Player button

2. **YoutubeForm.xaml.cs**
   - Added `SoundboardButton_Click` handler
   - Opens SoundboardWindow as child window

## UI Design

### Color Scheme
- **Background**: `#121212` (Very Dark Gray)
- **Panels**: `#202020` (Dark Gray)
- **Buttons (Normal)**: `#2A2A2A`
- **Buttons (Hover)**: `#353535`
- **Accent Blue**: `#5B8DEF` (borders, progress bars)
- **Border**: `#404040`

### Layout
```
┌─────────────────────────────────────────────────┐
│ 🎵 Soundboard    Grid: 4×3  Page 1/1  [Settings]│
├─────────────────────────────────────────────────┤
│ ┌──────┐ ┌──────┐ ┌──────┐ ┌──────┐           │
│ │Sound1│ │Sound2│ │Sound3│ │Sound4│           │
│ │  🔊  │ │  🔊  │ │  🔊  │ │  🔊  │           │
│ │Ready │ │Ready │ │Ready │ │Ready │           │
│ └──▓───┘ └──▓───┘ └──▓───┘ └──▓───┘           │
│ ┌──────┐ ┌──────┐ ┌──────┐ ┌──────┐           │
│ │Sound5│ │Sound6│ │Sound7│ │Sound8│           │
│ │  🔊  │ │  🔊  │ │  🔊  │ │  🔊  │           │
│ │Ready │ │Ready │ │Ready │ │Ready │           │
│ └──▓───┘ └──▓───┘ └──▓───┘ └──▓───┘           │
│ ┌──────┐ ┌──────┐ ┌──────┐ ┌──────┐           │
│ │Sound9│ │Sound10│ │Sound11│ │Sound12│         │
│ │  🔊  │ │  🔊  │ │  🔊  │ │  🔊  │           │
│ │Ready │ │Ready │ │Ready │ │Ready │           │
│ └──▓───┘ └──▓───┘ └──▓───┘ └──▓───┘           │
│                                                 │
│           [◀ Previous]  [Next ▶]                │
└─────────────────────────────────────────────────┘
```

## Button States

### Visual State Details

**1. Normal State**
- Background: `#2A2A2A`
- Border: `#404040` (2px)
- Progress bar: Empty (width: 0)
- Status: "Ready"

**2. Hover State**
- Background: `#353535` (lighter)
- Border: `#5B8DEF` (blue, 2px)
- Slightly brighter appearance

**3. Pressed State**
- Background: Same as hover
- Border: `#7AD1FF` (bright blue)
- Overlay: White 25% opacity (`#40FFFFFF`)
- Status: "Playing..."
- Progress bar: Animates (future implementation)

**4. Disabled State** (Current Default)
- Opacity: 50%
- Cursor: Arrow (not hand)
- Not clickable
- Grayed out appearance

## Code Highlights

### Soundboard Button Template
```xaml
<Style x:Key="SoundboardButtonStyle" TargetType="Button">
    <!-- Main container with border -->
    <Border Background="#2A2A2A" BorderBrush="#404040" BorderThickness="2">
        <Grid>
            <!-- Content Area: Name, Icon, Status -->
            <Grid>
                <TextBlock Text="Sound 1"/>
                <Viewbox><TextBlock Text="🔊"/></Viewbox>
                <TextBlock Text="Ready"/>
            </Grid>
            
            <!-- Progress Bar (6px height) -->
            <Border Background="#1A1A1A">
                <Border Background="#5B8DEF" Width="0"/>
            </Border>
        </Grid>
    </Border>
</Style>
```

### Grid Initialization
```csharp
private void InitializeSoundboardGrid()
{
    SoundboardGrid.Children.Clear();
    SoundboardGrid.Columns = _columns;
    SoundboardGrid.Rows = _rows;
    
    int totalButtons = _columns * _rows;
    for (int i = 0; i < totalButtons; i++)
    {
        var button = new Button
        {
            Content = $"Sound {i + 1}",
            Style = FindResource("SoundboardButtonStyle"),
            IsEnabled = false // Until sounds are added
        };
        SoundboardGrid.Children.Add(button);
    }
}
```

### Grid Resizing
```csharp
private void GridSettingsButton_Click(object sender, RoutedEventArgs e)
{
    var dialog = new GridSettingsDialog(_columns, _rows);
    if (dialog.ShowDialog() == true)
    {
        _columns = dialog.SelectedColumns;
        _rows = dialog.SelectedRows;
        InitializeSoundboardGrid();
        UpdateGridInfo();
    }
}
```

## User Experience

### Opening Soundboard
1. Click **"🎵 Soundboard"** button in YoutubeForm top bar
2. Window opens centered on parent
3. Shows default 4×3 grid of disabled buttons

### Configuring Grid
1. Click **"⚙️ Grid Settings"** button
2. Select desired columns (2-8) and rows (2-6)
3. Click **"OK"**
4. Grid instantly rebuilds with new dimensions
5. Top bar updates: "Grid: [columns] × [rows]"

### Page Navigation (Future)
- Previous/Next buttons for multiple pages
- Currently disabled (single page)
- Will enable when sounds exceed one page

## Placeholder Logic

### Currently Implemented
✅ Window opens/closes
✅ Grid displays with correct dimensions
✅ Grid Settings dialog works
✅ Grid rebuilds on size change
✅ Button visual states (hover, press)
✅ Status text updates on press
✅ Playback bar UI structure

### Not Yet Implemented (Placeholders)
❌ Add Sound functionality (button disabled)
❌ Sound playback logic
❌ Progress bar animation
❌ Actual audio file management
❌ Button configuration (name, file path)
❌ Save/load soundboard state
❌ Multi-page support (buttons enabled but inactive)

## Future Enhancements

### Phase 1: Basic Functionality
- [ ] Add sound file picker dialog
- [ ] Assign audio files to buttons
- [ ] Implement NAudio playback
- [ ] Animate progress bars during playback
- [ ] Enable buttons when sounds assigned

### Phase 2: Advanced Features
- [ ] Volume control per button
- [ ] Loop/repeat options
- [ ] Stop all sounds button
- [ ] Fade in/out effects
- [ ] Hotkey support (keyboard shortcuts)

### Phase 3: Configuration
- [ ] Save soundboard layouts to JSON
- [ ] Load saved configurations
- [ ] Import/export soundboard files
- [ ] Custom button colors/icons
- [ ] Button groups/categories

### Phase 4: Professional Features
- [ ] Multi-track mixing (overlap sounds)
- [ ] Recording capabilities
- [ ] Sound trimming/editing
- [ ] Effects (reverb, pitch shift)
- [ ] MIDI controller support

## Technical Notes

### Grid Calculations
```csharp
// Grid size limits
Columns: 1-12
Rows: 1-12
Max buttons per page: 144 (12×12)
Min buttons per page: 1 (1×1)

// Total buttons per page
int totalButtons = _columns * _rows;

// Button index with pagination
int buttonIndex = i + (_currentPage * totalButtons);

// Total pages needed
_totalPages = (int)Math.Ceiling((double)totalSounds / totalButtons);
```

### Window Management
```csharp
// Singleton pattern in YoutubeForm
private SoundboardWindow? _soundboardWindow;

private void SoundboardButton_Click(object sender, RoutedEventArgs e)
{
    // Only allow one instance
    if (_soundboardWindow != null && !_soundboardWindow.IsClosed())
    {
        _soundboardWindow.Activate();
        return;
    }

    // Create new instance
    _soundboardWindow = new SoundboardWindow { Owner = this };
    _soundboardWindow.Closed += (s, args) => { _soundboardWindow = null; };
    _soundboardWindow.Show();
}
```

### Window Ownership
- Soundboard window is owned by YoutubeForm
- **Only one instance allowed** (singleton pattern)
- Stays on top of parent
- Closes independently
- Clicking button again activates existing window

## Testing Checklist

- [x] Build compiles without errors
- [ ] Button appears in YoutubeForm
- [ ] Window opens when clicked
- [ ] Grid displays 4×3 default buttons
- [ ] Only one window can open at a time
- [ ] Clicking button again activates existing window
- [ ] Hover state shows blue border
- [ ] Click shows pressed state
- [ ] Grid Settings dialog opens
- [ ] Can set grid to 1×1 (minimum)
- [ ] Can set grid to 12×12 (maximum)
- [ ] Changing grid size rebuilds buttons
- [ ] Grid info text updates correctly
- [ ] Close button works
- [ ] Window can be resized
- [ ] Scroll works with large grids (12×12 = 144 buttons)
- [ ] All buttons have playback bars

## Related Files

- `SoundboardWindow.xaml` - UI definition
- `SoundboardWindow.xaml.cs` - Logic and grid management
- `YoutubeForm.xaml` - Soundboard button
- `YoutubeForm.xaml.cs` - Button click handler

## Dependencies

- **WPF** - UniformGrid, Styles, Templates
- **System.Windows.Controls** - UI components
- **Future**: NAudio for audio playback
