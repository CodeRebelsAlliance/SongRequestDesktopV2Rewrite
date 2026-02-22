# System Media Transport Controls (SMTC) - Why It Doesn't Work in WPF

## The Problem

System Media Transport Controls (SMTC) is a **UWP-only API** that doesn't work in WPF applications running on .NET 10. 

### Technical Reasons:

1. **`SystemMediaTransportControls.GetForCurrentView()` requires UWP context**
   - Returns `null` or throws exceptions in WPF
   - Only works in UWP apps with proper XAML Islands setup

2. **Windows Runtime (WinRT) APIs conflict with .NET 10**
   - Adding `Microsoft.Windows.SDK.Contracts` package causes 200+ compilation errors
   - WinRT bridge isn't fully compatible with modern .NET + WPF

3. **MediaPlayer integration doesn't exist in WPF**
   - Documentation shows `MediaPlayer.SystemMediaTransportControls` property
   - This is a UWP `MediaPlayer`, not available in WPF's NAudio/WaveOut

## Attempted Solutions (All Failed)

### ❌ Attempt 1: Direct WinRT API Usage
```csharp
_controls = SystemMediaTransportControls.GetForCurrentView();
// Result: NullReferenceException or InvalidOperationException
```

### ❌ Attempt 2: Windows SDK Contracts Package
```xml
<PackageReference Include="Microsoft.Windows.SDK.Contracts" Version="10.0.19041.41" />
// Result: 200+ compilation errors, type conflicts
```

### ❌ Attempt 3: Update Target Framework
```xml
<TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
// Result: More compatibility issues with CefSharp, NAudio, etc.
```

## Alternative Solutions for WPF

### ✅ Option 1: System Tray Icon with Context Menu (Already Implemented!)
The `AppManager` class provides a system tray icon with context menu:
- Right-click tray icon → "Exit SongRequest" 
- Double-click to show window
- Always accessible, works perfectly in WPF

### ✅ Option 2: Global Hotkeys (Recommended)
Implement Windows global hotkeys using Win32 API:

```csharp
using System.Runtime.InteropServices;

public class GlobalHotkeys
{
    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    
    // Example: Ctrl+Alt+P for Play/Pause
    const uint MOD_CONTROL = 0x0002;
    const uint MOD_ALT = 0x0001;
    const uint VK_P = 0x50;
    
    public void RegisterPlayPauseHotkey(IntPtr windowHandle)
    {
        RegisterHotKey(windowHandle, 1, MOD_CONTROL | MOD_ALT, VK_P);
    }
}
```

**Benefits:**
- Works anywhere, even when app isn't focused
- No dependency on SMTC or UWP
- More powerful (can use any key combination)

### ✅ Option 3: Discord Rich Presence
Show now playing info in Discord:
- Use `DiscordRPC` NuGet package
- Shows song, artist, album art
- More visibility than SMTC for most users

### ✅ Option 4: Web API for Remote Control
Expose a local web API:
- Users can control from phone/browser
- More flexible than SMTC
- Example: `http://localhost:8080/play`

## Why Other Apps Have SMTC

Apps like Spotify, iTunes, and Windows Media Player use SMTC because they:

1. **Use native Windows APIs directly (C++)**
   - Built with Windows SDK, not .NET
   
2. **Are packaged as UWP apps**
   - Spotify uses UWP bridge
   - Windows Media Player is native UWP

3. **Use COM interop with custom marshaling**
   - Complex, requires C++/CLI mixed-mode assemblies
   - Not practical for pure C# WPF apps

## Recommendation

**Use the System Tray Icon** (already implemented) combined with **Global Hotkeys** for the best user experience in WPF.

If media controls are critical, consider:
- Migrating to UWP/WinUI 3 (major rewrite)
- Creating a separate UWP companion app
- Using Electron instead of WPF (has SMTC support via native modules)

## References

- [WPF and WinRT APIs (Microsoft Docs)](https://docs.microsoft.com/en-us/windows/apps/desktop/modernize/desktop-to-uwp-enhance)
- [System Media Transport Controls (UWP Only)](https://docs.microsoft.com/en-us/uwp/api/windows.media.systemmediatransportcontrols)
- [Global Hotkeys in WPF (Stack Overflow)](https://stackoverflow.com/questions/48935/how-do-i-register-a-global-hot-key-for-my-application-in-c)
