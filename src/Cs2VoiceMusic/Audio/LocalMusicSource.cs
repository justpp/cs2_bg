using NAudio.Wave;

namespace Cs2VoiceMusic.Audio;

/// <summary>
/// 本地音乐文件播放源：用 NAudio 解码 mp3 / wav / flac / m4a 等，
/// 重采样到混音引擎的格式后写入环形缓冲。
///
/// 这条路径的意义：当 QQ 音乐没开、或进程级回环被系统策略挡住时，
/// 仍然可以把歌送进语音。它也完全不依赖外部播放器，行为最可预测。
///
/// 线程模型：解码/播放跑在自己的后台线程；Start / Stop / Next / Previous
/// 只改标志位与索引，不碰解码器，避免跨线程操作 NAudio 对象。
/// </summary>
public sealed class LocalMusicSource : IDisposable
{
    private readonly int _sampleRate;
    private readonly int _channels;

    private Thread? _thread;
    private volatile bool _running;
    private volatile bool _paused;
    private volatile bool _disposed;
    private volatile bool _restartRequested;

    private string? _currentFile;
    private readonly List<string> _playlist = new();
    private volatile int _playlistIndex = -1;

    /// <summary>缓冲目标水位：生产端只补到这么多，延迟就不会越堆越高。</summary>
    private const int TargetBufferMs = 60;

    /// <summary>每次从解码器读一小段（20ms），配合水位控制把延迟钉住。</summary>
    private const int ChunkMs = 20;

    public AudioRingBuffer Output { get; }

    public string? CurrentFile => _currentFile;
    public bool IsRunning => _running;
    public bool IsPaused => _paused;

    /// <summary>放完最后一首后是否回到第一首（槽位配置里的 Loop）。</summary>
    public bool Loop { get; set; } = true;

    public float PeakLevel { get; private set; }
    public bool HasSignal { get; private set; }

    /// <summary>最近一次错误（解码失败、文件损坏等）。</summary>
    public string? LastError { get; private set; }

    /// <summary>当前播放进度（秒）。</summary>
    public double PositionSeconds { get; private set; }

    /// <summary>当前曲目总长（秒）。</summary>
    public double DurationSeconds { get; private set; }

    /// <summary>一曲播完时触发。</summary>
    public event Action? TrackEnded;

    /// <summary>播放过程中出错（例如文件损坏）。播放会就此停止，由上层提示用户。</summary>
    public event Action<string>? Failed;

    public LocalMusicSource(int sampleRate = 48000, int channels = 2, int bufferFrames = 16384)
    {
        _sampleRate = sampleRate;
        _channels = channels;
        Output = new AudioRingBuffer(bufferFrames);
    }

    /// <summary>设置播放列表并从第一首开始。</summary>
    public void SetPlaylist(IEnumerable<string> files)
    {
        _playlist.Clear();
        _playlist.AddRange(files.Where(File.Exists));
        _playlistIndex = _playlist.Count > 0 ? 0 : -1;
    }

    public IReadOnlyList<string> Playlist => _playlist;

    public void Play(string file)
    {
        Stop();
        _playlist.Clear();
        _playlist.Add(file);
        _playlistIndex = 0;
        Start();
    }

    public void Start()
    {
        // 可能在"挂上 _localMusic"与"这里 Start"之间被 Stop() 拆掉并 Dispose
        if (_disposed) return;
        if (_running || _playlistIndex < 0 || _playlistIndex >= _playlist.Count) return;

        LastError = null;
        _running = true;
        _paused = false;
        _restartRequested = false;
        _thread = new Thread(PlaybackLoop)
        {
            IsBackground = true,
            Name = "local-music",
        };
        _thread.Start();
    }

    public void Pause()
    {
        _paused = true;
    }

    public void Resume()
    {
        _paused = false;
    }

    public void TogglePause()
    {
        if (_paused) Resume();
        else Pause();
    }

    public void Next()
    {
        if (_playlist.Count == 0) return;
        _playlistIndex = (_playlistIndex + 1) % _playlist.Count;
        _restartRequested = true;
    }

