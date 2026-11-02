using System.Runtime.InteropServices;

namespace Cs2VoiceMusic.Audio;

public enum DeviceKind
{
    Playback,
    Recording,
}

public sealed record AudioDeviceInfo(string Id, string Name, DeviceKind Kind)
{
    public override string ToString() => Name;
}

/// <summary>音频设备枚举，以及"哪个进程正在出声"的查询。</summary>
internal static unsafe class AudioDeviceService
{
    /// <summary>枚举全部活动设备（播放/录音）。</summary>
    public static List<AudioDeviceInfo> Enumerate(DeviceKind kind)
    {
        var result = new List<AudioDeviceInfo>();
        int flow = kind == DeviceKind.Playback ? Wasapi.eRender : Wasapi.eCapture;

        Guid clsid = Wasapi.CLSID_MMDeviceEnumerator;
        Guid iid = new Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"); // IMMDeviceEnumerator
        int hr = Wasapi.CoCreateInstance(ref clsid, IntPtr.Zero, Wasapi.CLSCTX_ALL, ref iid, out IntPtr pEnum);
        Com.Check(hr, "创建 IMMDeviceEnumerator");

        IntPtr pCollection = IntPtr.Zero;
        try
        {
            // IMMDeviceEnumerator: 3 EnumAudioEndpoints, 4 GetDefaultAudioEndpoint, 5 GetDevice
            hr = Com.Call(pEnum, 3, (IntPtr)flow, (IntPtr)Wasapi.DEVICE_STATE_ACTIVE, (IntPtr)(&pCollection));
            Com.Check(hr, "EnumAudioEndpoints");

            // IMMDeviceCollection: 3 GetCount, 4 Item
            IntPtr countPtr = IntPtr.Zero;
            hr = Com.Call(pCollection, 3, (IntPtr)(&countPtr));
            Com.Check(hr, "GetCount");
            int count = (int)countPtr;

            for (int i = 0; i < count; i++)
            {
                IntPtr pDevice = IntPtr.Zero;
                hr = Com.Call(pCollection, 4, (IntPtr)i, (IntPtr)(&pDevice));
                if (hr < 0 || pDevice == IntPtr.Zero) continue;

                try
                {
                    string id = GetDeviceId(pDevice) ?? $"device-{i}";
                    string name = GetFriendlyName(pDevice) ?? id;
                    result.Add(new AudioDeviceInfo(id, name, kind));
                }
                finally
                {
                    Com.Release(pDevice);
                }
            }
        }
        finally
        {
            Com.Release(pCollection);
            Com.Release(pEnum);
        }

        return result;
    }

    /// <summary>取默认设备。</summary>
    public static AudioDeviceInfo? GetDefault(DeviceKind kind)
    {
        int flow = kind == DeviceKind.Playback ? Wasapi.eRender : Wasapi.eCapture;
        Guid clsid = Wasapi.CLSID_MMDeviceEnumerator;
        Guid iid = new Guid("A95664D2-9614-4F35-A746-DE8DB63617E6");
        int hr = Wasapi.CoCreateInstance(ref clsid, IntPtr.Zero, Wasapi.CLSCTX_ALL, ref iid, out IntPtr pEnum);
        if (hr < 0) return null;

        try
        {
            IntPtr pDevice = IntPtr.Zero;
            // role: 0=eConsole 1=eMultimedia 2=eCommunications
            hr = Com.Call(pEnum, 4, (IntPtr)flow, (IntPtr)1, (IntPtr)(&pDevice));
            if (hr < 0 || pDevice == IntPtr.Zero) return null;

            try
            {
                string id = GetDeviceId(pDevice) ?? "";
                string name = GetFriendlyName(pDevice) ?? id;
                return new AudioDeviceInfo(id, name, kind);
            }
            finally
            {
                Com.Release(pDevice);
            }
        }
        finally
        {
            Com.Release(pEnum);
        }
    }

