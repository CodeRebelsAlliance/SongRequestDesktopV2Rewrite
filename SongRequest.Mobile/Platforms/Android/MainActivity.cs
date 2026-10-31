using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using System;
using System.Threading.Tasks;

namespace SongRequest.Mobile;

[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation |
                           ConfigChanges.UiMode | ConfigChanges.ScreenLayout |
                           ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    private const int FolderPickerRequestCode = 4711;

    private static TaskCompletionSource<string?>? _folderPick;

    /// <summary>Shows Android's folder picker and returns the picked tree URI (or null).</summary>
    public static Task<string?> PickFolderAsync()
    {
        var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity as MainActivity;
        if (activity == null) return Task.FromResult<string?>(null);

        var tcs = new TaskCompletionSource<string?>();
        _folderPick = tcs;

        try
        {
            var intent = new Intent(Intent.ActionOpenDocumentTree);
            intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantPersistableUriPermission);
            activity.StartActivityForResult(intent, FolderPickerRequestCode);
        }
        catch (Exception ex)
        {
            Android.Util.Log.Debug("SongRequest", $"folder picker could not start: {ex.Message}");
            _folderPick = null;
            tcs.TrySetResult(null);
        }

        return tcs.Task;
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);

        if (requestCode != FolderPickerRequestCode) return;

        string? uri = null;
        if (resultCode == Result.Ok && data?.Data != null)
        {
            uri = data.Data.ToString();

            try
            {
                // Keep read access for the copy that follows (and any retry).
                ContentResolver?.TakePersistableUriPermission(data.Data, ActivityFlags.GrantReadUriPermission);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Debug("SongRequest", $"persistable permission failed: {ex.Message}");
            }
        }

        _folderPick?.TrySetResult(uri);
        _folderPick = null;
    }

    /// <summary>
    /// Android is low on memory: keep the visible page, drop the hidden web views.
    /// Normal tab switches never evict (see MainPage), only this callback does.
    /// </summary>
    public override void OnTrimMemory(TrimMemory level)
    {
        base.OnTrimMemory(level);

        if (level >= TrimMemory.RunningLow)
            AppHost.Current?.Page.TrimResidentViews();
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        UpdateCarMode();

        // The notification permission is requested when playback first starts
        // (see AndroidPlatform.RequestNotificationPermissionOnce), so startup is
        // never blocked by a system dialog.
    }

    public override void OnConfigurationChanged(Android.Content.Res.Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        UpdateCarMode();
    }

    private void UpdateCarMode()
    {
        try
        {
            var configuration = Resources?.Configuration;
            int mode = configuration == null ? 0 : (int)configuration.UiMode;
            bool car = (mode & (int)Android.Content.Res.UiMode.TypeMask) ==
                       (int)Android.Content.Res.UiMode.TypeCar;
            CarModeState.SetUiMode(car);
            AppHost.Current?.ApplyCarMode();
        }
        catch (Exception ex)
        {
            Android.Util.Log.Debug("SongRequest", $"car mode detection failed: {ex.Message}");
        }
    }
}