    public void Previous()
    {
        if (_playlist.Count == 0) return;
        _playlistIndex = (_playlistIndex - 1 + _playlist.Count) % _playlist.Count;
        _restartRequested = true;
    }

    private void PlaybackLoop()
    {
        try
        {
            while (_running && !_disposed)
            {
                if (_playlistIndex < 0 || _playlistIndex >= _playlist.Count) break;

                string file = _playlist[_playlistIndex];
                _currentFile = file;

                bool trackCompleted = false;
                bool restartRequested = false;

                try
                {
                    using var reader = new AudioFileReader(file);
                    using var resampler = new MediaFoundationResampler(
                        reader, new WaveFormat(_sampleRate, 16, _channels))
                    {
                        ResamplerQuality = 60,
                    };

                    DurationSeconds = reader.TotalTime.TotalSeconds;
                    PositionSeconds = 0;

                    int chunkFrames = Math.Max(1, _sampleRate * ChunkMs / 1000);
                    int targetFrames = Math.Max(chunkFrames, _sampleRate * TargetBufferMs / 1000);
                    var buffer = new byte[chunkFrames * _channels * 2];
                    var floats = new float[chunkFrames * _channels];

                    while (_running)
                    {
                        if (_restartRequested)
                        {
                            _restartRequested = false;
                            restartRequested = true;
                            break;
                        }

                        if (_paused)
                        {
                            Thread.Sleep(20);
                            continue;
                        }

                        // 水位控制：只在低于目标水位时补数据。
                        // 早先写成"缓冲有空位就写满"，等于把延迟一路顶到缓冲上限（~240ms）；
                        // 而且判空用的 FreeFrames（帧）与 buffer.Length（字节）单位不一致。
                        int available = Output.AvailableFrames;
                        if (available >= targetFrames)
                        {
                            Thread.Sleep(5);
                            continue;
                        }

                        int wantFrames = Math.Min(chunkFrames, targetFrames - available);
                        int read = resampler.Read(buffer, 0, wantFrames * _channels * 2);
                        if (read <= 0)
                        {
                            trackCompleted = true; // 本曲结束
                            break;
                        }

                        int samples = read / 2;
                        for (int i = 0; i < samples; i++)
                        {
                            floats[i] = BitConverter.ToInt16(buffer, i * 2) / 32768f;
                        }

                        Output.Write(floats.AsSpan(0, samples));

                        float peak = 0;
                        for (int i = 0; i < samples; i++)
                        {
                            float v = Math.Abs(floats[i]);
                            if (v > peak) peak = v;
                        }
                        PeakLevel = Math.Max(peak, PeakLevel * 0.75f);
                        if (peak > 0.0005f) HasSignal = true;

                        PositionSeconds = reader.CurrentTime.TotalSeconds;
                    }
                }
                catch (Exception ex)
                {
                    // 文件损坏 / 解码器缺失：明确报错并停下，不要"看起来在放其实没声"
                    string error = $"播放「{Path.GetFileName(file)}」失败：{ex.Message}";
                    LastError = error;
                    Failed?.Invoke(error);
                    break;
                }

                if (!_running) break;

                // 手动切歌：索引已经由 Next/Previous 改好，直接进下一轮。
                // 早先这里又把索引自增一次，导致按一次"下一首"跳两首。
                if (restartRequested) continue;

                if (!trackCompleted) break;

                TrackEnded?.Invoke();

                int next = _playlistIndex + 1;
                if (next >= _playlist.Count)
                {
                    if (!Loop)
                    {
                        _playlistIndex = _playlist.Count - 1;
                        break;
                    }
                    next = 0;
                }
                _playlistIndex = next;
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Failed?.Invoke(ex.Message);
        }
        finally
        {
            _running = false;
        }
    }

    public void Stop()
    {
        // 注意不能因为 _running 已为 false 就提前返回：
        // 播完最后一首（Loop=false）时线程会自己退出，此时 _thread 仍需回收，
        // 缓冲里残留的尾巴也必须清掉，否则下次播放会先冒出上一首的几帧。
        _running = false;
        try { _thread?.Join(TimeSpan.FromSeconds(2)); } catch { }
        _thread = null;
        Output.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