    /// <summary>按设备 ID 打开 IMMDevice，调用方负责 Release。</summary>
    public static IntPtr OpenDevice(string deviceId)
    {
        Guid clsid = Wasapi.CLSID_MMDeviceEnumerator;
        Guid iid = new Guid("A95664D2-9614-4F35-A746-DE8DB63617E6");
        int hr = Wasapi.CoCreateInstance(ref clsid, IntPtr.Zero, Wasapi.CLSCTX_ALL, ref iid, out IntPtr pEnum);
        Com.Check(hr, "创建 IMMDeviceEnumerator");

        try
        {
            IntPtr pDevice = IntPtr.Zero;
            IntPtr pId = Marshal.StringToCoTaskMemUni(deviceId);
            try
            {
                hr = Com.Call(pEnum, 5, pId, (IntPtr)(&pDevice));
            }
            finally
            {
                Marshal.FreeCoTaskMem(pId);
            }
            Com.Check(hr, $"打开设备 {deviceId}");
            return pDevice;
        }
        finally
        {
            Com.Release(pEnum);
        }
    }

    /// <summary>取设备的混音格式（调用方负责 Marshal.FreeCoTaskMem）。</summary>
    public static IntPtr GetMixFormat(IntPtr pDevice)
    {
        IntPtr pClient = ActivateAudioClient(pDevice, IntPtr.Zero);
        try
        {
            IntPtr pFmt = IntPtr.Zero;
            // IAudioClient: 8 = GetMixFormat
            int hr = Com.Call(pClient, 8, (IntPtr)(&pFmt));
            Com.Check(hr, "GetMixFormat");
            return pFmt;
        }
        finally
        {
            Com.Release(pClient);
        }
    }

    /// <summary>在设备上激活 IAudioClient。</summary>
    public static IntPtr ActivateAudioClient(IntPtr pDevice, IntPtr activationParams)
    {
        Guid iid = Wasapi.IID_IAudioClient;
        IntPtr pClient = IntPtr.Zero;
        // IMMDevice: 3 Activate
        int hr = Com.Call(pDevice, 3, (IntPtr)(&iid), (IntPtr)Wasapi.CLSCTX_ALL, activationParams, (IntPtr)(&pClient));
        Com.Check(hr, "激活 IAudioClient");
        return pClient;
    }

    /// <summary>创建"进程级回环"捕获客户端：只捕获目标进程（及其子进程）渲染的声音。</summary>
    public static IntPtr ActivateProcessLoopback(uint processId, bool includeProcessTree)
    {
        var ap = new Wasapi.AUDIOCLIENT_ACTIVATION_PARAMS
        {
            ActivationType = Wasapi.AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK,
            TargetProcessId = processId,
            ProcessLoopbackMode = includeProcessTree
                ? Wasapi.PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE
                : Wasapi.PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE,
        };

        IntPtr pAp = Marshal.AllocHGlobal(Marshal.SizeOf<Wasapi.AUDIOCLIENT_ACTIVATION_PARAMS>());
        IntPtr pPv = Marshal.AllocHGlobal(Marshal.SizeOf<Wasapi.PropVariantBlob>());
        try
        {
            Marshal.StructureToPtr(ap, pAp, false);
            var pv = new Wasapi.PropVariantBlob
            {
                vt = 65, // VT_BLOB
                cbSize = Marshal.SizeOf<Wasapi.AUDIOCLIENT_ACTIVATION_PARAMS>(),
                pBlobData = pAp,
            };
            Marshal.StructureToPtr(pv, pPv, false);

            var handler = new ActivationHandler();
            Guid iid = Wasapi.IID_IAudioClient;
            IntPtr pOperation = IntPtr.Zero;
            int hr = Wasapi.ActivateAudioInterfaceAsync(
                Wasapi.VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK, ref iid, pPv, handler, out pOperation);
            Com.Check(hr, "ActivateAudioInterfaceAsync");

            // 异步操作对象由我们负责释放（早先直接丢弃了 out 参数，每次都漏一个 COM 对象）
            try
            {
                if (!handler.Done.Wait(TimeSpan.FromSeconds(5)))
                {
                    // 超时后回调仍可能到达，这里只清理"已经拿到的"接口
                    handler.ReleaseActivated();
                    throw new TimeoutException("进程级回环激活超时");
                }

                if (handler.HResult < 0)
                {
                    handler.ReleaseActivated();
                    Com.Check(handler.HResult, $"进程级回环激活 (PID={processId})");
                }

                if (handler.Activated == IntPtr.Zero)
                    throw new InvalidOperationException("进程级回环激活返回空接口");

                // 所有权移交给调用方
                return handler.TakeActivated();
            }
            finally
            {
                Com.Release(pOperation);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pPv);
            Marshal.FreeHGlobal(pAp);
        }
    }

