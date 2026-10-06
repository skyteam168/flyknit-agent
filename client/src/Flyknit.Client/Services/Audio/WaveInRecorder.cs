using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Flyknit.Core.Speech;

namespace Flyknit.Client.Services.Audio;

/// <summary>
/// 麦克风录音，直接用 winmm.dll 的 waveIn 接口。
///
/// 没有引第三方音频库是故意的：录音这点需求用不着，少一个依赖就少一处版本冲突，
/// 安装包也不用多带几 MB。waveIn 从 Win2000 一路支持到 Win11，够稳。
/// </summary>
public sealed class WaveInRecorder : IDisposable
{
    private const int BufferCount = 4;
    private const int BufferMillis = 100;

    private const int MMSYSERR_NOERROR = 0;
    private const int WIM_DATA = 0x3C0;
    private const int CALLBACK_FUNCTION = 0x00030000;
    private const int WAVE_MAPPER = -1;
    private const int WHDR_DONE = 0x00000001;

    private readonly object _gate = new();
    private readonly List<byte[]> _chunks = new();
    private readonly List<IntPtr> _headers = new();
    private readonly WaveDelegate _callback; // 必须保持引用，否则会被 GC 掉导致回调时崩溃

    private IntPtr _handle;
    private bool _running;
    private double _peak;
    private long _bytes;

    /// <summary>每收到一小段音频触发一次，参数是 0~1 的响度，用来画动效。</summary>
    public event Action<double>? LevelChanged;

    public long ByteCount => Interlocked.Read(ref _bytes);
    public double Peak { get { lock (_gate) { return _peak; } } }
    public TimeSpan Elapsed => RecordingLimits.DurationOf(ByteCount);

    public void Start()
    {
        if (_running) { return; }

        var format = new WAVEFORMATEX
        {
            wFormatTag = 1, // PCM
            nChannels = (short)RecordingLimits.Channels,
            nSamplesPerSec = RecordingLimits.SampleRate,
            wBitsPerSample = (short)RecordingLimits.BitsPerSample,
        };
        format.nBlockAlign = (short)(format.nChannels * format.wBitsPerSample / 8);
        format.nAvgBytesPerSec = format.nSamplesPerSec * format.nBlockAlign;
        format.cbSize = 0;

        var result = waveInOpen(out _handle, WAVE_MAPPER, ref format, _callback, IntPtr.Zero, CALLBACK_FUNCTION);
        if (result != MMSYSERR_NOERROR)
        {
            throw new AudioDeviceException(Describe(result));
        }

        lock (_gate)
        {
            _chunks.Clear();
            _peak = 0;
        }
        Interlocked.Exchange(ref _bytes, 0);

        var size = RecordingLimits.BytesPerSecond * BufferMillis / 1000;
        for (var i = 0; i < BufferCount; i++)
        {
            AddBuffer(size);
        }

        result = waveInStart(_handle);
        if (result != MMSYSERR_NOERROR)
        {
            Cleanup();
            throw new AudioDeviceException(Describe(result));
        }
        _running = true;
    }

    /// <summary>停止录音，返回打好包的 WAV。没录到东西时返回空数组。</summary>
    public byte[] Stop()
    {
        if (!_running) { return Array.Empty<byte>(); }
        _running = false;
        waveInStop(_handle);
        waveInReset(_handle);
        Cleanup();

        lock (_gate)
        {
            return _chunks.Count == 0 ? Array.Empty<byte>() : WavWriter.Wrap(_chunks);
        }
    }

    /// <summary>丢弃本次录音，不做任何打包。</summary>
    public void Cancel()
    {
        if (!_running) { return; }
        _running = false;
        waveInStop(_handle);
        waveInReset(_handle);
        Cleanup();
        lock (_gate) { _chunks.Clear(); }
    }

    public WaveInRecorder()
    {
        _callback = OnWaveMessage;
    }

