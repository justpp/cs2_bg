using System.Runtime.InteropServices;

namespace Cs2VoiceMusic.Audio;

/// <summary>
/// WASAPI 渲染端：把混音后的数据写入目标播放设备（虚拟声卡）。
///
/// 使用事件驱动 + 共享模式。共享模式下我们只是"喂数据"，
/// 由音频引擎负责最终时钟；缓冲填充策略保证不会欠载。
/// </summary>
public sealed unsafe class AudioRenderSink : IDisposable
{
    private readonly string _deviceId;
    private readonly int _sampleRate;
    private readonly int _channels;

    private IntPtr _pDevice;
    private IntPtr _pClient;
    private IntPtr _pRender;
    private IntPtr _hEvent;
    private IntPtr _mmcss;

    private Thread? _thread;
    private volatile bool _running;
    private volatile bool _disposed;

    /// <summary>线程没能按时退出，COM 对象被故意泄漏，后续不再释放。</summary>
    private volatile bool _threadAbandoned;

    private int _bufferFrames;
    private float[] _mixBuffer = Array.Empty<float>();

    public delegate void FillHandler(Span<float> destination, int frames);

    /// <summary>渲染线程每次需要填多少帧时回调，由混音器负责填充。</summary>
    public FillHandler? FillCallback { get; set; }

    /// <summary>渲染循环的心跳计数，用于界面显示与卡顿检测。</summary>
    public long RenderCallbacks { get; private set; }

    /// <summary>欠载（缓冲不足、补了静音）次数。</summary>
    public long Underruns { get; private set; }

    public string? LastError { get; private set; }

    public bool IsRunning => _running;

    public AudioRenderSink(string deviceId, int sampleRate = 48000, int channels = 2)
    {
        _deviceId = deviceId;
        _sampleRate = sampleRate;
        _channels = channels;
    }

    public void Initialize()
    {
        _pDevice = AudioDeviceService.OpenDevice(_deviceId);
        _pClient = AudioDeviceService.ActivateAudioClient(_pDevice, IntPtr.Zero);

        // 共享模式必须使用设备自己的混音格式，否则返回 AUDCLNT_E_UNSUPPORTED_FORMAT
        IntPtr pDeviceFmt = IntPtr.Zero;
        IntPtr pFmt = IntPtr.Zero;
        try
        {
            int hrMix = Com.Call(_pClient, 8, (IntPtr)(&pDeviceFmt)); // GetMixFormat
            Com.Check(hrMix, "GetMixFormat(渲染)");
            if (pDeviceFmt == IntPtr.Zero) throw new InvalidOperationException("设备混音格式为空");

            var deviceFormat = Marshal.PtrToStructure<Wasapi.WAVEFORMATEX>(pDeviceFmt);

            // 只有当设备格式恰好是 48kHz / 16bit / 立体声 PCM 时才能直接喂数据，
            // 其余情况（浮点、44.1k、单声道等）一律走 AUTOCONVERTPCM 由引擎转换。
            // 注意：WAVE_FORMAT_EXTENSIBLE 的 PCM 也走转换分支——这里只判断
            // "能否原样喂 16bit PCM"，不需要单独判别浮点。
            int flags = Wasapi.AUDCLNT_STREAMFLAGS_EVENTCALLBACK;
            bool needsConvert = deviceFormat.wFormatTag != Wasapi.WAVE_FORMAT_PCM
                                || deviceFormat.wBitsPerSample != 16
                                || deviceFormat.nChannels != 2
                                || deviceFormat.nSamplesPerSec != (uint)_sampleRate;

            if (needsConvert)
            {
                flags |= Wasapi.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM
                       | Wasapi.AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;

                var want = Wasapi.WAVEFORMATEX.Pcm16(_sampleRate, _channels);
                pFmt = Marshal.AllocHGlobal(Marshal.SizeOf<Wasapi.WAVEFORMATEX>());
                Marshal.StructureToPtr(want, pFmt, false);
            }
            else
            {
                pFmt = pDeviceFmt;
            }

            int hr = Com.Call(_pClient, 3,
                (IntPtr)Wasapi.AUDCLNT_SHAREMODE_SHARED, (IntPtr)flags,
                (IntPtr)0, (IntPtr)0, pFmt, IntPtr.Zero);
            Com.Check(hr, "Initialize(渲染)");
        }
        finally
        {
            if (pFmt != IntPtr.Zero && pFmt != pDeviceFmt) Marshal.FreeHGlobal(pFmt);
            if (pDeviceFmt != IntPtr.Zero) Marshal.FreeCoTaskMem(pDeviceFmt);
        }

        _hEvent = Wasapi.CreateEventW(IntPtr.Zero, false, false, null);
        if (_hEvent == IntPtr.Zero) throw new InvalidOperationException("CreateEvent 失败");

        int hrEvt = Com.Call(_pClient, 13, _hEvent);
        Com.Check(hrEvt, "SetEventHandle(渲染)");

        IntPtr pBufferSize = IntPtr.Zero;
        int hrSize = Com.Call(_pClient, 4, (IntPtr)(&pBufferSize));
        Com.Check(hrSize, "GetBufferSize(渲染)");
        _bufferFrames = (int)pBufferSize;
        if (_bufferFrames <= 0) _bufferFrames = _sampleRate / 10;

        Guid iid = Wasapi.IID_IAudioRenderClient;
        IntPtr pRender = IntPtr.Zero;
        int hrSvc = Com.Call(_pClient, 14, (IntPtr)(&iid), (IntPtr)(&pRender));
        Com.Check(hrSvc, "GetService(IAudioRenderClient)");
        if (pRender == IntPtr.Zero) throw new InvalidOperationException("IAudioRenderClient 为空");
        _pRender = pRender;

        _mixBuffer = new float[_bufferFrames * _channels];
    }

