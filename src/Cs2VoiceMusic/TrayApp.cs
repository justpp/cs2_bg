using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using Cs2VoiceMusic.Config;
using Cs2VoiceMusic.Ui;
using Application = System.Windows.Application;

namespace Cs2VoiceMusic;

/// <summary>
/// 托盘常驻宿主：持有隐藏的主窗口（用于接收 WM_HOTKEY），
/// 负责创建托盘图标与设置窗口。
/// </summary>
public sealed class TrayApp : IDisposable
{
    private NotifyIcon? _notifyIcon;
    private HotkeyWindow? _hotkeyWindow;
    private MainWindow? _mainWindow;
    private AppController? _controller;
    private ToolStripMenuItem? _toggleInjectItem;
    private ToolStripMenuItem? _statusItem;

    private EventWaitHandle? _showWindowEvent;
    private RegisteredWaitHandle? _showWindowWait;

    public void Initialize()
    {
        _controller = new AppController();

        // 监听"请打开窗口"信号：另一个实例启动时用它把窗口叫出来
        _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset,
            @"Global\Cs2VoiceMusic.ShowWindow");
        _showWindowWait = ThreadPool.RegisterWaitForSingleObject(
            _showWindowEvent,
            (_, _) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(ShowMainWindow)),
            null, Timeout.Infinite, false);

        // 隐藏窗口：只用来接收 WM_HOTKEY 消息
        _hotkeyWindow = new HotkeyWindow(id => _controller?.Hotkeys?.HandleMessage(id));
        _hotkeyWindow.Show();
        _hotkeyWindow.Hide();

        IntPtr hwnd = new WindowInteropHelper(_hotkeyWindow).Handle;
        _controller.AttachHotkeys(hwnd);
        _controller.StateChanged += UpdateTrayState;
        _controller.StatusMessage += OnStatusMessage;

        CreateTrayIcon();

        if (_controller.Config.AutoStartOnLaunch)
        {
            // 稍等片刻再启动，确保托盘图标先出现
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(800),
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                _controller.Start();
                _controller.StartMonitoring();
                UpdateTrayState();
            };
            timer.Start();
        }
        else
        {
            _controller.StartMonitoring();
        }
    }

    private void CreateTrayIcon()
    {
        _notifyIcon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "CS2 语音放歌助手",
            Visible = true,
        };

        var menu = new ContextMenuStrip();

        _statusItem = new ToolStripMenuItem("状态：未启动") { Enabled = false };
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());

        var toggleRun = new ToolStripMenuItem("启动 / 停止混音");
        toggleRun.Click += (_, _) =>
        {
            _controller?.ToggleRun();
            UpdateTrayState();
        };
        menu.Items.Add(toggleRun);

        _toggleInjectItem = new ToolStripMenuItem("音乐注入：开");
        _toggleInjectItem.Click += (_, _) =>
        {
            if (_controller is null) return;
            _controller.Engine.MusicInjectionEnabled = !_controller.Engine.MusicInjectionEnabled;
            UpdateTrayState();
        };
        menu.Items.Add(_toggleInjectItem);

        // 音乐槽位：托盘里也能直接点
        var slotMenu = new ToolStripMenuItem("播放槽位");
        for (int i = 1; i <= 4; i++)
        {
            int index = i;
            var item = new ToolStripMenuItem($"槽位 {index}") { Tag = index };
            item.Click += (_, _) =>
            {
                var slot = _controller?.Config.Slots.FirstOrDefault(s => s.Index == index);
                if (slot is null) return;
                if (slot.Files.Count == 0)
                {
                    ShowMainWindow();
                    return;
                }
                var ctl = _controller;
                if (ctl is null) return;
                if (!ctl.IsRunning) ctl.Start();
                ctl.PlaySlot(slot);
                UpdateTrayState();
            };
            slotMenu.DropDownItems.Add(item);
        }
        slotMenu.DropDownItems.Add(new ToolStripSeparator());
        var stopSlot = new ToolStripMenuItem("停止槽位播放");
        stopSlot.Click += (_, _) =>
        {
            _controller?.StopSlotPlayback();
            UpdateTrayState();
        };
        slotMenu.DropDownItems.Add(stopSlot);
        menu.Items.Add(slotMenu);

        menu.Items.Add(new ToolStripSeparator());

        var settings = new ToolStripMenuItem("设置…");
        settings.Click += (_, _) => ShowMainWindow();
        menu.Items.Add(settings);

        var openConfig = new ToolStripMenuItem("打开配置目录");
        openConfig.Click += (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start("explorer.exe", Config.AppConfig.DefaultDirectory);
            }
            catch { }
        };
        menu.Items.Add(openConfig);

        menu.Items.Add(new ToolStripSeparator());

        var exit = new ToolStripMenuItem("退出");
        exit.Click += (_, _) => Application.Current.Shutdown();
        menu.Items.Add(exit);

        _notifyIcon.ContextMenuStrip = menu;
        _notifyIcon.DoubleClick += (_, _) => ShowMainWindow();

        // 菜单展开时刷新一次状态，避免显示过期的"开/关"
        menu.Opening += (_, _) =>
        {
            UpdateTrayState();
            RefreshSlotMenu(slotMenu);
        };
    }

    /// <summary>刷新槽位菜单项的文字：显示名称、曲目数与快捷键。</summary>
    private void RefreshSlotMenu(ToolStripMenuItem slotMenu)
    {
        if (_controller is null) return;

        foreach (ToolStripItem item in slotMenu.DropDownItems)
        {
            if (item is not ToolStripMenuItem mi || mi.Tag is not int index) continue;

            var slot = _controller.Config.Slots.FirstOrDefault(s => s.Index == index);
            if (slot is null) continue;

            var binding = _controller.Config.Hotkeys
                .FirstOrDefault(h => h.Action == HotkeyAction.PlaySlot1 + (index - 1));
            string key = binding?.IsBound == true ? binding.ToString() : "未绑定";
            string count = slot.Files.Count == 0 ? "未绑定音乐" : $"{slot.Files.Count} 首";
            mi.Text = $"{slot.DisplayName}（{count}）\t{key}";
            mi.Enabled = true;
        }
    }

    private static System.Drawing.Icon LoadIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/app.ico");
            var stream = System.Windows.Application.GetResourceStream(uri)?.Stream;
            if (stream is not null)
            {
                return new System.Drawing.Icon(stream);
            }
        }
        catch { }

        return System.Drawing.SystemIcons.Application;
    }

    private void ShowMainWindow()
    {
        if (_controller is null) return;

        if (_mainWindow is null)
        {
            _mainWindow = new MainWindow(_controller);
            _mainWindow.Closed += (_, _) => _mainWindow = null;
        }

        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Normal;

        _mainWindow.Activate();
        _mainWindow.Topmost = true;
        _mainWindow.Topmost = false;
        _mainWindow.Focus();
    }

    private void OnStatusMessage(string message)
    {
        if (_notifyIcon is null) return;

        // StatusMessage 可能来自监控定时器或音频线程，NotifyIcon 只能在 UI 线程碰
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action<string>(OnStatusMessage), message);
            return;
        }

        // NotifyIcon.Text 上限 63 个字符，超长会抛 ArgumentOutOfRangeException。
        // 这条路径可能被音频线程触发，异常会直接打断渲染循环，所以必须兜住。
        try
        {
            _notifyIcon.Text = Shorten($"CS2 语音放歌助手 - {message}", 63);
        }
        catch { }

        UpdateTrayState();
    }

    private static string Shorten(string text, int max)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= max) return text;
        return text[..Math.Max(0, max - 1)] + "…";
    }

    private void UpdateTrayState()
    {
        if (_controller is null || _notifyIcon is null) return;

        // StateChanged 可能来自音频线程，NotifyIcon 只能在 UI 线程操作
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(UpdateTrayState));
            return;
        }

        bool running = _controller.IsRunning;
        string inject = _controller.MusicInjectionEnabled ? "开" : "关";

        // "没挂上"和"挂上了但还没出声"是两种完全不同的故障，分开显示，
        // 否则用户看到"无信号"会一直以为是 QQ 音乐没在播放。
        string music;
        if (!_controller.Engine.HasMusicSource) music = "音乐源未接通";
        else if (_controller.Engine.MusicHasSignal) music = "已捕获";
        else music = "无信号";

        if (_statusItem is not null)
        {
            _statusItem.Text = running
                ? $"状态：运行中 · 音乐注入{inject} · {music}"
                : "状态：已停止";
        }

        if (_toggleInjectItem is not null)
        {
            _toggleInjectItem.Text = $"音乐注入：{inject}";
        }

        try
        {
            _notifyIcon.Text = running
                ? $"CS2 语音放歌助手 · 注入{inject}"
                : "CS2 语音放歌助手 · 已停止";
        }
        catch { }
    }

    public void Dispose()
    {
        _showWindowWait?.Unregister(null);
        _showWindowWait = null;
        _showWindowEvent?.Dispose();
        _showWindowEvent = null;

        _controller?.StopMonitoring();
        _controller?.Dispose();
        _controller = null;

        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        _hotkeyWindow?.Close();
        _hotkeyWindow = null;

        // 主窗口默认"关闭即隐藏"，退出时必须让它真的关掉
        if (_mainWindow is not null)
        {
            _mainWindow.AllowClose();
            _mainWindow = null;
        }
    }
}
