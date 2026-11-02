using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using ShapePath = System.Windows.Shapes.Path;
using System.Windows.Threading;
using Cs2VoiceMusic.Audio;
using Cs2VoiceMusic.Config;
using Cs2VoiceMusic.Media;

namespace Cs2VoiceMusic.Ui;

public partial class MainWindow : Window
{
    private readonly AppController _controller;
    private readonly DispatcherTimer _uiTimer;
    private readonly DispatcherTimer _mediaTimer;
    private HotkeyBinding? _capturing;

    public MainWindow(AppController controller)
    {
        InitializeComponent();
        _controller = controller;

        RefreshDevices();
        LoadConfigToUi();
        BuildSlotCards();
        BuildHotkeyList();

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _uiTimer.Tick += (_, _) => UpdateLevels();
        _uiTimer.Start();

        _mediaTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _mediaTimer.Tick += (_, _) => UpdateMediaInfo();
        _mediaTimer.Start();

        _controller.StateChanged += OnStateChanged;
        Loaded += (_, _) =>
        {
            UpdateState();
            UpdateMediaInfo();
            RefreshSlotCards();
            UpdateSlotPlayIcons();
        };

        // 窗口被隐藏后定时器仍在跑：电平/诊断没必要在看不见的时候刷新
        IsVisibleChanged += (_, _) => UpdateTimerState();

        Closing += OnClosing;
    }

