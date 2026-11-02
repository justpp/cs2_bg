using System.Diagnostics;

namespace Cs2VoiceMusic.Audio;

/// <summary>
/// 混音引擎：把麦克风与音乐两路信号混成一路，输出到虚拟声卡。
///
/// 三个关键设计：
///
/// 1) 时钟域统一。麦克风、QQ 音乐、虚拟声卡渲染三者跑在各自独立的硬件时钟上，
///    长时间运行必然漂移。这里以渲染线程为唯一时基：每帧都从两个环形缓冲按需取数，
///    缓冲水位过高就丢帧、过低就补静音，把延迟钉在 ~40ms 的可控范围内。
///
/// 2) 音乐注入开关与麦克风独立。关闭音乐注入时麦克风照常通过，
///    队友只会听到你说话，切换时不会有"咔"的爆音（用 5ms 斜坡淡化）。
///
/// 3) 混音后统一软限幅，避免音乐 + 说话同时出现时削波。
///
/// 线程模型（重要）：
///   · _sync 只保护"控制面"——Start / Stop / 换源等字段变更。
///   · 渲染线程（OnFill）**绝不**拿 _sync。因为 Stop() 会 Join 渲染线程，
///     一旦渲染线程在等这把锁，就会与控制线程互等到 Join 超时，
///     随后控制线程释放渲染线程正在使用的 COM 对象 → use-after-free。
///   · 释放（Dispose）一律放在锁外做，且必须先停线程再放 COM。
/// </summary>
public sealed class AudioMixerEngine : IDisposable
{
    private readonly object _sync = new();
    private readonly int _sampleRate;
    private readonly int _channels;

    // 渲染线程会无锁读取这几个引用，因此必须是 volatile：
    // 控制线程换源/停止时写入，渲染线程要么看到旧值、要么看到 null，不会撕裂。
    private volatile AudioCaptureSource? _mic;
    private volatile AudioCaptureSource? _music;       // 进程级回环（QQ 音乐）
    private volatile LocalMusicSource? _localMusic;    // 本地文件播放
    private volatile AudioRenderSink? _sink;

    // 目标缓冲水位：麦克风需要更低延迟（说话要跟手），音乐可以多缓一点
    private const int MicTargetMs = 40;
    private const int MusicTargetMs = 60;

    /// <summary>
    /// QQ 音乐那一路的环形缓冲容量（帧）。必须明显大于漂移阈值（MusicTargetMs * 3），
    /// 否则"积压就丢弃"的分支永远不可达，延迟会一路顶到缓冲上限。
    /// </summary>
    private const int MusicBufferFrames = 16384;

    private float _micGain = 1.0f;
    private float _musicGain = 0.6f;
    private volatile bool _musicInjectionEnabled = true;
    private volatile bool _micEnabled = true;

    // 淡化系数（5ms 斜坡）
    private float _musicFade;
    private float _micFade;
    private readonly float _fadeStep;

    private float[] _micScratch = Array.Empty<float>();
    private float[] _musicScratch = Array.Empty<float>();
    private float[] _junkScratch = Array.Empty<float>();

    // 统计
    private long _framesRendered;
    private long _musicFramesDropped;
    private long _musicFramesPadded;
    private long _micFramesDropped;
    private long _micFramesPadded;

    public AudioMixerEngine(int sampleRate = 48000, int channels = 2)
    {
        _sampleRate = sampleRate;
        _channels = channels;
        _fadeStep = 1f / (sampleRate * 0.005f); // 5ms
        _junkScratch = new float[4096 * channels]; // 漂移丢弃用，避免在渲染线程上分配
    }

    public int SampleRate => _sampleRate;

    /// <summary>音乐注入是否开启（队友能否听到音乐）。</summary>
    public bool MusicInjectionEnabled
    {
        get => _musicInjectionEnabled;
        set => _musicInjectionEnabled = value;
    }

    /// <summary>麦克风直通是否开启。</summary>
    public bool MicEnabled
    {
        get => _micEnabled;
        set => _micEnabled = value;
    }

    public float MicGain
    {
        get => Volatile.Read(ref _micGain);
        set => Volatile.Write(ref _micGain, Math.Clamp(value, 0f, 3f));
    }

    public float MusicGain
    {
        get => Volatile.Read(ref _musicGain);
        set => Volatile.Write(ref _musicGain, Math.Clamp(value, 0f, 3f));
    }

    /// <summary>
    /// 渲染线程是否在跑。线程异常退出时会自己把标志清掉，界面据此显示"已停止"。
    /// </summary>
    public bool IsRunning => _sink?.IsRunning == true;

