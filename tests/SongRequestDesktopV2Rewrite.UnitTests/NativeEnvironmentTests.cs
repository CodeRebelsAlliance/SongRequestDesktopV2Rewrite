using System;
using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

/// <summary>
/// Guards the fix for a subtle but critical .NET behaviour: on Unix
/// <see cref="Environment.SetEnvironmentVariable(string,string)"/> only updates the
/// *managed* environment, so native libraries (GTK/WebKitGTK/OpenAL) still see
/// nothing through <c>getenv()</c>. Everything native depends on must go through
/// <see cref="NativeEnvironment"/>.
/// </summary>
public class NativeEnvironmentTests
{
    [Fact]
    public void Set_IsVisibleToNativeCode()
    {
        var name = "SONGREQUEST_NATIVE_ENV_" + Guid.NewGuid().ToString("N");
        const string value = "natively-visible";

        NativeEnvironment.Set(name, value);

        Assert.Equal(value, NativeEnvironment.GetNative(name));
        Assert.Equal(value, Environment.GetEnvironmentVariable(name));
    }

    [Fact]
    public void Set_OverwritesAnExistingValue()
    {
        var name = "SONGREQUEST_NATIVE_ENV_" + Guid.NewGuid().ToString("N");

        NativeEnvironment.Set(name, "first");
        NativeEnvironment.Set(name, "second");

        Assert.Equal("second", NativeEnvironment.GetNative(name));
    }
}
