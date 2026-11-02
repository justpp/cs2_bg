using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Cs2VoiceMusic.Audio;

/// <summary>
/// 一路 WASAPI 采集源：可以是"进程级回环"（抓 QQ 音乐），
/// 也可以是普通麦克风采集。统一输出 48kHz / 16bit / 立体声，
/// 由 AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM 让音频引擎完成重采样与混音。
/// </summary>
public sealed unsafe class AudioCaptureSource : IDisposable
{
    private readonly string _name;
    private readonly int _sampleRate;
    private readonly int _channels;

    private IntPtr _pDevice;
    private IntPtr _pClient;
    private IntPtr _pCapture;
    private IntPtr _hEvent;
    private IntPtr _mmcss;

    private Thread? _thread;
    private volatile bool _running;
    private volatile bool _disposed;

    /// <summary>线程没能按时退出，COM 对象被故意泄漏，后续不再释放。</summary>
    private volatile bool _threadAbandoned;

    private byte[] _readBuffer = Array.Empty<byte>();
    private float[] _scratch = Array.Empty<float>();

    /// <summary>采集到的音频写入这里（交错立体声 float）。</summary>
    public AudioRingBuffer Output { get; }

    /// <summary>最近一次采样的峰值电平（0..1），用于界面电平表。</summary>
    public float PeakLevel { get; private set; }

    /// <summary>累计采集到的帧数，用于诊断。</summary>
    public long TotalFrames { get; private set; }

    /// <summary>采集启动后是否真的收到过数据。</summary>
    public bool HasSignal { get; private set; }

    public string Name => _name;
    public bool IsRunning => _running;

    /// <summary>最近一次错误，用于向用户展示。</summary>
    public string? LastError { get; private set; }

    public AudioCaptureSource(string name, int sampleRate = 48000, int channels = 2,
        int bufferFrames = 8192)
    {
        _name = name;
        _sampleRate = sampleRate;
        _channels = channels;
        Output = new AudioRingBuffer(bufferFrames);
    }

    /// <summary>
    /// 打开指定设备上的采集客户端。
    /// 麦克风采集与回环采集在 WASAPI 层共用同一套激活流程，
    /// 区别只在 Initialize 时是否带 AUDCLNT_STREAMFLAGS_LOOPBACK：
    ///   · 带 LOOPBACK → 捕获该"播放设备"正在播放的声音
    ///   · 不带 LOOPBACK → 从该"录音设备"读取麦克风
    /// </summary>
    public void OpenDevice(string deviceId)
    {
        _pDevice = AudioDeviceService.OpenDevice(deviceId);
        try
        {
            _pClient = AudioDeviceService.ActivateAudioClient(_pDevice, IntPtr.Zero);
        }
        catch
        {
            Com.Release(_pDevice);
            _pDevice = IntPtr.Zero;
            throw;
        }
    }

    /// <summary>打开指定播放设备的回环采集（捕获该设备上播放的声音）。</summary>
    public void OpenDeviceLoopback(string deviceId) => OpenDevice(deviceId);

    /// <summary>打开指定录音设备的麦克风采集。</summary>
    public void OpenMicrophone(string deviceId) => OpenDevice(deviceId);

    /// <summary>打开进程级回环：只捕获目标进程渲染的声音。</summary>
    public void OpenProcessLoopback(uint processId, bool includeProcessTree = true)
    {
        _pClient = AudioDeviceService.ActivateProcessLoopback(processId, includeProcessTree);
    }

    /// <summary>按当前已激活的客户端初始化音频流。</summary>
    public void Initialize(bool isLoopback)
    {
        if (_pClient == IntPtr.Zero)
            throw new InvalidOperationException("尚未激活 IAudioClient");

        int flags = Wasapi.AUDCLNT_STREAMFLAGS_EVENTCALLBACK
                  | Wasapi.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM
                  | Wasapi.AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;

        if (isLoopback)
            flags |= Wasapi.AUDCLNT_STREAMFLAGS_LOOPBACK;

        var fmt = Wasapi.WAVEFORMATEX.Pcm16(_sampleRate, _channels);
        IntPtr pFmt = Marshal.AllocHGlobal(Marshal.SizeOf<Wasapi.WAVEFORMATEX>());
        try
        {
            Marshal.StructureToPtr(fmt, pFmt, false);
            // IAudioClient: 3 = Initialize
            int hr = Com.Call(_pClient, 3,
                (IntPtr)Wasapi.AUDCLNT_SHAREMODE_SHARED, (IntPtr)flags,
                (IntPtr)0, (IntPtr)0, pFmt, IntPtr.Zero);
            Com.Check(hr, $"Initialize({_name})");
        }
        finally
        {
            Marshal.FreeHGlobal(pFmt);
        }

        _hEvent = Wasapi.CreateEventW(IntPtr.Zero, false, false, null);
        if (_hEvent == IntPtr.Zero) throw new InvalidOperationException("CreateEvent 失败");

        int hrEvent = Com.Call(_pClient, 13, _hEvent); // SetEventHandle
        Com.Check(hrEvent, $"SetEventHandle({_name})");

        IntPtr pBufferSize = IntPtr.Zero;
        int hrSize = Com.Call(_pClient, 4, (IntPtr)(&pBufferSize)); // GetBufferSize
        Com.Check(hrSize, $"GetBufferSize({_name})");

        Guid iid = Wasapi.IID_IAudioCaptureClient;
        IntPtr pCap = IntPtr.Zero;
        int hrSvc = Com.Call(_pClient, 14, (IntPtr)(&iid), (IntPtr)(&pCap)); // GetService
        Com.Check(hrSvc, $"GetService(IAudioCaptureClient) {_name}");
        if (pCap == IntPtr.Zero) throw new InvalidOperationException("IAudioCaptureClient 为空");
        _pCapture = pCap;

        // 每个采样帧 4 字节（16bit 立体声）；预留 1 秒余量
        _readBuffer = new byte[_sampleRate * 4];
        _scratch = new float[_sampleRate * 2];
    }