    /// <summary>渲染线程是否因异常退出（用于界面提示）。</summary>
    public string? RenderError => _sink?.LastError;

    /// <summary>QQ 音乐回环是否已挂上（挂接失败时为 false，监控可据此重试）。</summary>
    public bool HasMusicSource => _music is not null;

    public float MicPeak => _mic?.PeakLevel ?? 0f;
    public float MusicPeak => _localMusic?.PeakLevel ?? _music?.PeakLevel ?? 0f;

    public long FramesRendered => Interlocked.Read(ref _framesRendered);
    public long MusicUnderruns => Interlocked.Read(ref _musicFramesPadded);
    public long MusicOverruns => Interlocked.Read(ref _musicFramesDropped);
    public long MicUnderruns => Interlocked.Read(ref _micFramesPadded);
    public long MicOverruns => Interlocked.Read(ref _micFramesDropped);

    public string? LastError { get; private set; }

    /// <summary>音乐源是否已经真正采到过数据（用来区分"没开 QQ 音乐"和"路由配错"）。</summary>
    public bool MusicHasSignal => _music?.HasSignal == true || _localMusic?.HasSignal == true;

    /// <summary>本地音乐源（未启用时为 null）。</summary>
    public LocalMusicSource? LocalMusic => _localMusic;

    /// <summary>当前音乐来源：进程回环还是本地文件。</summary>
    public bool UsingLocalMusic => _localMusic is not null;

    public bool MicHasSignal => _mic?.HasSignal == true;

    /// <summary>启动音频通路。</summary>
    public void Start(string outputDeviceId, string? micDeviceId, uint? musicProcessId,
        bool musicProcessIncludeTree = true)
    {
        // 先拆旧链路：停止渲染线程要 Join，这一步必须在锁外完成。
        StopAndDisposeAll();

        // 每次启动都从干净状态开始，避免上一次的失败信息一直挂在界面上
        LastError = null;

        lock (_sync)
        {
            AudioRenderSink? sink = null;
            AudioCaptureSource? music = null;
            AudioCaptureSource? mic = null;
            AudioCaptureSource? failedMusic = null;

            try
            {
                // 渲染端：写入虚拟声卡
                sink = new AudioRenderSink(outputDeviceId, _sampleRate, _channels);
                sink.Initialize();
                sink.FillCallback = OnFill;

                // 音乐源：进程级回环。挂接失败不应该拖垮整条链路——
                // 麦克风直通仍然可用，用户至少还能正常说话。
                if (musicProcessId is uint pid)
                {
                    try
                    {
                        music = new AudioCaptureSource("QQ音乐", _sampleRate, _channels, MusicBufferFrames);
                        music.OpenProcessLoopback(pid, musicProcessIncludeTree);
                        music.Initialize(isLoopback: true);
                    }
                    catch (Exception ex)
                    {
                        // 这里还持有 _sync，不能 Dispose（它会 Join 采集线程）：
                        // 采集线程此刻根本还没启动，直接放弃引用、在锁外释放即可。
                        LastError = $"音乐源挂接失败（PID {pid}）：{ex.Message}";
                        failedMusic = music;
                        music = null;
                    }
                }

                // 麦克风：设备回环即"监听"该设备的输出；真正的麦克风采集用 capture 流
                if (!string.IsNullOrEmpty(micDeviceId))
                {
                    mic = new AudioCaptureSource("麦克风", _sampleRate, _channels);
                    mic.OpenMicrophone(micDeviceId);
                    mic.Initialize(isLoopback: false);
                }

                // 先发布引用，再启动——渲染线程的回调要用到它们
                _sink = sink;
                _music = music;
                _mic = mic;

                // 先启动捕获源，让它有时间积累数据
                music?.Start();
                mic?.Start();

                // 等捕获端攒够一点数据再开渲染，避免开头欠载（最多等 300ms）
                var wait = Stopwatch.StartNew();
                int targetFrames = _sampleRate * MusicTargetMs / 1000;
                while (wait.ElapsedMilliseconds < 300)
                {
                    bool musicReady = music is null || music.Output.AvailableFrames >= targetFrames / 2;
                    bool micReady = mic is null || mic.Output.AvailableFrames >= targetFrames / 4;
                    if (musicReady && micReady) break;
                    Thread.Sleep(5);
                }

                // 淡化初值要在渲染线程启动前设好，否则首帧会从 0 淡入
                _musicFade = _musicInjectionEnabled ? 1f : 0f;
                _micFade = _micEnabled ? 1f : 0f;

                sink.Start();
            }
            catch (Exception ex)
            {
                LastError = ex.Message;

                // 就地回滚。渲染线程若已启动，它不会去抢 _sync（见 OnFill 注释），
                // 所以这里的 Stop/Join 能正常返回，随后才能安全释放 COM。
                _sink = null;
                _music = null;
                _mic = null;

                try { sink?.Stop(); } catch { }
                try { music?.Stop(); } catch { }
                try { mic?.Stop(); } catch { }

                sink?.Dispose();
                music?.Dispose();
                mic?.Dispose();
                throw;
            }
            finally
            {
                // 失败的音乐源在这里（仍持锁，但它没有线程）释放
                failedMusic?.Dispose();
            }
        }
    }

