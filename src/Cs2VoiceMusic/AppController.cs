using System.Diagnostics;
using System.Media;
using Cs2VoiceMusic.Audio;
using Cs2VoiceMusic.Config;
using Cs2VoiceMusic.Hotkeys;
using Cs2VoiceMusic.Media;

namespace Cs2VoiceMusic;

/// <summary>
/// 应用核心：把配置、音频引擎、热键、媒体控制串起来。
/// 界面层只与本类交互。
/// </summary>
public sealed class AppController : IDisposable
{
    private readonly AppConfig _config;
    private readonly AudioMixerEngine _engine;
    private readonly SystemMediaController _media;
    private HotkeyManager? _hotkeys;

    private uint? _musicPid;
    private readonly System.Threading.Timer _monitorTimer;

    public event Action? StateChanged;
    public event Action<string>? StatusMessage;

    public AppConfig Config => _config;
    public AudioMixerEngine Engine => _engine;
    public SystemMediaController Media => _media;
    public HotkeyManager? Hotkeys => _hotkeys;

    /// <summary>音乐注入开关（队友能否听到音乐）。</summary>
    public bool MusicInjectionEnabled => _engine.MusicInjectionEnabled;

    public bool IsRunning => _engine.IsRunning;

    public string StatusText { get; private set; } = "未启动";

    public AppController()
    {
        bool existed = File.Exists(Path.Combine(AppConfig.DefaultDirectory, "config.json"));
        _config = AppConfig.Load();
        _engine = new AudioMixerEngine();
        _media = new SystemMediaController();

        _engine.MusicGain = _config.MusicGain;
        _engine.MicGain = _config.MicGain;
        _engine.MusicInjectionEnabled = _config.MusicInjectionDefault;

        // 首次运行就把默认配置落盘，这样槽位/热键的改动有明确的保存目标，
        // 也避免用户在"配置还不存在"的状态下误以为设置已生效。
        if (!existed)
        {
            try { _config.Save(); } catch { /* 保存失败不阻塞启动 */ }
        }

        // 每 2 秒检查一次 QQ 音乐进程与设备状态
        _monitorTimer = new System.Threading.Timer(_ => MonitorTick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void AttachHotkeys(IntPtr hwnd)
    {
        _hotkeys = new HotkeyManager(hwnd);
        _hotkeys.HotkeyPressed += OnHotkey;
        ReloadHotkeys();
    }

    public List<HotkeyBinding> ReloadHotkeys()
    {
        if (_hotkeys is null) return new List<HotkeyBinding>();
        var failed = _hotkeys.RegisterAll(_config.Hotkeys);
        return failed;
    }

    public void StartMonitoring() => _monitorTimer.Change(2000, 2000);

    public void StopMonitoring() => _monitorTimer.Change(Timeout.Infinite, Timeout.Infinite);

    /// <summary>启动音频通路。返回是否成功。</summary>
    public bool Start()
    {
        try
        {
            // 1) 找 QQ 音乐进程
            _musicPid = ResolveMusicProcess();

            // 2) 确定输出设备：配置的虚拟声卡，未配置则用默认播放设备
            string outputId = _config.OutputDeviceId;
            if (string.IsNullOrEmpty(outputId))
            {
                var def = AudioDeviceService.GetDefault(DeviceKind.Playback);
                if (def is null)
                {
                    Fail("找不到可用的播放设备");
                    return false;
                }
                outputId = def.Id;
            }

            string? micId = string.IsNullOrEmpty(_config.MicDeviceId) ? null : _config.MicDeviceId;

            _engine.Start(outputId, micId, _musicPid);
            _engine.MusicInjectionEnabled = _config.MusicInjectionDefault;
            _engine.MusicGain = _config.MusicGain;
            _engine.MicGain = _config.MicGain;

            // 明确告知用户当前的接线状况，避免"以为在放歌其实没接通"
            if (_musicPid is null)
            {
                StatusMessage?.Invoke("未找到 QQ 音乐进程，只有麦克风会进入语音");
                StatusText = "运行中（未找到 QQ 音乐）";
            }
            else if (_engine.LastError is not null)
            {
                // 找到了进程但挂接失败：麦克风照常工作，但音乐这条路没通，必须说出来
                StatusMessage?.Invoke($"音乐源未接通：{_engine.LastError}");
                StatusText = "运行中（音乐源未接通）";
            }
            else
            {
                StatusText = "运行中";
            }

            StateChanged?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            Fail($"启动失败: {ex.Message}");
            return false;
        }
    }

    public void Stop()
    {
        try { _engine.Stop(); } catch { }
        StatusText = "已停止";
        StateChanged?.Invoke();
    }

    public bool ToggleRun()
    {
        if (IsRunning) { Stop(); return false; }
        return Start();
    }

    /// <summary>在当前播放设备上寻找 QQ 音乐的发声进程。</summary>
    private uint? ResolveMusicProcess()
    {
        // 先看音频会话：只有真正在出声的进程才值得捕获
        var sessions = AudioSessionProbe.EnumerateSessions();
        foreach (var s in sessions)
        {
            foreach (var name in _config.MusicProcessNames)
            {
                if (s.ProcessName.Contains(name, StringComparison.OrdinalIgnoreCase))
                    return s.ProcessId;
            }
        }

        // 会话里没有就按进程名找（可能还没开始播放）
        foreach (var name in _config.MusicProcessNames)
        {
            var pids = AudioSessionProbe.FindProcessIds(name);
            if (pids.Count > 0) return pids[0];
        }

        return null;
    }

    private void MonitorTick()
    {
        try
        {
            if (!_engine.IsRunning) return;

            // QQ 音乐重启后 PID 变了，需要重新挂接
            uint? current = ResolveMusicProcess();
            if (current is not uint pid) return;

            // 除了 PID 变化，还要覆盖"启动时挂接失败"的情况：
            // 那时 PID 已经记下了，但音乐源其实没挂上，必须在这里补一次。
            if (pid == _musicPid && _engine.HasMusicSource) return;

            _musicPid = pid;
            _engine.SwitchMusicSource(pid);

            if (_engine.HasMusicSource)
            {
                StatusMessage?.Invoke($"已重新连接 QQ 音乐 (PID {pid})");
            }
            else if (_engine.LastError is not null)
            {
                StatusMessage?.Invoke($"音乐源仍无法挂接：{_engine.LastError}");
            }

            StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"监控异常: {ex.Message}");
        }
    }

    private void OnHotkey(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.ToggleMusicInjection:
                _engine.MusicInjectionEnabled = !_engine.MusicInjectionEnabled;
                Beep(_engine.MusicInjectionEnabled);
                StatusMessage?.Invoke(_engine.MusicInjectionEnabled ? "音乐注入：开" : "音乐注入：关");
                break;

            case HotkeyAction.PlaySlot1:
            case HotkeyAction.PlaySlot2:
            case HotkeyAction.PlaySlot3:
            case HotkeyAction.PlaySlot4:
                PlaySlotByAction(action);
                break;
        }

        StateChanged?.Invoke();
    }