    public void Start()
    {
        if (_running) return;

        // 先预填一部分，避免启动瞬间欠载
        Prefill();

        int hr = Com.Call(_pClient, 10); // Start
        Com.Check(hr, "Start(渲染)");

        _running = true;
        _thread = new Thread(RenderLoop)
        {
            IsBackground = true,
            Name = "render",
            Priority = ThreadPriority.Highest,
        };
        _thread.Start();
    }

    private void Prefill()
    {
        if (FillCallback is null) return;

        int framesToFill = Math.Min(_bufferFrames / 2, _bufferFrames);
        FillAndWrite(framesToFill);
    }

    private void RenderLoop()
    {
        _mmcss = Mmcss.Register();
        try
        {
            while (_running)
            {
                uint wait = Wasapi.WaitForSingleObject(_hEvent, 200);
                if (!_running) break;
                if (wait != Wasapi.WAIT_OBJECT_0) continue;

                IntPtr pPadding = IntPtr.Zero;
                int hr = Com.Call(_pClient, 6, (IntPtr)(&pPadding)); // GetCurrentPadding
                if (hr < 0) continue;

                int padding = (int)pPadding;
                int available = _bufferFrames - padding;
                if (available <= 0) continue;

                FillAndWrite(available);
                RenderCallbacks++;
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
        finally
        {
            // 线程因异常退出时也要把状态置为"已停止"，
            // 否则界面会一直显示运行中，而实际上早就没有声音了。
            _running = false;
            Mmcss.Unregister(_mmcss);
            _mmcss = IntPtr.Zero;
        }
    }

    private void FillAndWrite(int frames)
    {
        if (frames <= 0) return;
        if (frames * _channels > _mixBuffer.Length)
            _mixBuffer = new float[frames * _channels];

        var span = _mixBuffer.AsSpan(0, frames * _channels);

        if (FillCallback is not null)
        {
            FillCallback(span, frames);
        }
        else
        {
            span.Clear();
            Underruns++;
        }

        WriteToDevice(span, frames);
    }

    private unsafe void WriteToDevice(Span<float> samples, int frames)
    {
        IntPtr pData = IntPtr.Zero;
        // IAudioRenderClient: 3 GetBuffer, 4 ReleaseBuffer
        int hr = Com.Call(_pRender, 3, (IntPtr)frames, (IntPtr)(&pData));
        if (hr < 0 || pData == IntPtr.Zero) return;

        try
        {
            short* pDst = (short*)pData;
            for (int i = 0; i < frames * _channels; i++)
            {
                float v = samples[i];
                // 软限幅，避免混音溢出造成刺耳爆音
                if (v > 1f) v = 1f;
                else if (v < -1f) v = -1f;
                pDst[i] = (short)(v * 32767f);
            }
        }
        finally
        {
            Com.Call(_pRender, 4, (IntPtr)frames, (IntPtr)0);
        }
    }

    /// <summary>
    /// 停止工作线程。返回线程是否已经真正退出。
    ///
    /// 返回 false 表示线程卡在 COM 调用里没能在 2 秒内退出，
    /// 此时绝不能释放它正在使用的对象（见 Dispose）。
    /// </summary>
    public bool Stop()
    {
        if (!_running && _thread is null) return true;
        _running = false;

        bool joined = true;
        try { joined = _thread?.Join(TimeSpan.FromSeconds(2)) ?? true; } catch { }
        _thread = null;
        if (!joined) _threadAbandoned = true;

        if (_pClient != IntPtr.Zero)
        {
            try { Com.Call(_pClient, 11); } catch { }
        }

        return joined;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 线程没退出就宁可泄漏也不释放：否则它下一次 COM 调用就是 use-after-free
        Stop();
        if (_threadAbandoned) return;

        if (_hEvent != IntPtr.Zero)
        {
            Wasapi.CloseHandle(_hEvent);
            _hEvent = IntPtr.Zero;
        }

        Com.Release(_pRender);
        Com.Release(_pClient);
        Com.Release(_pDevice);
        _pRender = _pClient = _pDevice = IntPtr.Zero;
    }
}
