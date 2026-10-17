using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

public class VisualizationStreamerTests
{
    [Fact]
    public void FirstFrame_IsEmittedImmediately_WithFormat()
    {
        var engine = new MusicEngine();
        using var streamer = new VisualizationStreamer(engine);
        object? frame = null;
        streamer.FrameReady += f => frame = f;

        streamer.OnSamplesCaptured(null, new float[] { 0f, 1f, -1f });

        Assert.NotNull(frame);
        Assert.Equal(3, TestSupport.Prop<int>(frame!, "sampleCount"));
        Assert.Equal(2, TestSupport.Prop<int>(frame!, "channels"));      // default 2 when no capture provider
        Assert.Equal(44100, TestSupport.Prop<int>(frame!, "sampleRate"));
    }

    [Fact]
    public void Quantization_ClampsAndScalesTo8Bit()
    {
        var engine = new MusicEngine();
        using var streamer = new VisualizationStreamer(engine);
        string? data = null;
        streamer.FrameReady += f => data = TestSupport.Prop<string>(f, "data");

        streamer.OnSamplesCaptured(null, new[] { 0f, 1f, -1f, 0.5f });

        var bytes = Convert.FromBase64String(data!);
        Assert.Equal(4, bytes.Length);
        Assert.Equal(0, (sbyte)bytes[0]);
        Assert.Equal(127, (sbyte)bytes[1]);
        Assert.Equal(-127, (sbyte)bytes[2]);
        Assert.Equal(64, (sbyte)bytes[3]); // round(0.5 * 127)
    }

    [Fact]
    public void Frames_AreThrottledWithinTheInterval()
    {
        var engine = new MusicEngine();
        using var streamer = new VisualizationStreamer(engine);
        int frames = 0;
        streamer.FrameReady += _ => frames++;

        streamer.OnSamplesCaptured(null, new float[100]); // first -> emitted
        streamer.OnSamplesCaptured(null, new float[100]); // immediately after -> throttled

        Assert.Equal(1, frames);
    }

    [Fact]
    public void AfterDispose_NoFramesAreEmitted()
    {
        var engine = new MusicEngine();
        var streamer = new VisualizationStreamer(engine);
        streamer.Dispose();
        object? frame = null;
        streamer.FrameReady += f => frame = f;

        streamer.OnSamplesCaptured(null, new float[10]);

        Assert.Null(frame);
    }

    [Fact]
    public void EmptySampleBatch_IsIgnored()
    {
        var engine = new MusicEngine();
        using var streamer = new VisualizationStreamer(engine);
        int frames = 0;
        streamer.FrameReady += _ => frames++;

        streamer.OnSamplesCaptured(null, Array.Empty<float>());

        Assert.Equal(0, frames);
    }
}
