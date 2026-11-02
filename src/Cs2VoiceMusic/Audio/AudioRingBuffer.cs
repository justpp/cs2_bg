using System.Runtime.InteropServices;

namespace Cs2VoiceMusic.Audio;

/// <summary>
/// 单生产者/单消费者无锁环形缓冲，存放 32 位浮点、交错立体声帧。
///
/// 生产端 = 捕获线程（麦克风 / 进程回环），消费端 = 渲染线程。
/// 用绝对帧计数 + 掩码取模，避免读写指针回绕带来的歧义。
/// </summary>
public sealed class AudioRingBuffer
{
    private readonly float[] _data;      // 立体声交错：L R L R ...
    private readonly int _mask;          // 帧容量 - 1（容量必须是 2 的幂）
    private readonly int _capacityFrames;

    private int _writePos;               // 绝对写入帧号（单调递增）
    private int _readPos;                // 绝对读取帧号（单调递增）

    public AudioRingBuffer(int capacityFrames)
    {
        int cap = 1;
        while (cap < capacityFrames) cap <<= 1;

        _capacityFrames = cap;
        _mask = cap - 1;
        _data = new float[cap * 2];
    }

    public int CapacityFrames => _capacityFrames;

    /// <summary>当前可读帧数。</summary>
    public int AvailableFrames => Volatile.Read(ref _writePos) - Volatile.Read(ref _readPos);

    /// <summary>生产端：写入交错立体声帧，返回实际写入量（缓冲满时丢弃多余的）。</summary>
    public int Write(ReadOnlySpan<float> interleavedStereo)
    {
        int frames = interleavedStereo.Length / 2;
        int w = _writePos;
        int r = Volatile.Read(ref _readPos);
        int free = _capacityFrames - (w - r);
        if (frames > free) frames = free;
        if (frames <= 0) return 0;

        int idx = w & _mask;
        int firstChunk = Math.Min(frames, _capacityFrames - idx);
        interleavedStereo[..(firstChunk * 2)].CopyTo(_data.AsSpan(idx * 2));

        int remaining = frames - firstChunk;
        if (remaining > 0)
        {
            interleavedStereo.Slice(firstChunk * 2, remaining * 2).CopyTo(_data);
        }

        Volatile.Write(ref _writePos, w + frames);
        return frames;
    }

    /// <summary>消费端：读取帧数，不足时补静音。返回实际从缓冲取到的帧数。</summary>
    public int Read(Span<float> destination, bool padSilence = true)
    {
        int frames = destination.Length / 2;
        int r = _readPos;
        int w = Volatile.Read(ref _writePos);
        int take = Math.Min(frames, w - r);

        if (take > 0)
        {
            int idx = r & _mask;
            int firstChunk = Math.Min(take, _capacityFrames - idx);
            _data.AsSpan(idx * 2, firstChunk * 2).CopyTo(destination);

            int remaining = take - firstChunk;
            if (remaining > 0)
            {
                _data.AsSpan(0, remaining * 2).CopyTo(destination[(firstChunk * 2)..]);
            }

            Volatile.Write(ref _readPos, r + take);
        }

        if (padSilence && take < frames)
        {
            destination[(take * 2)..].Clear();
        }

        return take;
    }

    /// <summary>丢弃全部待读数据（用于重新同步）。</summary>
    public void Clear() => Volatile.Write(ref _readPos, Volatile.Read(ref _writePos));

    /// <summary>剩余可写帧数（生产端用来做背压判断）。</summary>
    public int FreeFrames => _capacityFrames - AvailableFrames;
}

/// <summary>把线程提升为 MMCSS "Pro Audio" 等级，降低音频线程被抢占的概率。</summary>
internal static class Mmcss
{
    [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AvSetMmThreadCharacteristicsW(string taskName, out uint taskIndex);

    [DllImport("avrt.dll", SetLastError = true)]
    private static extern bool AvRevertMmThreadCharacteristics(IntPtr avrtHandle);

    public static IntPtr Register(string task = "Pro Audio")
    {
        try
        {
            return AvSetMmThreadCharacteristicsW(task, out _);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    public static void Unregister(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        try { AvRevertMmThreadCharacteristics(handle); } catch { }
    }
}
