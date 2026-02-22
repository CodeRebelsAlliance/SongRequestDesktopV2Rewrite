# Drag Songs from YouTube Form to Soundboard

## Overview
Complete implementation of dragging downloaded song requests from YoutubeForm directly to the Soundboard buttons.

## Features

### ✨ Drag Icon
- **Location:** Left of status text on each song panel
- **Appearance:** `⋮⋮` (vertical grip icon)
- **Color:** Blue (#5B8DEF) to match accent
- **Tooltip:** "Drag to Soundboard"
- **Cursor:** Hand cursor on hover

### 🎵 Drag Functionality
- **Drag Source:** Song request panels in YoutubeForm
- **Drag Target:** Empty soundboard buttons
- **Visual Feedback:** Green border + background tint on valid drop
- **Data Transfer:** Song title, video ID, and file path

### 🎯 Drop Behavior
- **Empty Buttons Only:** Can only drop on empty soundboard slots
- **Auto-Copy:** Copies MP3 file to soundboard folder
- **Smart Naming:** Uses sanitized song title as filename
- **Metadata Preserved:** Includes duration and title

## User Experience

### Dragging a Song

1. **Download a song** in YoutubeForm (status shows "Downloaded")
2. **Hover over drag icon** (`⋮⋮`) - cursor changes to hand
3. **Click and hold** left mouse button
4. **Drag over soundboard** window
5. **Hover over empty button** - green border appears
6. **Release mouse** - song added to button!

### Visual Flow
```
YouTube Panel → Drag Icon → Soundboard Button → Green Feedback → Drop → Added!
```

### Button States

**Before Drop:**
```
┌─────────────┐
│  Empty 1    │
│     🔊      │
│   Ready     │
└─────────────┘
```

**During Hover:**
```
┌─────────────┐  ← Green border (3px)
│  Empty 1    │  ← Green tint
│     🔊      │
│   Ready     │
└─────────────┘
```

**After Drop:**
```
┌─────────────┐
│ Never Gonna │  ← Song title (truncated)
│  Give You   │
│   Ready     │
└─────────────┘
```

## Implementation Details

### YouTube Form Changes

**1. DragSongData Class**
```csharp
public class DragSongData
{
    public string VideoId { get; set; }   // YouTube video ID
    public string Title { get; set; }      // Video title
    public string FilePath { get; set; }   // Path to MP3 file
}
```

**2. Drag Icon UI**
```csharp
var dragIcon = new TextBlock
{
    Text = "⋮⋮",
    FontSize = 18,
    FontWeight = FontWeights.Bold,
    Foreground = new SolidColorBrush(Color.FromRgb(91, 141, 239)),
    Cursor = Cursors.Hand,
    ToolTip = "Drag to Soundboard",
    Tag = new DragSongData { /* ... */ }
};
dragIcon.MouseDown += DragIcon_MouseDown;
```

**3. Drag Start Handler**
```csharp
private void DragIcon_MouseDown(object sender, MouseButtonEventArgs e)
{
    if (e.LeftButton == MouseButtonState.Pressed && sender is TextBlock dragIcon)
    {
        var dragData = dragIcon.Tag as DragSongData;
        
        // Validate file exists
        if (string.IsNullOrEmpty(dragData.FilePath) || !File.Exists(dragData.FilePath))
        {
            AppendConsoleText("⚠ Cannot drag: Audio file not yet downloaded");
            return;
        }
        
        // Start drag with custom format
        var data = new DataObject("SongRequestDrag", dragData);
        DragDrop.DoDragDrop(dragIcon, data, DragDropEffects.Copy);
    }
}
```

### Soundboard Changes

**1. Updated DragEnter Handler**
```csharp
private void Button_PreviewDragEnter(object sender, DragEventArgs e)
{
    // Check for song request drag
    if (e.Data.GetDataPresent("SongRequestDrag"))
    {
        e.Effects = DragDropEffects.Copy;
        SetDragOverEffect(button, true);
        return;
    }
    
    // Check for file drop (existing)
    if (e.Data.GetDataPresent(DataFormats.FileDrop))
    {
        // ... existing file drop code
    }
}
```

**2. Updated Drop Handler**
```csharp
private async void Button_PreviewDrop(object sender, DragEventArgs e)
{
    // Handle song request drop
    if (e.Data.GetDataPresent("SongRequestDrag"))
    {
        var dragData = e.Data.GetData("SongRequestDrag") as YoutubeForm.DragSongData;
        await ProcessSongRequestDropAsync(dragData, context);
        return;
    }
    
    // Handle file drop (existing)
    if (e.Data.GetDataPresent(DataFormats.FileDrop))
    {
        // ... existing file drop code
    }
}
```

**3. Process Song Drop**
```csharp
private async Task ProcessSongRequestDropAsync(YoutubeForm.DragSongData dragData, ButtonContext context)
{
    // Sanitize filename from title
    var safeTitle = SanitizeFileName(dragData.Title);
    var fileName = safeTitle + Path.GetExtension(dragData.FilePath);
    
    // Copy to soundboard folder
    var soundboardFolder = SoundboardConfiguration.GetSoundboardFolder();
    var destFilePath = Path.Combine(soundboardFolder, fileName);
    File.Copy(dragData.FilePath, destFilePath, false);
    
    // Get audio duration
    using (var audioFile = new AudioFileReader(destFilePath))
    {
        duration = audioFile.TotalTime.TotalSeconds;
    }
    
    // Update button
    context.Data.Name = TruncateTitle(dragData.Title, 30);
    context.Data.SoundFile = fileName;
    context.Data.Length = duration;
    
    // Save and refresh
    _config.Save();
    InitializeSoundboardGrid();
}
```

**4. Helper Methods**
```csharp
private string SanitizeFileName(string fileName)
{
    var invalid = Path.GetInvalidFileNameChars();
    var sanitized = string.Join("_", fileName.Split(invalid));
    return sanitized.Substring(0, Math.Min(100, sanitized.Length));
}

private string TruncateTitle(string title, int maxLength)
{
    if (title.Length <= maxLength)
        return title;
    return title.Substring(0, maxLength - 3) + "...";
}
```

## File Management

### File Naming

**Original YouTube file:**
```
data/downloadedvideos/dQw4w9WgXcQ.mp3
```

**Soundboard copy:**
```
soundboard/Never_Gonna_Give_You_Up.mp3
```

**Sanitization Rules:**
- Replace invalid characters with `_`
- Max length: 100 characters
- Preserve original extension (.mp3)

### Title Truncation

**Original Title:**
```
"Rick Astley - Never Gonna Give You Up (Official Video)"
```

**Button Display (30 char max):**
```
"Rick Astley - Never Gonna..."
```

### Duplicate Handling

If file already exists in soundboard folder:
```
┌─────────────────────────────────────┐
│ A file named 'song.mp3' already     │
│ exists in the soundboard folder.    │
│ Do you want to use it anyway?       │
│                                     │
│              [Yes] [No]             │
└─────────────────────────────────────┘
```

**Yes:** Use existing file (no copy)
**No:** Cancel drop operation

## Visual Feedback

### Drag States

**Idle (not dragging):**
```
Status Row: [⋮⋮] [Downloaded]
            Blue   Dark Blue
            Hand   
            cursor
```

**Dragging over window:**
```
Mouse cursor shows copy icon (+)
```

**Hovering over valid button:**
```
┌────────────┐
│ Empty Btn  │ ← Green border (3px)
│            │ ← Green tint (30% opacity)
│   Ready    │
└────────────┘
```

**Hovering over invalid button (non-empty):**
```
Mouse cursor shows forbidden icon (⊘)
```

### Console Messages

**Successful Drop:**
```
✓ Copied song request audio to: soundboard/Song_Title.mp3
✓ Audio duration: 3.45 seconds
✓ Song request added: Never Gonna Give You... (3.45s) at position (0, 0)
```

**Error: File Not Downloaded:**
```
⚠ Cannot drag: Audio file not yet downloaded
```

**Error: File Not Found:**
```
⚠ Cannot drag: File not found: data/downloadedvideos/xyz.mp3
```

**Error: Invalid Drop Target:**
```
This button already has a sound assigned.
```

## Debug Output

### Drag Start
```
🎯 DragIcon_MouseDown
   VideoId: dQw4w9WgXcQ
   Title: Never Gonna Give You Up
   FilePath: data/downloadedvideos/dQw4w9WgXcQ.mp3
   ✓ Starting drag operation
```

### Drag Enter
```
🎯 PreviewDragEnter triggered
   Button: Empty 1, IsEmpty: True
   ✓ Song request drag detected - showing green effect
```

### Drop Processing
```
✓ Copied song request audio to: soundboard/Never_Gonna_Give_You_Up.mp3
✓ Audio duration: 3.45 seconds
✓ Song request added: Never Gonna Give You... (3.45s) at position (0, 0)
✓ Soundboard configuration saved
✓ Initialized soundboard grid: 4×3 with 12 buttons
```

## Error Handling

### File Not Downloaded
**Scenario:** User drags before download completes
**Detection:** Check if `dragData.FilePath` is empty or file doesn't exist
**Action:** Show warning in console, cancel drag

### File Copy Error
**Scenario:** Permission error, disk full, etc.
**Detection:** Exception during `File.Copy()`
**Action:** Show error dialog with exception message

### Invalid Characters in Title
**Scenario:** Title contains `/`, `\`, `:`, etc.
**Detection:** Automatic during sanitization
**Action:** Replace with `_`, continue

### Title Too Long
**Scenario:** YouTube title > 100 characters
**Detection:** Length check during sanitization
**Action:** Truncate to 100 characters

## Testing Checklist

- [x] Build compiles without errors
- [ ] Drag icon appears on downloaded songs
- [ ] Drag icon has correct cursor (hand)
- [ ] Drag icon tooltip shows "Drag to Soundboard"
- [ ] Drag icon is blue (#5B8DEF)
- [ ] Cannot drag before download completes
- [ ] Drag starts on left click + hold
- [ ] Green border appears on hover over empty button
- [ ] No feedback on hover over non-empty button
- [ ] Drop copies file to soundboard folder
- [ ] Drop uses song title as button name
- [ ] Long titles are truncated (30 chars)
- [ ] Invalid filename characters replaced with _
- [ ] Duplicate file prompts user
- [ ] Audio duration detected correctly
- [ ] Button updates immediately after drop
- [ ] Config saves after drop
- [ ] Console shows success messages
- [ ] Error messages show for invalid operations

## Future Enhancements

### Planned Features
- [ ] Drag icon shows loading spinner during download
- [ ] Drag preview shows thumbnail while dragging
- [ ] Batch drag (multiple songs at once)
- [ ] Drag to specific position (not just empty)
- [ ] Undo drag operation
- [ ] Drag from blacklist too

### Advanced Features
- [ ] Custom title on drop (edit before adding)
- [ ] Choose specific part of song to use
- [ ] Automatic volume normalization
- [ ] Preview sound before dropping
- [ ] Drag to reorder buttons

## Related Files

- `YoutubeForm.xaml.cs` - Drag source implementation
- `SoundboardWindow.xaml.cs` - Drop target implementation
- `SoundboardModels.cs` - Button data structure
- `SoundboardConfiguration.cs` - Config save/load

## Keyboard Shortcuts (Future)

Potential shortcuts:
- **Ctrl+Drag** - Copy without removing from YouTube list
- **Shift+Drag** - Move (remove from YouTube after adding)
- **Alt+Drag** - Add to multiple buttons

## Known Limitations

1. **Single Drop Only:** Can only drop on one button at a time
2. **No Preview:** Can't preview sound before dropping
3. **No Title Edit:** Can't customize title during drop
4. **File Format:** Only supports original download format (usually MP3)

## Tips & Tricks

### Quick Add Workflow
1. Download multiple songs in YoutubeForm
2. Open Soundboard window (keep both visible)
3. Rapid-fire drag songs to empty buttons
4. Organize by color/position later

### Title Management
- Short, descriptive titles work best
- YouTube title often includes extra info (remove manually later via Edit)
- Use Edit dialog to rename after adding

### File Organization
- All soundboard files go to `soundboard/` folder
- YouTube downloads stay in `data/downloadedvideos/`
- Same file can be used by multiple buttons
