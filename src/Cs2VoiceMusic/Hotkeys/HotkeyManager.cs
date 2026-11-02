using System.Runtime.InteropServices;
using Cs2VoiceMusic.Config;

namespace Cs2VoiceMusic.Hotkeys;

/// <summary>
/// 全局热键管理器，基于 RegisterHotKey。
///
/// 用 RegisterHotKey 而不是低级键盘钩子：不需要管理员权限，
/// 也不会被反作弊视为可疑的输入拦截行为。
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly IntPtr _hwnd;
    private readonly Dictionary<int, HotkeyBinding> _registered = new();
    private int _nextId = 1;

    public event Action<HotkeyAction>? HotkeyPressed;

    /// <summary>注册失败的绑定（用于界面提示冲突）。</summary>
    public List<HotkeyBinding> FailedBindings { get; } = new();

    public HotkeyManager(IntPtr hwnd)
    {
        _hwnd = hwnd;
    }

    /// <summary>重新注册全部热键。返回失败项。</summary>
    public List<HotkeyBinding> RegisterAll(IEnumerable<HotkeyBinding> bindings)
    {
        UnregisterAll();
        FailedBindings.Clear();

        foreach (var b in bindings)
        {
            if (!b.IsBound) continue;

            int id = _nextId++;
            // MOD_NOREPEAT 让长按只触发一次
            if (RegisterHotKey(_hwnd, id, b.Modifiers | MOD_NOREPEAT, b.VirtualKey))
            {
                _registered[id] = b.Clone();
            }
            else
            {
                FailedBindings.Add(b.Clone());
                _nextId--;
            }
        }

        return FailedBindings;
    }

    public void UnregisterAll()
    {
        foreach (var id in _registered.Keys)
        {
            try { UnregisterHotKey(_hwnd, id); } catch { }
        }
        _registered.Clear();
        _nextId = 1;
    }

    /// <summary>由窗口过程转发 WM_HOTKEY。</summary>
    public void HandleMessage(int wParam)
    {
        if (_registered.TryGetValue(wParam, out var binding))
        {
            HotkeyPressed?.Invoke(binding.Action);
        }
    }

    public static bool IsHotkeyMessage(int msg) => msg == WM_HOTKEY;

    public void Dispose()
    {
        UnregisterAll();
    }
}
