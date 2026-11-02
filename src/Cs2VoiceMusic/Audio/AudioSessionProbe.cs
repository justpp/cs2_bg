using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Cs2VoiceMusic.Audio;

/// <summary>
/// 通过 WASAPI 音频会话（IAudioSessionManager2）找出"当前正在出声"的进程。
/// 这比按进程名硬编码更可靠：QQ 音乐的实际发声进程可能叫 QQMusic.exe，
/// 也可能是其子进程/WebView 进程。
/// </summary>
internal static unsafe class AudioSessionProbe
{
    public sealed record SessionInfo(uint ProcessId, string ProcessName, string DisplayName, bool IsActive);

    /// <summary>枚举当前默认播放设备上的所有音频会话。</summary>
    public static List<SessionInfo> EnumerateSessions()
    {
        var list = new List<SessionInfo>();
        IntPtr pDevice = IntPtr.Zero, pClient = IntPtr.Zero, pSessionMgr = IntPtr.Zero, pSessionEnum = IntPtr.Zero;
        try
        {
            var defaultDev = AudioDeviceService.GetDefault(DeviceKind.Playback);
            if (defaultDev is null) return list;

            pDevice = AudioDeviceService.OpenDevice(defaultDev.Id);
            pClient = AudioDeviceService.ActivateAudioClient(pDevice, IntPtr.Zero);

            // IAudioClient::GetService(IAudioSessionManager2) — 槽位 14
            Guid iidMgr = Wasapi.IID_IAudioSessionManager2;
            int hr = Com.Call(pClient, 14, (IntPtr)(&iidMgr), (IntPtr)(&pSessionMgr));
            if (hr < 0 || pSessionMgr == IntPtr.Zero) return list;

            // IAudioSessionManager2: 3 GetAudioSessionControl, 4 GetSimpleAudioVolume,
            // 5 RegisterSessionNotification, 6 UnregisterSessionNotification,
            // 7 RegisterDuckNotification, 8 UnregisterDuckNotification,
            // 9 GetSessionEnumerator
            hr = Com.Call(pSessionMgr, 9, (IntPtr)(&pSessionEnum));
            if (hr < 0 || pSessionEnum == IntPtr.Zero) return list;

            // IAudioSessionEnumerator: 3 GetCount, 4 GetSession
            IntPtr pCount = IntPtr.Zero;
            hr = Com.Call(pSessionEnum, 3, (IntPtr)(&pCount));
            if (hr < 0) return list;
            int count = (int)pCount;

            for (int i = 0; i < count; i++)
            {
                IntPtr pCtrl = IntPtr.Zero;
                hr = Com.Call(pSessionEnum, 4, (IntPtr)i, (IntPtr)(&pCtrl));
                if (hr < 0 || pCtrl == IntPtr.Zero) continue;

                try
                {
                    // IAudioSessionControl::GetState — 槽位 3，返回 AudioSessionState
                    // 0=Inactive 1=Active 2=Expired
                    IntPtr pState = IntPtr.Zero;
                    hr = Com.Call(pCtrl, 3, (IntPtr)(&pState));
                    bool active = hr >= 0 && (int)pState == 1;

                    // QI 到 IAudioSessionControl2 才能拿到 PID
                    IntPtr pCtrl2 = IntPtr.Zero;
                    Guid iid2 = Wasapi.IID_IAudioSessionControl2;
                    hr = Com.Call(pCtrl, 0, (IntPtr)(&iid2), (IntPtr)(&pCtrl2));
                    if (hr < 0 || pCtrl2 == IntPtr.Zero) continue;

                    try
                    {
                        // IAudioSessionControl2: 3 GetSessionIdentifier, 4 GetSessionInstanceIdentifier,
                        // 5 GetProcessId, 6 IsSystemSoundsSession, 7 SetDuckingPreference
                        IntPtr pPid = IntPtr.Zero;
                        hr = Com.Call(pCtrl2, 5, (IntPtr)(&pPid));
                        if (hr < 0) continue;
                        uint pid = (uint)pPid;

                        string procName = "";
                        try
                        {
                            var p = Process.GetProcessById((int)pid);
                            procName = p.ProcessName;
                        }
                        catch { /* 进程可能已退出 */ }

                        // 会话显示名：IAudioSessionControl::GetDisplayName — 槽位 4
                        string display = "";
                        IntPtr pDisp = IntPtr.Zero;
                        if (Com.Call(pCtrl, 4, (IntPtr)(&pDisp)) >= 0 && pDisp != IntPtr.Zero)
                        {
                            display = Marshal.PtrToStringUni(pDisp) ?? "";
                            Marshal.FreeCoTaskMem(pDisp);
                        }

                        list.Add(new SessionInfo(pid, procName, display, active));
                    }
                    finally
                    {
                        Com.Release(pCtrl2);
                    }
                }
                finally
                {
                    Com.Release(pCtrl);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"枚举音频会话失败: {ex.Message}");
        }
        finally
        {
            Com.Release(pSessionEnum);
            Com.Release(pSessionMgr);
            Com.Release(pClient);
            Com.Release(pDevice);
        }

        return list;
    }

    /// <summary>按进程名查找候选 PID（含名字包含关系，例如 QQMusic / QQMusicMini）。</summary>
    public static List<uint> FindProcessIds(params string[] namePatterns)
    {
        var result = new List<uint>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                string n = p.ProcessName;
                foreach (var pat in namePatterns)
                {
                    if (n.Contains(pat, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add((uint)p.Id);
                        break;
                    }
                }
            }
            catch { }
            finally { p.Dispose(); }
        }
        return result;
    }
}