    private void AddBuffer(int size)
    {
        var header = new WAVEHDR
        {
            lpData = Marshal.AllocHGlobal(size),
            dwBufferLength = size,
        };
        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEHDR>());
        Marshal.StructureToPtr(header, ptr, false);
        waveInPrepareHeader(_handle, ptr, Marshal.SizeOf<WAVEHDR>());
        waveInAddBuffer(_handle, ptr, Marshal.SizeOf<WAVEHDR>());
        lock (_gate) { _headers.Add(ptr); }
    }

    private void OnWaveMessage(IntPtr handle, int message, IntPtr instance, IntPtr param1, IntPtr param2)
    {
        if (message != WIM_DATA || !_running) { return; }

        var header = Marshal.PtrToStructure<WAVEHDR>(param1);
        if ((header.dwFlags & WHDR_DONE) == 0 || header.dwBytesRecorded <= 0) { return; }

        var data = new byte[header.dwBytesRecorded];
        Marshal.Copy(header.lpData, data, 0, header.dwBytesRecorded);

        var level = LevelMeter.Measure(data);
        lock (_gate)
        {
            _chunks.Add(data);
            if (level > _peak) { _peak = level; }
        }
        Interlocked.Add(ref _bytes, data.Length);

        // 回调跑在系统的音频线程上，这里不做任何重活，界面那边自己切回 UI 线程
        try { LevelChanged?.Invoke(level); } catch (Exception) { /* 订阅方出错不能拖垮录音 */ }

        if (_running)
        {
            waveInAddBuffer(handle, param1, Marshal.SizeOf<WAVEHDR>());
        }
    }

    private void Cleanup()
    {
        lock (_gate)
        {
            foreach (var ptr in _headers)
            {
                try
                {
                    waveInUnprepareHeader(_handle, ptr, Marshal.SizeOf<WAVEHDR>());
                    var header = Marshal.PtrToStructure<WAVEHDR>(ptr);
                    if (header.lpData != IntPtr.Zero) { Marshal.FreeHGlobal(header.lpData); }
                }
                catch (Exception) { /* 释放失败只会漏一小块内存，不值得把异常抛给用户 */ }
                finally { Marshal.FreeHGlobal(ptr); }
            }
            _headers.Clear();
        }
        if (_handle != IntPtr.Zero)
        {
            waveInClose(_handle);
            _handle = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        try { Cancel(); } catch (Exception) { /* 退出时尽力而为 */ }
    }

    /// <summary>有没有可用的录音设备。没有麦克风时界面上的按钮直接置灰。</summary>
    public static bool HasDevice()
    {
        try { return waveInGetNumDevs() > 0; }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    private static string Describe(int code) => code switch
    {
        4 => "麦克风被其他程序占用了，关掉会议或录音软件再试",
        2 => "没有找到可用的麦克风",
        32 => "麦克风不支持 16kHz 单声道录音",
        _ => $"打不开麦克风（错误码 {code}）",
    };

    private delegate void WaveDelegate(IntPtr hwi, int uMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WAVEFORMATEX
    {
        public short wFormatTag;
        public short nChannels;
        public int nSamplesPerSec;
        public int nAvgBytesPerSec;
        public short nBlockAlign;
        public short wBitsPerSample;
        public short cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEHDR
    {
        public IntPtr lpData;
        public int dwBufferLength;
        public int dwBytesRecorded;
        public IntPtr dwUser;
        public int dwFlags;
        public int dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    [DllImport("winmm.dll")] private static extern int waveInGetNumDevs();
    [DllImport("winmm.dll")] private static extern int waveInOpen(out IntPtr phwi, int uDeviceID,
        ref WAVEFORMATEX pwfx, WaveDelegate dwCallback, IntPtr dwInstance, int dwFlags);
    [DllImport("winmm.dll")] private static extern int waveInStart(IntPtr hwi);
    [DllImport("winmm.dll")] private static extern int waveInStop(IntPtr hwi);
    [DllImport("winmm.dll")] private static extern int waveInReset(IntPtr hwi);
    [DllImport("winmm.dll")] private static extern int waveInClose(IntPtr hwi);
    [DllImport("winmm.dll")] private static extern int waveInPrepareHeader(IntPtr hwi, IntPtr pwh, int cbwh);
    [DllImport("winmm.dll")] private static extern int waveInUnprepareHeader(IntPtr hwi, IntPtr pwh, int cbwh);
    [DllImport("winmm.dll")] private static extern int waveInAddBuffer(IntPtr hwi, IntPtr pwh, int cbwh);
}

public sealed class AudioDeviceException : Exception
{
    public AudioDeviceException(string message) : base(message) { }
}
