using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

public class MusicShareServiceTests
{
    [Fact]
    public void NewService_StartsIdleAndEmpty()
    {
        var svc = new MusicShareService();

        Assert.Equal(ShareStatus.Idle, svc.Status);
        Assert.Equal(0, svc.GetBufferLevel());
        Assert.False(svc.TryDequeueAudioChunk(out _));
        Assert.Null(svc.SessionId);
    }

    [Fact]
    public void ProcessReceivedChunks_AboveThreshold_StreamsAndReturnsMaxSequence()
    {
        var svc = new MusicShareService();
        var chunks = Enumerable.Range(1, 4).Select(i => new AudioChunk { SequenceNumber = i, Data = "x" }).ToList();

        var max = svc.ProcessReceivedChunks(chunks);

        Assert.Equal(4, max);
        Assert.Equal(4, svc.GetBufferLevel());
        Assert.Equal(ShareStatus.Streaming, svc.Status);
    }

    [Fact]
    public void ProcessReceivedChunks_LowBuffer_ReportsBuffering()
    {
        var svc = new MusicShareService();

        svc.ProcessReceivedChunks(new[] { new AudioChunk { SequenceNumber = 1 } });

        Assert.Equal(1, svc.GetBufferLevel());
        Assert.Equal(ShareStatus.Buffering, svc.Status);
    }

    [Fact]
    public void ProcessReceivedChunks_RaisesReceivedAndBufferLevelEvents()
    {
        var svc = new MusicShareService();
        int received = 0, bufferLevelEvents = 0;
        svc.AudioChunkReceived += (_, _) => received++;
        svc.BufferLevelChanged += (_, _) => bufferLevelEvents++;

        svc.ProcessReceivedChunks(new[] { new AudioChunk { SequenceNumber = 1 }, new AudioChunk { SequenceNumber = 2 } });

        Assert.Equal(2, received);
        Assert.Equal(1, bufferLevelEvents);
    }

    [Fact]
    public void ProcessReceivedChunks_Empty_IsNoOp()
    {
        var svc = new MusicShareService();

        Assert.Equal(0, svc.ProcessReceivedChunks(Array.Empty<AudioChunk>()));
        Assert.Equal(0, svc.GetBufferLevel());
        Assert.Equal(ShareStatus.Idle, svc.Status);
    }

    [Fact]
    public void TryDequeueAudioChunk_DrainsBuffer()
    {
        var svc = new MusicShareService();
        svc.ProcessReceivedChunks(new[] { new AudioChunk { SequenceNumber = 1, Data = "abc" } });

        Assert.True(svc.TryDequeueAudioChunk(out var chunk));
        Assert.Equal("abc", chunk!.Data);
        Assert.Equal(0, svc.GetBufferLevel());
        Assert.False(svc.TryDequeueAudioChunk(out _));
    }

    [Fact]
    public async Task StopReceiving_ClearsBufferAndResetsStatus()
    {
        var svc = new MusicShareService();
        svc.ProcessReceivedChunks(new[] { new AudioChunk { SequenceNumber = 1 }, new AudioChunk { SequenceNumber = 2 } });

        await svc.StopReceivingAsync();

        Assert.Equal(0, svc.GetBufferLevel());
        Assert.Equal(ShareStatus.Idle, svc.Status);
    }

    [Fact]
    public void ReceiveAudio_IsSettable()
    {
        var svc = new MusicShareService { ReceiveAudio = false };
        Assert.False(svc.ReceiveAudio);
    }
}