    /// <summary>启用本地音乐文件播放（会停用 QQ 音乐回环）。</summary>
    public void EnableLocalMusic(IEnumerable<string> files, bool loop = true, Action<string>? onFailed = null)
    {
        LocalMusicSource? old;
        lock (_sync)
        {
            if (_sink is null) return;
            old = _localMusic;
            _localMusic = null;
        }

        // Dispose 会 Join 播放线程，放在锁外
        old?.Dispose();

        var src = new LocalMusicSource(_sampleRate, _channels) { Loop = loop };
        if (onFailed is not null) src.Failed += onFailed;
        src.SetPlaylist(files);

        lock (_sync)
        {
            // 期间可能被 Stop() 拆掉了整条链路
            if (_sink is null)
            {
                src.Dispose();
                return;
            }
            _localMusic = src;
        }

        // Start 只起线程、不拿 _sync，放在锁外避免和 Stop() 抢锁
        src.Start();
    }

    public void DisableLocalMusic()
    {
        LocalMusicSource? old;
        lock (_sync)
        {
            old = _localMusic;
            _localMusic = null;
        }

        old?.Stop();
        old?.Dispose();
    }

    /// <summary>运行中切换音乐源进程（例如 QQ 音乐重启后换了 PID）。</summary>
    public void SwitchMusicSource(uint processId, bool includeTree = true)
    {
        AudioCaptureSource? old;
        lock (_sync)
        {
            if (_sink is not { IsRunning: true }) return;
            old = _music;
            _music = null;
        }

        // 旧源的 Dispose 会 Join 采集线程，同样放在锁外
        old?.Dispose();

        AudioCaptureSource? src = null;
        try
        {
            src = new AudioCaptureSource("QQ音乐", _sampleRate, _channels, MusicBufferFrames);
            src.OpenProcessLoopback(processId, includeTree);
            src.Initialize(isLoopback: true);
            src.Start();
        }
        catch (Exception ex)
        {
            // 这段在锁外，Dispose 里的 Join 是安全的
            // （采集线程可能已经起来了，必须停掉再释放 COM）
            LastError = ex.Message;
            src?.Dispose();
            return;
        }

        bool accepted;
        lock (_sync)
        {
            accepted = _sink is { IsRunning: true };
            if (accepted)
            {
                _music = src;
                LastError = null;   // 换源成功，清掉上一次的挂接失败
            }
        }

        if (!accepted) src.Dispose();
    }

    public void Stop() => StopAndDisposeAll();

    /// <summary>
    /// 拆掉整条链路：先在锁内摘掉引用，再在锁外停线程、释放 COM。
    ///
    /// 顺序很关键：必须先把引用置空（渲染线程随后只看到 null，输出静音），
    /// 再 Join 渲染线程，最后才 Release 它用过的 COM 对象。
    /// </summary>
    private void StopAndDisposeAll()
    {
        AudioRenderSink? sink;
        AudioCaptureSource? music, mic;
        LocalMusicSource? local;

        lock (_sync)
        {
            sink = _sink;
            music = _music;
            mic = _mic;
            local = _localMusic;

            _sink = null;
            _music = null;
            _mic = null;
            _localMusic = null;
        }

        if (sink is null && music is null && mic is null && local is null) return;

        sink?.Stop();
        music?.Stop();
        mic?.Stop();

        sink?.Dispose();
        music?.Dispose();
        mic?.Dispose();
        local?.Dispose();
    }

