using System;
using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

public class TimeDisplayTests
{
    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(65, "01:05")]
    [InlineData(3599, "59:59")]
    [InlineData(3600, "1:00:00")]
    [InlineData(3903, "1:05:03")]
    [InlineData(7325, "2:02:05")]
    public void Clock_UsesHoursWhenNeeded(int seconds, string expected)
    {
        Assert.Equal(expected, TimeDisplay.Clock(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Clock_NegativeValue_IsClampedToZero()
    {
        Assert.Equal("00:00", TimeDisplay.Clock(TimeSpan.FromSeconds(-5)));
    }
}
