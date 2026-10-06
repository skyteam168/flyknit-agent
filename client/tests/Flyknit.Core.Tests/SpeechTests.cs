using System;
using System.Collections.Generic;
using System.Linq;
using Flyknit.Core.Speech;
using Xunit;

namespace Flyknit.Core.Tests;

public class WavWriterTests
{
    private static byte[] Silence(int samples) => new byte[samples * 2];

    [Fact]
    public void WritesAValidRiffHeader()
    {
        var wav = WavWriter.Wrap(new List<byte[]> { Silence(1600) });

        Assert.Equal(WavWriter.HeaderLength + 3200, wav.Length);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal("fmt ", System.Text.Encoding.ASCII.GetString(wav, 12, 4));
        Assert.Equal("data", System.Text.Encoding.ASCII.GetString(wav, 36, 4));
        // RIFF 块长度 = 文件长度 - 8
        Assert.Equal(wav.Length - 8, BitConverter.ToInt32(wav, 4));
        Assert.Equal(3200, BitConverter.ToInt32(wav, 40));
    }

    [Fact]
    public void HeaderDescribes16kMonoPcm()
    {
        var wav = WavWriter.Wrap(new List<byte[]> { Silence(10) });

        Assert.Equal(1, BitConverter.ToInt16(wav, 20));                       // PCM
        Assert.Equal(1, BitConverter.ToInt16(wav, 22));                       // 单声道
        Assert.Equal(16000, BitConverter.ToInt32(wav, 24));                   // 采样率
        Assert.Equal(16, BitConverter.ToInt16(wav, 34));                      // 位深
        Assert.Equal(2, BitConverter.ToInt16(wav, 32));                       // 块对齐
        Assert.Equal(32000, BitConverter.ToInt32(wav, 28));                   // 每秒字节数
    }

    [Fact]
    public void JoinsChunksInOrder()
    {
        var wav = WavWriter.Wrap(new List<byte[]>
        {
            new byte[] { 1, 0, 2, 0 },
            new byte[] { 3, 0 },
        });

        var payload = wav.Skip(WavWriter.HeaderLength).ToArray();
        Assert.Equal(new byte[] { 1, 0, 2, 0, 3, 0 }, payload);
    }

    [Fact]
    public void HandlesAnEmptyRecording()
    {
        var wav = WavWriter.Wrap(new List<byte[]>());

        Assert.Equal(WavWriter.HeaderLength, wav.Length);
        Assert.Equal(0, BitConverter.ToInt32(wav, 40));
    }
}

public class RecordingLimitsTests
{
    [Fact]
    public void ConvertsBytesToDuration()
    {
        Assert.Equal(1.0, RecordingLimits.DurationOf(32000).TotalSeconds, 3);
        Assert.Equal(0.5, RecordingLimits.DurationOf(16000).TotalSeconds, 3);
        Assert.Equal(TimeSpan.Zero, RecordingLimits.DurationOf(0));
        Assert.Equal(TimeSpan.Zero, RecordingLimits.DurationOf(-5));
    }

    [Fact]
    public void TreatsAVeryShortClipAsAMisclick()
    {
        Assert.True(RecordingLimits.TooShort(1600));    // 50ms
        Assert.False(RecordingLimits.TooShort(32000));  // 1s
    }

    [Fact]
    public void StopsAtTheDurationCap()
    {
        Assert.False(RecordingLimits.Reached(TimeSpan.FromMinutes(2)));
        Assert.True(RecordingLimits.Reached(RecordingLimits.MaxDuration));
        Assert.True(RecordingLimits.Reached(TimeSpan.FromMinutes(5)));
    }
}

public class LevelMeterTests
{
    private static byte[] Tone(int samples, short amplitude)
    {
        var data = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            // 方波就够了，这里只关心幅度
            var v = (i % 2 == 0) ? amplitude : (short)-amplitude;
            data[i * 2] = (byte)(v & 0xFF);
            data[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
        return data;
    }

    [Fact]
    public void SilenceReadsAsZero()
    {
        Assert.Equal(0, LevelMeter.Measure(new byte[320]), 3);
        Assert.True(LevelMeter.IsSilent(LevelMeter.Measure(new byte[320])));
    }

    [Fact]
    public void LouderAudioReadsHigher()
    {
        var quiet = LevelMeter.Measure(Tone(160, 800));
        var loud = LevelMeter.Measure(Tone(160, 20000));

        Assert.True(loud > quiet);
        Assert.InRange(loud, 0, 1);
        Assert.InRange(quiet, 0, 1);
    }

    [Fact]
    public void StaysWithinZeroToOneAtFullScale()
    {
        var level = LevelMeter.Measure(Tone(160, short.MaxValue));
        Assert.InRange(level, 0, 1);
    }

    [Fact]
    public void NormalSpeechIsNotMistakenForAMutedMic()
    {
        // 麦克风被静音时整段都是极小的底噪，正常说话不该落进这个区间
        Assert.True(LevelMeter.IsSilent(LevelMeter.Measure(Tone(160, 3))));
        Assert.False(LevelMeter.IsSilent(LevelMeter.Measure(Tone(160, 4000))));
    }

    [Fact]
    public void ToleratesAnOddLengthBuffer()
    {
        Assert.Equal(0, LevelMeter.Measure(new byte[] { 7 }), 3);
        Assert.Equal(0, LevelMeter.Measure(Array.Empty<byte>()), 3);
    }
}