    /// <summary>渲染线程回调：为 frames 帧填充立体声数据。</summary>
    private void OnFill(Span<float> destination, int frames)
    {
        int samples = frames * _channels;

        if (_micScratch.Length < samples) _micScratch = new float[samples];
        if (_musicScratch.Length < samples) _musicScratch = new float[samples];

        // 这里刻意不加锁：控制线程的 Stop() 正在 Join 本线程，等锁就是死等。
        bool musicOn = _musicInjectionEnabled;
        bool micOn = _micEnabled;
        float musicGain = Volatile.Read(ref _musicGain);
        float micGain = Volatile.Read(ref _micGain);

        // 一次性快照源引用，避免中途被换掉后前后不一致
        var mic = _mic;
        var local = _localMusic;
        var music = _music;

        // ---- 麦克风 ----
        if (mic is not null && micOn)
        {
            var micSpan = _micScratch.AsSpan(0, samples);
            int got = mic.Output.Read(micSpan);
            HandleDrift("mic", got, frames, mic.Output);
        }
        else
        {
            _micScratch.AsSpan(0, samples).Clear();
        }

        // ---- 音乐 ----
        // 本地文件优先；没有本地播放时才用 QQ 音乐回环
        AudioRingBuffer? musicRing = local is not null ? local.Output : music?.Output;
        if (musicRing is not null && musicOn)
        {
            var musicSpan = _musicScratch.AsSpan(0, samples);
            int got = musicRing.Read(musicSpan);

            // 本地文件的生产速度由我们控制（生产端自带背压），
            // 所以不能对它执行"积压就丢弃"——那会瞬间丢光整首歌。
            // 只有硬件回环（QQ 音乐）才需要主动丢帧来压延迟。
            if (local is null)
            {
                HandleDrift("music", got, frames, musicRing);
            }
            else if (got < frames)
            {
                Interlocked.Add(ref _musicFramesPadded, frames - got);
            }
        }
        else
        {
            _musicScratch.AsSpan(0, samples).Clear();
        }

        // ---- 混音（带淡化，避免开关时爆音）----
        float targetMusicFade = (musicOn && musicRing is not null) ? 1f : 0f;
        float targetMicFade = (micOn && mic is not null) ? 1f : 0f;

        for (int i = 0; i < samples; i += _channels)
        {
            _musicFade = MoveTowards(_musicFade, targetMusicFade, _fadeStep);
            _micFade = MoveTowards(_micFade, targetMicFade, _fadeStep);

            for (int c = 0; c < _channels; c++)
            {
                float m = _musicScratch[i + c] * musicGain * _musicFade;
                float v = _micScratch[i + c] * micGain * _micFade;
                destination[i + c] = m + v;
            }
        }

        Interlocked.Add(ref _framesRendered, frames);
    }

    /// <summary>
    /// 漂移处理：以渲染端为时基。
    /// 缓冲积压过多 → 丢弃多余帧（压低延迟）；不足 → 已由 Read 补静音。
    /// </summary>
    private void HandleDrift(string tag, int got, int frames, AudioRingBuffer ring)
    {
        if (got < frames)
        {
            if (tag == "mic") Interlocked.Add(ref _micFramesPadded, frames - got);
            else Interlocked.Add(ref _musicFramesPadded, frames - got);
        }

        int targetMs = tag == "mic" ? MicTargetMs : MusicTargetMs;
        int targetFrames = _sampleRate * targetMs / 1000;
        int available = ring.AvailableFrames;

        // 阈值必须落在缓冲容量之内，否则这条补偿永远不会触发：
        // 音乐路曾用 60ms*3 = 8640 帧，而缓冲只有 8192 帧，分支不可达，
        // 延迟一路顶到缓冲上限，且 MusicOverruns 恒为 0。
        int threshold = Math.Min(targetFrames * 3, ring.CapacityFrames * 3 / 4);
        if (available <= threshold) return;

        int drop = available - targetFrames;
        int chunkFrames = Math.Max(1, Math.Min(drop, 4096));
        int needed = chunkFrames * _channels;
        if (_junkScratch.Length < needed) _junkScratch = new float[needed];

        int dropped = 0;
        while (dropped < drop)
        {
            int chunk = Math.Min(drop - dropped, _junkScratch.Length / _channels);
            if (chunk <= 0) break;
            int n = ring.Read(_junkScratch.AsSpan(0, chunk * _channels), padSilence: false);
            if (n <= 0) break;
            dropped += n;
        }

        if (tag == "mic") Interlocked.Add(ref _micFramesDropped, dropped);
        else Interlocked.Add(ref _musicFramesDropped, dropped);
    }

    private static float MoveTowards(float current, float target, float step)
    {
        if (Math.Abs(target - current) <= step) return target;
        return current + Math.Sign(target - current) * step;
    }

    public void Dispose()
    {
        Stop();
    }
}
