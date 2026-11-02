using System.IO;
using System.Windows;
using Cs2VoiceMusic.Config;
using System.Windows.Threading;

namespace Cs2VoiceMusic;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstanceMutex;
    private TrayApp? _tray;
    private static readonly string LogPath =
        Path.Combine(AppConfig.DefaultDirectory, "app.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 自检模式：不进界面
        if (e.Args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            int code = SelfTest.Run(e.Args);
            Shutdown(code);
            return;
        }

        // 单实例：重复启动时把已有窗口叫出来，而不是弹一个没用的提示框。
        // 因为已去掉了"打开窗口"的全局热键，双击 exe 是最自然的入口。
        _singleInstanceMutex = new Mutex(true, @"Global\Cs2VoiceMusic.SingleInstance", out bool isNew);
        if (!isNew)
        {
            if (TryActivateExistingInstance())
            {
                Shutdown(0);
                return;
            }

            MessageBox.Show("CS2 语音放歌助手已经在运行了，请在系统托盘里查看（双击托盘图标可打开设置）。",
                "已在运行", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log("未处理异常: " + args.ExceptionObject);

        Log("=== 启动 ===");

        try
        {
            _tray = new TrayApp();
            _tray.Initialize();
        }
        catch (Exception ex)
        {
            Log("初始化失败: " + ex);
            MessageBox.Show($"初始化失败：{ex.Message}\n\n日志：{LogPath}",
                "CS2 语音放歌助手", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>
    /// 通过命名事件通知已运行的实例把窗口弹出来。
    /// 用 EventWaitHandle 而不是窗口消息广播：不需要提升权限，也不受 UIPI 限制。
    /// </summary>
    private static bool TryActivateExistingInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(@"Global\Cs2VoiceMusic.ShowWindow", out var evt))
            {
                using (evt)
                {
                    evt.Set();
                }
                return true;
            }
        }
        catch { }

        return false;
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log("界面线程异常: " + e.Exception);

        // 托盘初始化之前（或托盘已释放）弹窗会让程序变成一个关不掉的僵尸，
        // 这种情况记日志后直接退出，别再让用户对着看不见的窗口发呆。
        if (_tray is null)
        {
            e.Handled = false;
            return;
        }

        e.Handled = true;
        MessageBox.Show($"发生错误：{e.Exception.Message}\n\n日志：{LogPath}",
            "CS2 语音放歌助手", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log("=== 退出 ===");
        _tray?.Dispose();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(AppConfig.DefaultDirectory);
            File.AppendAllText(LogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch { }
    }
}
