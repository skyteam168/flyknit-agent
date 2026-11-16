namespace Flyknit.Core.Notices;

/// <summary>什么时候响：做完了、等你处理（确认 / 回答 / 到了上限要你决定）、出错了。三种听得出区别。</summary>
public enum SoundCue
{
    Done,
    Attention,
    Failed,
}

/// <summary>
/// 提示音的音色。
/// - soft / alert：Windows 自带的系统声音（原来就有的两种）；
/// - 其余的在这里现场合成（正弦波 + 泛音 + 衰减包络）：不带任何音频文件，没有版权问题，安装包也不变大。
/// 每种音色对三种时刻有各自的旋律：完成往上走，等你处理是两下同音，出错往下走。
/// </summary>
public static class SoundThemes
{
    public const string None = "none";

    /// <summary>系统声音的两种（老设置里就有）。</summary>
    public static readonly IReadOnlyList<string> System = new[] { "soft", "alert" };

    /// <summary>合成的音色。</summary>
    public static readonly IReadOnlyList<string> Synth = new[] { "chime", "marimba", "bell", "digital", "bubble" };

    public static bool IsKnown(string? name) => name is not null && (System.Contains(name) || Synth.Contains(name));

    public static string Normalize(string? name) => IsKnown(name) ? name! : None;

    private const int Rate = 22050;

    private readonly record struct Note(double Freq, double Start, double Length);

    /// <summary>合成音色的 WAV（16 位单声道 22050 Hz）。不是合成音色（none / 系统声音 / 不认识的）返回 null。</summary>
    public static byte[]? Wav(string theme, SoundCue cue)
    {
        if (!Synth.Contains(theme))
        {
            return null;
        }
        var notes = Melody(theme, cue);
        var total = notes.Max(n => n.Start + n.Length) + 0.05;
        var samples = new double[(int)(total * Rate)];
        foreach (var note in notes)
        {
            Render(theme, note, samples);
        }
        // 归一化到适中的音量（不吓人），再转 16 位
        var peak = samples.Select(Math.Abs).DefaultIfEmpty(0).Max();
        var gain = peak > 0 ? 0.32 / peak : 0;
        var pcm = new short[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            pcm[i] = (short)Math.Clamp(samples[i] * gain * short.MaxValue, short.MinValue, short.MaxValue);
        }
        return Encode(pcm);
    }

    // 音高：C5 523.25 · E5 659.25 · G5 783.99 · C6 1046.5 · E6 1318.5 · G6 1568.0 · Eb5 622.25 · A4 440
    private static Note[] Melody(string theme, SoundCue cue)
    {
        var (low, mid, high) = theme switch
        {
            "marimba" => (523.25, 659.25, 783.99),
            "bell" => (659.25, 783.99, 1046.5),
            "digital" => (880.0, 1174.66, 1760.0),
            "bubble" => (587.33, 783.99, 1174.66),
            _ => (1046.5, 1318.5, 1568.0), // chime
        };
        var step = theme == "digital" ? 0.09 : theme == "bell" ? 0.16 : 0.12;
        var len = theme switch
        {
            "bell" => 1.1,
            "digital" => 0.08,
            "bubble" => 0.14,
            "marimba" => 0.45,
            _ => 0.6,
        };
        return cue switch
        {
            SoundCue.Done => new[] { new Note(low, 0, len), new Note(mid, step, len), new Note(high, step * 2, len * 1.3) },
            SoundCue.Attention => new[] { new Note(mid, 0, len), new Note(mid, step * 1.8, len) },
            _ => new[] { new Note(mid, 0, len), new Note(low * 0.84, step * 1.6, len * 1.4) }, // 往下走，落在小三度
        };
    }

    private static void Render(string theme, Note note, double[] buffer)
    {
        var start = (int)(note.Start * Rate);
        var count = (int)(note.Length * Rate);
        for (var i = 0; i < count && start + i < buffer.Length; i++)
        {
            var t = (double)i / Rate;
            var attack = Math.Min(1, t / 0.004); // 4 ms 起音，避免咔哒声
            double v;
            switch (theme)
            {
                case "marimba":
                    v = Math.Exp(-t * 9) * (Math.Sin(2 * Math.PI * note.Freq * t) + 0.25 * Math.Exp(-t * 30) * Math.Sin(2 * Math.PI * note.Freq * 4 * t));
                    break;
                case "bell":
                    // 钟声的泛音不是整数倍
                    v = Math.Exp(-t * 3.2) * (Math.Sin(2 * Math.PI * note.Freq * t)
                        + 0.5 * Math.Exp(-t * 2) * Math.Sin(2 * Math.PI * note.Freq * 2.76 * t)
                        + 0.25 * Math.Exp(-t * 4) * Math.Sin(2 * Math.PI * note.Freq * 5.4 * t));
                    break;
                case "digital":
                    // 方波的前三个奇次谐波：电子提示音的味道，又不刺耳
                    v = (Math.Sin(2 * Math.PI * note.Freq * t) + Math.Sin(2 * Math.PI * note.Freq * 3 * t) / 3 + Math.Sin(2 * Math.PI * note.Freq * 5 * t) / 5)
                        * (t < note.Length - 0.01 ? 1 : Math.Max(0, (note.Length - t) / 0.01));
                    break;
                case "bubble":
                    // 音高快速往上滑：水滴声
                    var f = note.Freq * (1 + 0.6 * (1 - Math.Exp(-t * 40)));
                    v = Math.Exp(-t * 18) * Math.Sin(2 * Math.PI * f * t);
                    break;
                default: // chime
                    v = Math.Exp(-t * 5) * (Math.Sin(2 * Math.PI * note.Freq * t) + 0.3 * Math.Exp(-t * 8) * Math.Sin(2 * Math.PI * note.Freq * 2 * t));
                    break;
            }
            buffer[start + i] += v * attack;
        }
    }

    private static byte[] Encode(short[] pcm)
    {
        using var ms = new MemoryStream(44 + pcm.Length * 2);
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(36 + pcm.Length * 2);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);            // fmt 块长度
        w.Write((short)1);      // PCM
        w.Write((short)1);      // 单声道
        w.Write(Rate);
        w.Write(Rate * 2);      // 每秒字节数
        w.Write((short)2);      // 每个采样的字节数
        w.Write((short)16);     // 位深
        w.Write("data"u8);
        w.Write(pcm.Length * 2);
        foreach (var s in pcm)
        {
            w.Write(s);
        }
        w.Flush();
        return ms.ToArray();
    }
}
