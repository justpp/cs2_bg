using System.Windows;
using System.Windows.Interop;

namespace Cs2VoiceMusic;

/// <summary>
/// 一个不可见的窗口，唯一作用是提供 HWND 让 RegisterHotKey 能投递 WM_HOTKEY。
///
/// 用隐藏的普通窗口而不是消息专用窗口（HWND_MESSAGE），
/// 是因为消息专用窗口收不到某些广播消息，隐藏普通窗口兼容性更好。
/// </summary>
public sealed class HotkeyWindow : Window
{
    private readonly Action<int> _onHotkey;

    public HotkeyWindow(Action<int> onHotkey)
    {
        _onHotkey = onHotkey;

        Width = 0;
        Height = 0;
        WindowStyle = WindowStyle.None;
        ShowInTaskbar = false;
        ShowActivated = false;
        AllowsTransparency = true;
        Opacity = 0;
        Left = -10000;
        Top = -10000;
        Title = "Cs2VoiceMusic.HotkeyWindow";
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd)?.AddHook(Hook);
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (Hotkeys.HotkeyManager.IsHotkeyMessage(msg))
        {
            _onHotkey(wParam.ToInt32());
            handled = true;
        }
        return IntPtr.Zero;
    }
}