    /// <summary>只在窗口可见时跑定时器，隐藏到托盘后彻底停掉，省 CPU。</summary>
    private void UpdateTimerState()
    {
        if (IsVisible)
        {
            if (!_uiTimer.IsEnabled) _uiTimer.Start();
            if (!_mediaTimer.IsEnabled) _mediaTimer.Start();
        }
        else
        {
            _uiTimer.Stop();
            _mediaTimer.Stop();
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // 关闭窗口 = 隐藏到托盘，程序继续运行。
        // 但退出程序时必须真的关掉，否则托盘"退出"会卡在这里。
        if (_reallyClose)
        {
            e.Cancel = false;
            return;
        }

        e.Cancel = true;
        Hide();
    }

    // ---------------- 状态 ----------------

    private void OnStateChanged()
    {
        if (Dispatcher.CheckAccess()) UpdateState();
        else Dispatcher.BeginInvoke(new Action(UpdateState));
    }

    private void UpdateState()
    {
        bool running = _controller.IsRunning;

        StartStopButton.Content = running ? "停止混音" : "启动混音";
        RunStateText.Text = running ? "运行中" : "已停止";
        RunStateBadge.Background = running
            ? (Brush)FindResource("GoodBrush")
            : (Brush)FindResource("PanelAltBrush");
        RunStateText.Foreground = running ? Brushes.White : (Brush)FindResource("TextDimBrush");

        StatusText.Text = _controller.StatusText;
        InjectToggle.IsChecked = _controller.Engine.MusicInjectionEnabled;
        MicToggle.IsChecked = _controller.Engine.MicEnabled;

        UpdateDiagnostics();

        // 只刷图标：RefreshSlotCards 会逐个 File.Exists（网络盘/外接盘可能很慢），
        // 不该在每次状态变化时都跑一遍。
        UpdateSlotPlayIcons();
    }

    private void UpdateDiagnostics()
    {
        var e = _controller.Engine;
        var lines = new List<string>();

        lines.Add($"渲染：{(e.IsRunning ? "运行中" : "停止")}  ·  已输出 {e.FramesRendered / 48000.0:F1} 秒");
        lines.Add($"音乐源：{(e.MusicHasSignal ? "已捕获到声音" : "尚未捕获到声音")}"
                  + $"  ·  欠载 {e.MusicUnderruns}  ·  丢弃 {e.MusicOverruns}");
        lines.Add($"麦克风：{(e.MicHasSignal ? "已捕获到声音" : "尚未捕获到声音")}"
                  + $"  ·  欠载 {e.MicUnderruns}  ·  丢弃 {e.MicOverruns}");

        if (e.LastError is not null) lines.Add($"错误：{e.LastError}");
        if (e.RenderError is not null) lines.Add($"渲染线程错误：{e.RenderError}");
        if (e.LocalMusic?.LastError is not null) lines.Add($"播放错误：{e.LocalMusic.LastError}");
        if (_controller.Media.LastError is not null) lines.Add($"媒体控制：{_controller.Media.LastError}");

        DiagnosticText.Text = string.Join(Environment.NewLine, lines);
    }

    private void UpdateLevels()
    {
        if (!IsVisible) return;

        double mic = Math.Clamp(_controller.Engine.MicPeak, 0, 1);
        double music = Math.Clamp(_controller.Engine.MusicPeak, 0, 1);

        MicLevelBar.Width = MicLevelBar.Parent is Grid g1 ? g1.ActualWidth * mic : 0;
        MusicLevelBar.Width = MusicLevelBar.Parent is Grid g2 ? g2.ActualWidth * music : 0;

        if (_uiTimerCounter++ % 20 == 0)
        {
            UpdateDiagnostics();
            RefreshProcessStatus();
        }
    }

    private int _uiTimerCounter;

    /// <summary>进程名扫描要枚举全部进程，放到后台线程，避免拖慢 UI 定时器。</summary>
    private void RefreshProcessStatus()
    {
        if (_processScanPending) return;
        _processScanPending = true;

        // 配置里的列表可能正被 UI 线程改（保存设置），先拷一份快照
        string[] names;
        try { names = _controller.Config.MusicProcessNames.ToArray(); }
        catch { names = Array.Empty<string>(); }
        if (names.Length == 0) names = new[] { "QQMusic" };
        Task.Run(() =>
        {
            List<uint> pids;
            try { pids = AudioSessionProbe.FindProcessIds(names); }
            catch { pids = new List<uint>(); }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                _processScanPending = false;
                ProcessStatusText.Text = pids.Count > 0
                    ? $"已找到 QQ 音乐进程：{string.Join(", ", pids)}"
                    : "未找到 QQ 音乐进程（请确认已启动 QQ 音乐）";
            }));
        });
    }

    private bool _processScanPending;

    private void UpdateMediaInfo()
    {
        // 同步等待 WinRT 会在 UI 线程上阻塞（没有媒体会话时尤其明显），
        // 所以放到线程池执行，回到 UI 线程再更新控件。
        if (_mediaRefreshPending) return;
        _mediaRefreshPending = true;

        Task.Run(() =>
        {
            MediaInfo? info;
            try
            {
                info = _controller.Media.GetInfo();
            }
            catch
            {
                info = null;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                _mediaRefreshPending = false;
                ApplyMediaInfo(info);
            }));
        });
    }

    /// <summary>媒体信息查询在途，避免慢的时候堆积请求。</summary>
    private bool _mediaRefreshPending;

    private void ApplyMediaInfo(MediaInfo? info)
    {
        if (info is null || string.IsNullOrWhiteSpace(info.Title))
        {
            NowPlayingTitle.Text = "QQ 音乐未在播放";
            NowPlayingArtist.Text = "启动 QQ 音乐并播放后，这里会显示歌曲信息";
            SetPlayIcon(false);
            return;
        }

        NowPlayingTitle.Text = info.Title;
        NowPlayingArtist.Text = string.IsNullOrWhiteSpace(info.Artist) ? info.Album : info.Artist;
        SetPlayIcon(info.IsPlaying);
    }

    /// <summary>切换播放/暂停图标（矢量，不依赖字体）。</summary>
    private void SetPlayIcon(bool playing)
    {
        PlayPauseIcon.Data = (System.Windows.Media.Geometry)FindResource(playing ? "IconPause" : "IconPlay");
    }

    // ---------------- 设备 ----------------

    private void RefreshDevices()
    {
        var outputs = AudioDeviceService.Enumerate(DeviceKind.Playback);
        var mics = AudioDeviceService.Enumerate(DeviceKind.Recording);

        var outputItems = new List<AudioDeviceInfo>
        {
            new("", "（默认播放设备）", DeviceKind.Playback),
        };
        outputItems.AddRange(outputs);
        OutputDeviceCombo.ItemsSource = outputItems;

        var micItems = new List<AudioDeviceInfo>
        {
            new("", "（不使用麦克风）", DeviceKind.Recording),
        };
        micItems.AddRange(mics);
        MicDeviceCombo.ItemsSource = micItems;

        // 恢复选中项
        OutputDeviceCombo.SelectedItem =
            outputItems.FirstOrDefault(d => d.Id == _controller.Config.OutputDeviceId) ?? outputItems[0];
        MicDeviceCombo.SelectedItem =
            micItems.FirstOrDefault(d => d.Id == _controller.Config.MicDeviceId) ?? micItems[0];
    }

    private void RefreshDevices_Click(object sender, RoutedEventArgs e) => RefreshDevices();

    private void LoadConfigToUi()
    {
        ProcessNamesBox.Text = string.Join(", ", _controller.Config.MusicProcessNames);
    }

    // ---------------- 热键 ----------------

    private sealed class HotkeyRow
    {
        public HotkeyAction Action { get; init; }
        public string ActionName { get; init; } = "";
        public string DisplayText { get; set; } = "";
        public HotkeyBinding Binding { get; set; } = new();
    }

    private void BuildHotkeyList()
    {
        var rows = new List<HotkeyRow>();
        foreach (var b in _controller.Config.Hotkeys)
        {
            rows.Add(new HotkeyRow
            {
                Action = b.Action,
                ActionName = ActionDisplayName(b.Action),
                DisplayText = b.ToString(),
                Binding = b,
            });
        }
        HotkeyList.ItemsSource = rows;
    }

    private string ActionDisplayName(HotkeyAction action)
    {
        if (action == HotkeyAction.ToggleMusicInjection) return "开关音乐注入";

        int slotIndex = action - HotkeyAction.PlaySlot1 + 1;
        var slot = _controller.Config.Slots.FirstOrDefault(s => s.Index == slotIndex);
        string name = slot?.DisplayName ?? $"槽位 {slotIndex}";
        int count = slot?.Files.Count ?? 0;
        return count > 0
            ? $"播放「{name}」（{count} 首）"
            : $"播放「{name}」（未绑定音乐）";
    }

    private void HotkeyCapture_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not HotkeyRow row) return;

        _capturing = row.Binding;
        HotkeyHint.Text = $"请按下「{row.ActionName}」的新组合键，Esc 取消";
        btn.Content = "按下按键…";

        PreviewKeyDown += CaptureKey;
        btn.Focus();
    }

    private void CaptureKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_capturing is null) return;

        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            EndCapture();
            return;
        }

        // 只按下修饰键时继续等待
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return;
        }

        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        uint mods = 0;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) mods |= 2;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) mods |= 1;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) mods |= 4;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) mods |= 8;

        // 无修饰键时只允许 F1~F24：RegisterHotKey 注册的是全局热键，
        // 若允许裸字母/数字，整个系统的该键（含游戏内按键）都会被吞掉。
        bool isFunctionKey = vk >= 0x70 && vk <= 0x87;
        if (mods == 0 && !isFunctionKey)
        {
            HotkeyHint.Text = "请至少带一个修饰键（Ctrl / Alt / Shift / Win），或使用 F1~F24";
            return;
        }

        _capturing.Modifiers = mods;
        _capturing.VirtualKey = vk;

        EndCapture();
    }

    private void EndCapture()
    {
        PreviewKeyDown -= CaptureKey;
        _capturing = null;
        HotkeyHint.Text = "";

        var failed = _controller.ReloadHotkeys();
        _controller.PersistConfig();
        BuildHotkeyList();
        RefreshSlotCards();

        HotkeyHint.Text = failed.Count > 0
            ? $"有 {failed.Count} 个快捷键被占用：{string.Join("、", failed.Select(f => f.ToString()))}"
            : "";
    }

    private void ResetHotkeys_Click(object sender, RoutedEventArgs e)
    {
        _controller.Config.Hotkeys = AppConfig.CreateDefault().Hotkeys;
        _controller.ReloadHotkeys();
        _controller.PersistConfig();   // 与录制路径保持一致，否则重启就丢了
        BuildHotkeyList();
        RefreshSlotCards();
        HotkeyHint.Text = "已恢复默认快捷键";
    }

    // ---------------- 按钮 ----------------

    private void StartStop_Click(object sender, RoutedEventArgs e)
    {
        _controller.ToggleRun();
        UpdateState();
    }

    private void InjectToggle_Click(object sender, RoutedEventArgs e)
    {
        _controller.Engine.MusicInjectionEnabled = InjectToggle.IsChecked == true;
    }

    private void MicToggle_Click(object sender, RoutedEventArgs e)
    {
        _controller.Engine.MicEnabled = MicToggle.IsChecked == true;
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        // 同步等 WinRT 会卡住 UI 线程，放后台执行
        Task.Run(() => _controller.Media.PlayPause());
        UpdateMediaInfo();
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        Task.Run(() => _controller.Media.Next());
        UpdateMediaInfo();
    }

    private void Prev_Click(object sender, RoutedEventArgs e)
    {
        Task.Run(() => _controller.Media.Previous());
        UpdateMediaInfo();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var cfg = _controller.Config;

        if (OutputDeviceCombo.SelectedItem is AudioDeviceInfo outDev)
            cfg.OutputDeviceId = outDev.Id;

        if (MicDeviceCombo.SelectedItem is AudioDeviceInfo micDev)
            cfg.MicDeviceId = micDev.Id;

        cfg.MusicProcessNames = ProcessNamesBox.Text
            .Split(new[] { ',', '，', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();

        if (cfg.MusicProcessNames.Count == 0)
            cfg.MusicProcessNames = new List<string> { "QQMusic" };

        cfg.MusicGain = _controller.Engine.MusicGain;
        cfg.MicGain = _controller.Engine.MicGain;
        cfg.MusicInjectionDefault = _controller.Engine.MusicInjectionEnabled;

        _controller.SaveConfig();
        FooterHint.Text = $"已保存 · {DateTime.Now:HH:mm:ss}";

        // 设备变了就重启音频通路
        if (_controller.IsRunning)
        {
            _controller.Stop();
            _controller.Start();
        }
        UpdateState();
    }

    // ---------------- 音乐槽位 ----------------

    private static readonly string[] AudioExtensions =
        { ".mp3", ".wav", ".flac", ".m4a", ".aac", ".wma", ".ogg", ".opus" };

    /// <summary>槽位界面元素，便于单独刷新。</summary>
    private sealed class SlotUi
    {
        public MusicSlot Slot { get; set; } = new();
        public TextBlock Title { get; set; } = new();
        public TextBlock Badge { get; set; } = new();
        public TextBlock Summary { get; set; } = new();
        public Button PlayButton { get; set; } = new();
        public ShapePath PlayIcon { get; set; } = new();
    }

    private readonly List<SlotUi> _slotUis = new();

    /// <summary>按配置构建 4 个槽位卡片。</summary>
    private void BuildSlotCards()
    {
        SlotPanel.Children.Clear();
        _slotUis.Clear();

        foreach (var slot in _controller.Config.Slots.OrderBy(s => s.Index))
        {
            var ui = new SlotUi { Slot = slot };

            var card = new Border
            {
                Style = (Style)FindResource("Card"),
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(new TextBlock
            {
                Text = slot.DisplayName,
                FontWeight = FontWeights.SemiBold,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
            });
            var badge = new TextBlock
            {
                Text = ShortcutOf(slot.Index),
                FontSize = 11,
                Foreground = (Brush)FindResource("AccentBrush"),
            };
            header.Children.Add(new Border
            {
                Background = (Brush)FindResource("PanelAltBrush"),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(8, 0, 0, 0),
                Child = badge,
            });
            left.Children.Add(header);

            var summary = new TextBlock
            {
                Style = (Style)FindResource("DimText"),
                Margin = new Thickness(0, 4, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Text = SummarizeSlot(slot),
            };
            left.Children.Add(summary);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var pick = new Button
            {
                Content = "选择音乐…",
                Style = (Style)FindResource("SecondaryButton"),
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 0, 6, 0),
                Tag = ui,
            };
            pick.Click += PickSlotFiles_Click;
            buttons.Children.Add(pick);

            var clear = new Button
            {
                Content = "清空",
                Style = (Style)FindResource("SecondaryButton"),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 6, 0),
                Tag = ui,
            };
            clear.Click += ClearSlot_Click;
            buttons.Children.Add(clear);

            var playIcon = new ShapePath
            {
                Data = (Geometry)FindResource("IconPlay"),
                Style = (Style)FindResource("IconShapeOnAccent"),
            };
            var play = new Button
            {
                Style = (Style)FindResource("PrimaryButton"),
                Width = 44,
                Padding = new Thickness(0, 6, 0, 6),
                ToolTip = "播放这个槽位",
                Content = playIcon,
                Tag = ui,
            };
            play.Click += PlaySlot_Click;
            buttons.Children.Add(play);

            ui.Title = (TextBlock)header.Children[0];
            ui.Badge = badge;
            ui.Summary = summary;
            ui.PlayButton = play;
            ui.PlayIcon = playIcon;
            _slotUis.Add(ui);

            Grid.SetColumn(left, 0);
            Grid.SetColumn(buttons, 1);
            grid.Children.Add(left);
            grid.Children.Add(buttons);

            card.Child = grid;
            SlotPanel.Children.Add(card);
        }
    }

    private string ShortcutOf(int slotIndex)
    {
        var action = HotkeyAction.PlaySlot1 + (slotIndex - 1);
        var binding = _controller.Config.Hotkeys.FirstOrDefault(h => h.Action == action);
        return binding?.ToString() ?? "未绑定";
    }

    private static string SummarizeSlot(MusicSlot slot)
    {
        if (slot.Files.Count == 0) return "还没有添加音乐，点右侧「选择音乐」";

        var missing = slot.FindMissingFiles();
        string head = slot.Files.Count == 1
            ? Path.GetFileName(slot.Files[0])
            : $"{slot.Files.Count} 首 · 从 {Path.GetFileName(slot.Files[0])} 开始";

        return missing.Count == 0
            ? head
            : $"{head}　（{missing.Count} 个文件当前不可用）";
    }

    private void RefreshSlotCards()
    {
        foreach (var ui in _slotUis)
        {
            ui.Summary.Text = SummarizeSlot(ui.Slot);
            ui.Badge.Text = ShortcutOf(ui.Slot.Index);
        }
        UpdateSlotPlayIcons();
    }

    private void PickSlotFiles_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not SlotUi ui) return;

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"为「{ui.Slot.DisplayName}」选择音乐",
            Multiselect = true,
            Filter = "音频文件|*.mp3;*.wav;*.flac;*.m4a;*.aac;*.wma;*.ogg;*.opus|所有文件|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;

        foreach (var f in dlg.FileNames)
        {
            if (!ui.Slot.Files.Contains(f, StringComparer.OrdinalIgnoreCase))
                ui.Slot.Files.Add(f);
        }

        RefreshSlotCards();
        _controller.PersistConfig();
        FooterHint.Text = $"「{ui.Slot.DisplayName}」已保存";
    }

    private void ClearSlot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not SlotUi ui) return;
        ui.Slot.Files.Clear();
        RefreshSlotCards();
        _controller.PersistConfig();
        FooterHint.Text = $"「{ui.Slot.DisplayName}」已清空";
    }

    private void PlaySlot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not SlotUi ui) return;
        PlaySlot(ui.Slot);
    }

    /// <summary>播放指定槽位（也供热键调用）。</summary>
    public void PlaySlot(MusicSlot slot)
    {
        if (slot.Files.Count == 0)
        {
            FooterHint.Text = $"「{slot.DisplayName}」还没有音乐";
            return;
        }

        if (!_controller.IsRunning)
        {
            _controller.Start();
            UpdateState();
        }

        _controller.PlaySlot(slot);
        FooterHint.Text = "正在播放：" + Path.GetFileName(slot.Files[0]);
        UpdateSlotPlayIcons();
    }

    private void UpdateSlotPlayIcons()
    {
        int activeIndex = _controller.ActiveSlotIndex;
        var local = _controller.Engine.LocalMusic;
        bool playing = local is { IsRunning: true, IsPaused: false };

        foreach (var ui in _slotUis)
        {
            bool active = activeIndex == ui.Slot.Index && playing;
            ui.PlayIcon.Data = (Geometry)FindResource(active ? "IconPause" : "IconPlay");
        }
    }

    private void Hide_Click(object sender, RoutedEventArgs e) => Hide();

    /// <summary>允许窗口真正关闭（退出程序时用）。</summary>
    public void AllowClose()
    {
        _reallyClose = true;
        Close();
    }

    private bool _reallyClose;
}
