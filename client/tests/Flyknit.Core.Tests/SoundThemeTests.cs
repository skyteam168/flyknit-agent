using Flyknit.Core.Notices;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>合成提示音：每种音色、每种时刻都能生成合法的 WAV，三种时刻听得出不一样，非合成音色不生成。</summary>
public class SoundThemeTests
{
    public static IEnumerable<object[]> All() =>
        SoundThemes.Synth.SelectMany(theme => Enum.GetValues<SoundCue>().Select(cue => new object[] { theme, cue }));

    [Theory]
    [MemberData(nameof(All))]
    public void EverySynthThemeProducesAValidShortWav(string theme, SoundCue cue)
    {
        var wav = SoundThemes.Wav(theme, cue)!;
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal(wav.Length - 8, BitConverter.ToInt32(wav, 4));
        var dataBytes = BitConverter.ToInt32(wav, 40);
        Assert.Equal(wav.Length - 44, dataBytes);
        var seconds = dataBytes / 2.0 / 22050;
        Assert.InRange(seconds, 0.1, 2.5); // 提示音要短
        // 有声音，但不爆音：峰值在 16 位满幅的一半以内
        var peak = Enumerable.Range(0, dataBytes / 2).Max(i => Math.Abs((int)BitConverter.ToInt16(wav, 44 + i * 2)));
        Assert.InRange(peak, 3000, short.MaxValue / 2);
    }

    [Fact]
    public void CuesSoundDifferentWithinATheme()
    {
        foreach (var theme in SoundThemes.Synth)
        {
            var done = SoundThemes.Wav(theme, SoundCue.Done)!;
            var attention = SoundThemes.Wav(theme, SoundCue.Attention)!;
            var failed = SoundThemes.Wav(theme, SoundCue.Failed)!;
            Assert.False(done.SequenceEqual(attention));
            Assert.False(done.SequenceEqual(failed));
            Assert.False(attention.SequenceEqual(failed));
        }
    }

    [Theory]
    [InlineData("none")]
    [InlineData("soft")]
    [InlineData("alert")]
    [InlineData("../../evil")]
    [InlineData("")]
    public void NonSynthThemesProduceNothing(string theme) => Assert.Null(SoundThemes.Wav(theme, SoundCue.Done));

    [Theory]
    [InlineData("chime", "chime")]
    [InlineData("soft", "soft")]
    [InlineData("alert", "alert")]
    [InlineData("loud-siren", "none")]
    [InlineData(null, "none")]
    public void UnknownThemesFallBackToSilence(string? input, string expected) => Assert.Equal(expected, SoundThemes.Normalize(input));
}
