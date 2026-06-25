using System;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Delegates to NewUiWindow secondary PhotinoWindow on the same STA thread.
/// The second window shares the main window's message loop (both on the same
/// STA thread created by NewUiWindow).
/// </summary>
public class MusicPlayerWindow
{
    private readonly YoutubeForm _ytForm;

    public MusicPlayerWindow(YoutubeForm ytForm)
    {
        _ytForm = ytForm;
    }

    public bool IsOpen => _ytForm.NewUiRef?.IsOpen ?? false;

    public void Show()
    {
        _ytForm.NewUiRef?.ShowMusicPlayerWindow();
    }

    public void Close()
    {
        _ytForm.NewUiRef?.CloseMusicPlayerWindow();
    }
}
