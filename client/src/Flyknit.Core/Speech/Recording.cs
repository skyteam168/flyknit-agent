using System;
using System.Collections.Generic;
using System.IO;

namespace Flyknit.Core.Speech;

/// <summary>录音的限制和判断规则。和具体的录音设备无关，单独放这里方便测。</summary>
public static class RecordingLimits
{
    /// <summary>采样率。16kHz 单声道是各家 ASR 的通用输入，再高只是浪费带宽。</summary>
    public const int SampleRate = 16000;
    public const int Channels = 1;
    public const int BitsPerSample = 16;

    /// <summary>一段录音最长多久。超过就自动停止并送去识别，不会无声无息地一直录。</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(3);

    /// <summary>短于这个时长视为误触，不发请求。</summary>
    public static readonly TimeSpan MinDuration = TimeSpan.FromMilliseconds(400);

    public static int BytesPerSecond => SampleRate * Channels * (BitsPerSample / 8);

    public static TimeSpan DurationOf(long byteCount) =>
        TimeSpan.FromSeconds(byteCount <= 0 ? 0 : (double)byteCount / BytesPerSecond);

    public static bool TooShort(long byteCount) => DurationOf(byteCount) < MinDuration;

    public static bool Reached(TimeSpan elapsed) => elapsed >= MaxDuration;
}

/// <summary>把 16 位 PCM 数据包成 WAV。服务端按扩展名判断格式，不包头上游认不出来。</summary>
public static class WavWriter
{
    public const int HeaderLength = 44;

    public static byte[] Wrap(IReadOnlyList<byte[]> chunks, int sampleRate = RecordingLimits.SampleRate,
        int channels = RecordingLimits.Channels, int bits = RecordingLimits.BitsPerSample)
    {
        var total = 0;
        foreach (var c in chunks) { total += c.Length; }

        using var ms = new MemoryStream(HeaderLength + total);
        using (var w = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            var blockAlign = (short)(channels * bits / 8);
            w.Write(new[] { 'R', 'I', 'F', 'F' });
            w.Write(36 + total);
            w.Write(new[] { 'W', 'A', 'V', 'E' });
            w.Write(new[] { 'f', 'm', 't', ' ' });
            w.Write(16);                       // fmt 块长度
            w.Write((short)1);                 // PCM
            w.Write((short)channels);
            w.Write(sampleRate);
            w.Write(sampleRate * blockAlign);  // 每秒字节数
            w.Write(blockAlign);
            w.Write((short)bits);
            w.Write(new[] { 'd', 'a', 't', 'a' });
            w.Write(total);
        }
        foreach (var c in chunks) { ms.Write(c, 0, c.Length); }
        return ms.ToArray();
    }
}

/// <summary>算一段 PCM 的响度，给界面画跳动的竖条，顺便判断“是不是根本没录到声音”。</summary>
public static class LevelMeter
{
    /// <summary>返回 0~1 的响度。安静时接近 0，正常说话在 0.2~0.8。</summary>
    public static double Measure(ReadOnlySpan<byte> pcm16)
    {
        if (pcm16.Length < 2) { return 0; }
        double sum = 0;
        var samples = pcm16.Length / 2;
        for (var i = 0; i + 1 < pcm16.Length; i += 2)
        {
            var v = (short)(pcm16[i] | (pcm16[i + 1] << 8));
            sum += (double)v * v;
        }
        var rms = Math.Sqrt(sum / samples) / short.MaxValue;
        // 人声的动态范围很大，线性显示几乎看不出变化，这里压一下
        return Math.Clamp(Math.Sqrt(rms) * 1.4, 0, 1);
    }

    /// <summary>
    /// 整段的峰值都在这个响度以下，就认为麦克风没真正工作（被静音、被组策略禁用）。
    /// 换算过来大约是满量程的 7/32767，也就是 -73dBFS——这个档位已经是数字静音，
    /// 正常工作的麦克风哪怕只录到环境底噪也会高过它。
    /// </summary>
    public const double SilenceThreshold = 0.02;

    public static bool IsSilent(double peak) => peak < SilenceThreshold;
}
