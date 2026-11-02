using System.Runtime.InteropServices;

namespace Cs2VoiceMusic.Audio;

/// <summary>
/// 手工 COM 调用层。
///
/// 为什么不用 [ComImport]：进程级回环激活返回的 IAudioClient 是聚合对象，
/// 托管 RCW 在强类型转换时会退化成 __ComObject 并抛 NotImplementedException。
/// 直接按 vtable 槽位调用函数指针可以稳定工作，也避免了每次调用都要封送。
///
/// 槽位约定：0=QueryInterface 1=AddRef 2=Release，业务方法从 3 开始。
/// </summary>
internal static unsafe class Com
{
    public static int Call(IntPtr pUnk, int slot,
        IntPtr a1 = default, IntPtr a2 = default, IntPtr a3 = default,
        IntPtr a4 = default, IntPtr a5 = default, IntPtr a6 = default)
    {
        IntPtr vtbl = Marshal.ReadIntPtr(pUnk);
        IntPtr fn = Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size);
        var f = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, int>)fn;
        return f(pUnk, a1, a2, a3, a4, a5, a6);
    }

    public static void Release(IntPtr pUnk)
    {
        if (pUnk == IntPtr.Zero) return;
        Call(pUnk, 2);
    }

    public static void Check(int hr, string what)
    {
        if (hr < 0) throw new COMException($"{what} 失败 (0x{hr:X8})", hr);
    }
}

internal static class Wasapi
{
    // 设备数据流方向
    public const int eRender = 0;
    public const int eCapture = 1;

    // 设备状态
    public const int DEVICE_STATE_ACTIVE = 0x00000001;

    // 共享模式 / 流标志
    public const int AUDCLNT_SHAREMODE_SHARED = 0;
    public const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    public const int AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    public const int AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = unchecked((int)0x80000000);
    public const int AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;

    // 缓冲区标志
    public const int AUDCLNT_BUFFERFLAGS_SILENT = 0x2;

    public const int CLSCTX_ALL = 0x17;

    // 进程级回环的虚拟设备路径（audioclientactivationparams.h 中的权威定义）
    public const string VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK = @"VAD\Process_Loopback";
    public const int AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK = 1;
    public const int PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE = 0;
    public const int PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE = 1;

    public const ushort WAVE_FORMAT_PCM = 1;

    public static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    public static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    public static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
    public static readonly Guid IID_IAudioRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
    public static readonly Guid IID_IAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    public static readonly Guid IID_IAudioSessionControl2 = new("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D");

    // PKEY_Device_FriendlyName
    public static readonly Guid PKEY_Device_FriendlyName_FmtId = new("A45C254E-DF1C-4EFD-8020-67D146A850E0");
    public const uint PKEY_Device_FriendlyName_Pid = 14;

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;

        public static WAVEFORMATEX Pcm16(int sampleRate, int channels) => new()
        {
            wFormatTag = WAVE_FORMAT_PCM,
            nChannels = (ushort)channels,
            nSamplesPerSec = (uint)sampleRate,
            wBitsPerSample = 16,
            nBlockAlign = (ushort)(channels * 2),
            nAvgBytesPerSec = (uint)(sampleRate * channels * 2),
            cbSize = 0,
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROPVARIANT
    {
        public ushort vt;
        public ushort r1, r2, r3;
        public IntPtr p;      // 指针型载荷
        public IntPtr p2;     // 大结构型载荷占位
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AUDIOCLIENT_ACTIVATION_PARAMS
    {
        public int ActivationType;
        // 注意：C 头文件里联合体成员顺序是 TargetProcessId 在前、ProcessLoopbackMode 在后
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PropVariantBlob
    {
        public ushort vt;
        public ushort r1, r2, r3;
        public int cbSize;
        public IntPtr pBlobData;
    }

    [DllImport("ole32.dll")]
    public static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, int dwClsContext,
        ref Guid riid, out IntPtr ppv);

    [DllImport("ole32.dll")]
    public static extern int PropVariantClear(IntPtr pvar);

    [DllImport("mmdevapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IntPtr operation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateEventW(IntPtr attrs, bool manualReset, bool initialState, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr handle, uint ms);

    public const uint WAIT_OBJECT_0 = 0;
}

[ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceCompletionHandler
{
    [PreserveSig] int ActivateCompleted(IntPtr activateOperation);
}

[ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceAsyncOperation
{
    [PreserveSig] int GetActivateResult(out int activateResult, out IntPtr activatedInterface);
}