    /// <summary>当前正在播放的槽位序号（0 = 没有用槽位播放）。</summary>
    public int ActiveSlotIndex { get; private set; }

    /// <summary>按热键动作播放对应槽位；如果该槽位正在播，则暂停/继续。</summary>
    private void PlaySlotByAction(HotkeyAction action)
    {
        int index = action - HotkeyAction.PlaySlot1 + 1;
        var slot = _config.Slots.FirstOrDefault(s => s.Index == index);
        if (slot is null) return;

        // 同一个槽位再按一次 = 暂停/继续
        if (ActiveSlotIndex == index && _engine.LocalMusic is { } current)
        {
            current.TogglePause();
            StatusMessage?.Invoke(current.IsPaused
                ? $"「{slot.DisplayName}」已暂停"
                : $"「{slot.DisplayName}」继续播放");
            StateChanged?.Invoke();
            return;
        }

        if (slot.Files.Count == 0)
        {
            StatusMessage?.Invoke($"「{slot.DisplayName}」还没有绑定音乐");
            return;
        }

        if (!IsRunning)
        {
            if (!Start()) return;
        }

        PlaySlot(slot);
    }

    /// <summary>把当前配置写盘（槽位与热键改动后调用）。</summary>
    public void PersistConfig()
    {
        try
        {
            _config.Save();
        }
        catch (Exception ex)
        {
            StatusMessage?.Invoke($"保存配置失败: {ex.Message}");
        }
    }

    /// <summary>播放指定槽位的音乐（界面按钮也走这里）。</summary>
    public void PlaySlot(MusicSlot slot)
    {
        if (slot.Files.Count == 0) return;

        ActiveSlotIndex = slot.Index;

        // 解码失败（文件损坏 / 换了解码器）要能主动告诉用户，
        // 否则界面会一直显示"正在播放"而实际没声音。
        // 回调在 Start 之前挂好，避免启动瞬间的失败被漏掉。
        _engine.EnableLocalMusic(slot.Files, slot.Loop, OnLocalMusicFailed);

        StatusMessage?.Invoke($"正在播放「{slot.DisplayName}」");
    }

    private void OnLocalMusicFailed(string error)
    {
        ActiveSlotIndex = 0;
        StatusMessage?.Invoke(error);
        StateChanged?.Invoke();
    }

    /// <summary>停止槽位播放，回到 QQ 音乐回环。</summary>
    public void StopSlotPlayback()
    {
        _engine.DisableLocalMusic();
        ActiveSlotIndex = 0;
        StatusMessage?.Invoke("已停止槽位播放");
    }

    private void Beep(bool high)
    {
        if (!_config.EnableFeedbackBeep) return;
        try
        {
            // 用系统提示音做极短反馈，避免额外依赖
            Console.Beep(high ? 880 : 440, 60);
        }
        catch { }
    }

    private void Fail(string message)
    {
        StatusText = message;
        StatusMessage?.Invoke(message);
        StateChanged?.Invoke();
    }

    public void SaveConfig()
    {
        try
        {
            _config.MusicGain = _engine.MusicGain;
            _config.MicGain = _engine.MicGain;
            _config.Save();
            StatusMessage?.Invoke("配置已保存");
        }
        catch (Exception ex)
        {
            StatusMessage?.Invoke($"保存失败: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _monitorTimer.Dispose();
        _hotkeys?.Dispose();
        _engine.Dispose();
        _media.Dispose();
    }
}