    private static string? GetDeviceId(IntPtr pDevice)
    {
        IntPtr pStr = IntPtr.Zero;
        // IMMDevice: 5 GetId
        int hr = Com.Call(pDevice, 5, (IntPtr)(&pStr));
        if (hr < 0 || pStr == IntPtr.Zero) return null;
        try
        {
            return Marshal.PtrToStringUni(pStr);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pStr);
        }
    }

    private static string? GetFriendlyName(IntPtr pDevice)
    {
        IntPtr pStore = IntPtr.Zero;
        // IMMDevice: 4 OpenPropertyStore
        int hr = Com.Call(pDevice, 4, (IntPtr)0 /* STGM_READ */, (IntPtr)(&pStore));
        if (hr < 0 || pStore == IntPtr.Zero) return null;

        try
        {
            var key = new Wasapi.PROPERTYKEY
            {
                fmtid = Wasapi.PKEY_Device_FriendlyName_FmtId,
                pid = Wasapi.PKEY_Device_FriendlyName_Pid,
            };
            IntPtr pKey = Marshal.AllocHGlobal(Marshal.SizeOf<Wasapi.PROPERTYKEY>());
            IntPtr pVar = Marshal.AllocHGlobal(Marshal.SizeOf<Wasapi.PROPVARIANT>());
            try
            {
                Marshal.StructureToPtr(key, pKey, false);
                // IPropertyStore: 3 GetCount, 4 GetAt, 5 GetValue, 6 SetValue, 7 Commit
                hr = Com.Call(pStore, 5, pKey, pVar);
                if (hr < 0) return null;

                var pv = Marshal.PtrToStructure<Wasapi.PROPVARIANT>(pVar);
                try
                {
                    return pv.vt == 31 /* VT_LPWSTR */ && pv.p != IntPtr.Zero
                        ? Marshal.PtrToStringUni(pv.p)
                        : null;
                }
                finally
                {
                    Wasapi.PropVariantClear(pVar);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pVar);
                Marshal.FreeHGlobal(pKey);
            }
        }
        finally
        {
            Com.Release(pStore);
        }
    }
}

internal sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler
{
    public readonly ManualResetEventSlim Done = new(false);
    public int HResult = unchecked((int)0x80004005);
    public IntPtr Activated = IntPtr.Zero;

    /// <summary>调用方已放弃这次激活（超时/失败），回调需要自行释放接口。</summary>
    private int _abandoned;

    public int ActivateCompleted(IntPtr activateOperation)
    {
        try
        {
            // 这个 operation 的引用归调用方所有（见 ActivateProcessLoopback 的 Release），
            // 这里只借用，绝不 Release —— 否则会和调用方的 Release 重复，导致提前释放。
            var op = (IActivateAudioInterfaceAsyncOperation)Marshal.GetObjectForIUnknown(activateOperation);
            int hr = op.GetActivateResult(out int activateResult, out IntPtr pUnk);
            if (hr < 0)
            {
                HResult = hr;
                return hr;
            }
            HResult = activateResult;

            // 先发布，再检查是否已被放弃：
            // 反过来写会有一个窗口——ReleaseActivated 先取走（拿到空），
            // 随后这里才赋值，指针就没人释放了。
            Interlocked.Exchange(ref Activated, pUnk);

            // 超时后调用方已经放弃了这次激活：接口必须在这里就地释放，
            // 否则回调晚到就会把一个没人接管的引用永久泄漏。
            if (Volatile.Read(ref _abandoned))
            {
                var orphan = TakeActivated();
                if (orphan != IntPtr.Zero) Com.Release(orphan);
                return 0;
            }
        }
        catch (Exception ex)
        {
            HResult = ex.HResult;
        }
        finally
        {
            Done.Set();
        }
        return 0;
    }

    /// <summary>
    /// 取走激活出来的接口并把内部引用清零。
    /// 激活失败或超时的情况下，调用方不会来取，这里的指针必须自己释放。
    /// </summary>
    public IntPtr TakeActivated()
    {
        // 与回调线程竞争，必须原子取走
        return Interlocked.Exchange(ref Activated, IntPtr.Zero);
    }

    /// <summary>超时/失败后清理可能已经拿到的接口，避免泄漏。</summary>
    public void ReleaseActivated()
    {
        // 先标记放弃：此后回调再晚到，会自己释放而不是挂在这里
        Volatile.Write(ref _abandoned, true);
        var p = TakeActivated();
        if (p != IntPtr.Zero) Com.Release(p);
    }
}
