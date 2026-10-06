using System;
using System.Threading;
using System.Threading.Tasks;
using Flyknit.Client.Services.Audio;
using Flyknit.Core.Gateway;
using Flyknit.Core.Speech;

namespace Flyknit.Client.Services;

public sealed record SpeechOutcome(bool Ok, string Text, string Reason = "", string Message = "");

/// <summary>
/// 语音输入：录音 → 送服务端转写 → 把文字给界面。
///
/// 一次只允许一个录音会话，界面上也只有一个麦克风按钮，不做并发。
/// </summary>
public sealed class SpeechService : IDisposable
{
    private readonly FlyknitServerClient _server;
    private readonly object _gate = new();

    private WaveInRecorder? _recorder;
    private CancellationTokenSource? _cts;
    private System.Threading.Timer? _ticker;

    /// <summary>录音中每 ~100ms 一次：响度 0~1、已录时长。</summary>
    public event Action<double, TimeSpan>? Tick;

    /// <summary>录满上限自动收尾时触发，界面据此把录音条切到“识别中”。</summary>
    public event Action? AutoStopped;

    public SpeechService(FlyknitServerClient server)
    {
        _server = server;
    }

    public bool Recording { get { lock (_gate) { return _recorder is not null; } } }

    /// <summary>有没有麦克风。没有就别在界面上放按钮，免得点了才报错。</summary>
    public static bool Available() => WaveInRecorder.HasDevice();

    /// <summary>开始录音。失败时抛 AudioDeviceException，消息可以直接给用户看。</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_recorder is not null) { return; }
            var recorder = new WaveInRecorder();
            recorder.LevelChanged += OnLevel;
            recorder.Start();
            _recorder = recorder;
            _cts = new CancellationTokenSource();
        }
        // 没有声音输入时 waveIn 不回调，光靠 LevelChanged 计时器会停住，所以另起一个节拍
        _ticker = new System.Threading.Timer(_ => Beat(), null, 200, 200);
    }

    private void OnLevel(double level)
    {
        var recorder = _recorder;
        if (recorder is null) { return; }
        Tick?.Invoke(level, recorder.Elapsed);
    }

    private void Beat()
    {
        var recorder = _recorder;
        if (recorder is null) { return; }
        var elapsed = recorder.Elapsed;
        Tick?.Invoke(0, elapsed);
        if (RecordingLimits.Reached(elapsed))
        {
            AutoStopped?.Invoke();
        }
    }

    /// <summary>停止录音并转写。返回的消息已经是中文，界面直接显示即可。</summary>
    public async Task<SpeechOutcome> StopAndTranscribeAsync(string language)
    {
        WaveInRecorder? recorder;
        CancellationToken ct;
        lock (_gate)
        {
            recorder = _recorder;
            _recorder = null;
            ct = _cts?.Token ?? CancellationToken.None;
        }
        StopTicker();
        if (recorder is null) { return new SpeechOutcome(false, "", "idle"); }

        byte[] wav;
        double peak;
        try
        {
            peak = recorder.Peak;
            wav = recorder.Stop();
        }
        finally
        {
            recorder.LevelChanged -= OnLevel;
            recorder.Dispose();
        }

        var payload = wav.Length > WavWriter.HeaderLength ? wav.Length - WavWriter.HeaderLength : 0;
        if (RecordingLimits.TooShort(payload))
        {
            return new SpeechOutcome(false, "", "tooShort");
        }
        // 全程几乎没有波形：多半是麦克风被静音或被组策略禁了，而不是用户没说话
        if (LevelMeter.IsSilent(peak))
        {
            return new SpeechOutcome(false, "", "silent");
        }

        try
        {
            var result = await _server.TranscribeAsync(wav, "wav", language, ct);
            var text = (result.Text ?? "").Trim();
            return text.Length == 0
                ? new SpeechOutcome(false, "", "empty")
                : new SpeechOutcome(true, text);
        }
        catch (OperationCanceledException)
        {
            return new SpeechOutcome(false, "", "cancelled");
        }
        catch (GatewayException ex)
        {
            return new SpeechOutcome(false, "", "failed", ex.Message);
        }
        catch (Exception ex)
        {
            Log.Warn($"语音转写失败：{ex.Message}");
            return new SpeechOutcome(false, "", "failed", ex.Message);
        }
    }

    /// <summary>放弃本次录音，不发请求。已经在转写的也一并取消。</summary>
    public void Cancel()
    {
        WaveInRecorder? recorder;
        lock (_gate)
        {
            recorder = _recorder;
            _recorder = null;
            try { _cts?.Cancel(); } catch (ObjectDisposedException) { /* 已经结束了 */ }
        }
        StopTicker();
        if (recorder is null) { return; }
        recorder.LevelChanged -= OnLevel;
        try { recorder.Cancel(); } finally { recorder.Dispose(); }
    }

    private void StopTicker()
    {
        var ticker = _ticker;
        _ticker = null;
        ticker?.Dispose();
    }

    public void Dispose()
    {
        Cancel();
        _cts?.Dispose();
    }
}