    public void Start()
    {
        if (_running) return;
        if (_pClient == IntPtr.Zero || _pCapture == IntPtr.Zero)
            throw new InvalidOperationException("音频源未初始化");

        int hr = Com.Call(_pClient, 10); // Start
        Com.Check(hr, $"Start({_name})");

        _running = true;
        _thread = new Thread(CaptureLoop)
        {
            IsBackground = true,
            Name = $"capture:{_name}",
            Priority = ThreadPriority.Highest,
        };
        _thread.Start();
    }

    /// <summary>
    /// 停止采集线程。返回线程是否已经真正退出。
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
            try { Com.Call(_pClient, 11); } catch { } // Stop
        }

        return joined;
    }

    private void CaptureLoop()
    {
        _mmcss = Mmcss.Register();
        try
        {
            while (_running)
            {
                uint wait = Wasapi.WaitForSingleObject(_hEvent, 200);
                if (wait != Wasapi.WAIT_OBJECT_0) continue;
                if (!_running) break;

                DrainPackets();
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Debug.WriteLine($"[{_name}] 采集线程异常: {ex}");
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

    private unsafe void DrainPackets()
    {
        while (_running)
        {
            IntPtr pNext = IntPtr.Zero;
            int hr = Com.Call(_pCapture, 5, (IntPtr)(&pNext)); // GetNextPacketSize
            if (hr < 0) return;
            uint packetFrames = (uint)pNext;
            if (packetFrames == 0) return;

            IntPtr pData = IntPtr.Zero, pFrames = IntPtr.Zero, pFlags = IntPtr.Zero;
            IntPtr pDevPos = IntPtr.Zero, pQpc = IntPtr.Zero;
            hr = Com.Call(_pCapture, 3,
                (IntPtr)(&pData), (IntPtr)(&pFrames), (IntPtr)(&pFlags),
                (IntPtr)(&pDevPos), (IntPtr)(&pQpc));
            if (hr < 0) return;

            uint frames = (uint)pFrames;
            uint flags = (uint)pFlags;

            try
            {
                if (frames == 0) continue;

                int sampleCount = (int)frames * _channels;
                if (_scratch.Length < sampleCount) _scratch = new float[sampleCount];

                if ((flags & Wasapi.AUDCLNT_BUFFERFLAGS_SILENT) != 0 || pData == IntPtr.Zero)
                {
                    Array.Clear(_scratch, 0, sampleCount);
                }
                else
                {
                    int bytes = (int)frames * _channels * sizeof(short);
                    if (_readBuffer.Length < bytes) _readBuffer = new byte[bytes];

                    fixed (byte* pDst = _readBuffer)
                    {
                        Buffer.MemoryCopy((void*)pData, pDst, _readBuffer.Length, bytes);
                    }

                    // 16bit PCM → float
                    var src = MemoryMarshal.Cast<byte, short>(_readBuffer.AsSpan(0, bytes));
                    for (int i = 0; i < sampleCount; i++)
                    {
                        _scratch[i] = src[i] / 32768f;
                    }
                }

                UpdatePeak(_scratch, sampleCount);
                Output.Write(_scratch.AsSpan(0, sampleCount));
                TotalFrames += frames;
                if (frames > 0) HasSignal = true;
            }
            finally
            {
                Com.Call(_pCapture, 4, (IntPtr)frames); // ReleaseBuffer
            }
        }
    }

    private void UpdatePeak(float[] buffer, int count)
    {
        float peak = 0f;
        for (int i = 0; i < count; i++)
        {
            float v = Math.Abs(buffer[i]);
            if (v > peak) peak = v;
        }
        // 轻微衰减，避免电平表抖动过于剧烈
        PeakLevel = Math.Max(peak, PeakLevel * 0.75f);
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

        Com.Release(_pCapture);
        Com.Release(_pClient);
        Com.Release(_pDevice);
        _pCapture = _pClient = _pDevice = IntPtr.Zero;
    }
}
