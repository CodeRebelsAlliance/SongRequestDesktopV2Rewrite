using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

public class ConfigServiceTests
{
    private static string NewConfigPath()
        => Path.Combine(TestSupport.NewTempDir(), "config.json");

    [Fact]
    public void NewService_CreatesFileWithDefaults()
    {
        var path = NewConfigPath();

        var svc = new ConfigService(path);

        Assert.True(File.Exists(path));
        Assert.Equal(30, svc.Current.FetchingTimer);
        Assert.Equal(5, svc.Current.Threads);
        Assert.Equal("http://127.0.0.1:5000", svc.Current.Address);
        Assert.Equal("none", svc.Current.DefaultSorting);
    }

    [Fact]
    public void Update_PersistsChange()
    {
        var path = NewConfigPath();
        var svc = new ConfigService(path);

        svc.Update(c => c.Threads = 9);

        var reloaded = new ConfigService(path);
        Assert.Equal(9, reloaded.Current.Threads);
    }

    [Fact]
    public void Load_PatchesInvalidValuesWithDefaults()
    {
        var path = NewConfigPath();
        File.WriteAllText(path, "{\"Threads\":0,\"FetchingTimer\":0,\"Address\":\"\",\"BearerToken\":\"\"}");

        var svc = new ConfigService(path);

        Assert.Equal(5, svc.Current.Threads);
        Assert.Equal(30, svc.Current.FetchingTimer);
        Assert.Equal("http://127.0.0.1:5000", svc.Current.Address);
        Assert.Equal("DefaultToken", svc.Current.BearerToken);
    }

    [Fact]
    public void Load_CorruptFile_FallsBackToDefaults()
    {
        var path = NewConfigPath();
        File.WriteAllText(path, "this is definitely not json");

        var svc = new ConfigService(path);

        Assert.Equal(30, svc.Current.FetchingTimer);
        Assert.Equal(5, svc.Current.Threads);
    }

    [Fact]
    public void ChangingAProperty_RaisesPropertyChanged_AndAutoSaves()
    {
        var path = NewConfigPath();
        var svc = new ConfigService(path);

        string? changed = null;
        svc.Current.PropertyChanged += (_, e) => changed = e.PropertyName;

        svc.Current.Threads = 7;

        Assert.Equal(nameof(Config.Threads), changed);
        Assert.Equal(7, new ConfigService(path).Current.Threads); // auto-saved
    }

    [Fact]
    public void StartupAndWindowOptions_DefaultToOff()
    {
        var svc = new ConfigService(NewConfigPath());

        Assert.False(svc.Current.AutoStart);
        Assert.False(svc.Current.StartMinimized);
        Assert.False(svc.Current.MinimizeToTray);
        Assert.False(svc.Current.CloseToTray);
        Assert.False(svc.Current.GlobalKeyboardShortcuts);
        Assert.False(svc.Current.ToolPromptDismissed);
    }

    [Fact]
    public void StartupAndWindowOptions_Persist()
    {
        var path = NewConfigPath();
        var svc = new ConfigService(path);

        svc.Update(c =>
        {
            c.AutoStart = true;
            c.StartMinimized = true;
            c.MinimizeToTray = true;
            c.CloseToTray = true;
        });

        var reloaded = new ConfigService(path);
        Assert.True(reloaded.Current.AutoStart);
        Assert.True(reloaded.Current.StartMinimized);
        Assert.True(reloaded.Current.MinimizeToTray);
        Assert.True(reloaded.Current.CloseToTray);
    }
}
