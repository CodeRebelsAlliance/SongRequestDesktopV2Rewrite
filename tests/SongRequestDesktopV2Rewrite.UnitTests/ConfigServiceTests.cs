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
    public void ApiAndMcp_DefaultToOffAndLoopback()
    {
        var svc = new ConfigService(NewConfigPath());

        Assert.False(svc.Current.ApiEnabled);
        Assert.False(svc.Current.McpEnabled);
        Assert.Equal("127.0.0.1", svc.Current.ApiBindAddress);
        Assert.Equal("127.0.0.1", svc.Current.McpBindAddress);
        Assert.Equal(8730, svc.Current.ApiPort);
        Assert.Equal(8731, svc.Current.McpPort);
        Assert.Equal(string.Empty, svc.Current.ApiToken);
        Assert.Equal(string.Empty, svc.Current.McpToken);
    }

    [Fact]
    public void ApiAndMcp_OptionsPersistWithTheConfig()
    {
        var path = NewConfigPath();
        var svc = new ConfigService(path);

        svc.Update(c =>
        {
            c.ApiEnabled = true;
            c.ApiBindAddress = "0.0.0.0";
            c.ApiPort = 9000;
            c.ApiToken = "api-token";
            c.McpEnabled = true;
            c.McpBindAddress = "127.0.0.1";
            c.McpPort = 9001;
            c.McpToken = "mcp-token";
        });

        var reloaded = new ConfigService(path);
        Assert.True(reloaded.Current.ApiEnabled);
        Assert.Equal("0.0.0.0", reloaded.Current.ApiBindAddress);
        Assert.Equal(9000, reloaded.Current.ApiPort);
        Assert.Equal("api-token", reloaded.Current.ApiToken);
        Assert.True(reloaded.Current.McpEnabled);
        Assert.Equal(9001, reloaded.Current.McpPort);
        Assert.Equal("mcp-token", reloaded.Current.McpToken);
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
