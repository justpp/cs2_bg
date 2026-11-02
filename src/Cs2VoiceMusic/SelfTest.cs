using System.Diagnostics;
using System.Runtime.InteropServices;
using Cs2VoiceMusic.Audio;
using Cs2VoiceMusic.Config;

namespace Cs2VoiceMusic;

/// <summary>
/// 命令行自检：不启动界面，直接验证「进程回环 → 混音 → 渲染」整条链路。
/// 用法：
///   Cs2VoiceMusic.exe --selftest            列出设备与进程
///   Cs2VoiceMusic.exe --selftest --run      跑 5 秒真实混音并报告电平
///   Cs2VoiceMusic.exe --selftest --run 10   跑 10 秒
/// </summary>
public static class SelfTest
{
    public static int Run(string[] args)
    {
        AttachConsole(-1);
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* 无控制台时忽略 */ }

        // 同时写一份到文件：从资源管理器双击、或被其他进程启动时，
        // 控制台输出可能拿不到，文件是唯一可靠的出口。
        string reportPath = Path.Combine(AppConfig.DefaultDirectory, "selftest.txt");
        try
        {
            Directory.CreateDirectory(AppConfig.DefaultDirectory);
            var tee = new TeeWriter(Console.Out, new StreamWriter(reportPath, false, System.Text.Encoding.UTF8)
            {
                AutoFlush = true,
            });
            Console.SetOut(tee);
        }
        catch { }

        Console.WriteLine();
        Console.WriteLine("========== CS2 语音放歌助手 · 自检 ==========");
        Console.WriteLine($"系统: {Environment.OSVersion}  ·  {(Environment.Is64BitProcess ? "64 位" : "32 位")}");
        Console.WriteLine();

        // 1) 设备
        Console.WriteLine("[1] 音频设备");
        var outputs = AudioDeviceService.Enumerate(DeviceKind.Playback);
        var mics = AudioDeviceService.Enumerate(DeviceKind.Recording);

        Console.WriteLine($"  播放设备 {outputs.Count} 个：");
        foreach (var d in outputs) Console.WriteLine($"    - {d.Name}");
        Console.WriteLine($"  录音设备 {mics.Count} 个：");
        foreach (var d in mics) Console.WriteLine($"    - {d.Name}");

        var defOut = AudioDeviceService.GetDefault(DeviceKind.Playback);
        Console.WriteLine($"  默认播放: {defOut?.Name ?? "(无)"}");
        Console.WriteLine();

        // 2) 音频会话
        Console.WriteLine("[2] 正在出声的进程");
        var sessions = AudioSessionProbe.EnumerateSessions();
        if (sessions.Count == 0)
        {
            Console.WriteLine("  （没有活动音频会话）");
        }
        foreach (var session in sessions)
        {
            Console.WriteLine($"  PID {session.ProcessId,6}  {session.ProcessName,-24} {(session.IsActive ? "发声" : "空闲")}");
        }
        Console.WriteLine();

        // 3) QQ 音乐
        var cfg = AppConfig.Load();
        Console.WriteLine("[3] QQ 音乐进程");
        var pids = AudioSessionProbe.FindProcessIds(cfg.MusicProcessNames.ToArray());
        Console.WriteLine(pids.Count > 0
            ? $"  找到: {string.Join(", ", pids)}"
            : "  未找到（QQ 音乐没启动？）");
        Console.WriteLine();

        // --loopback <录音设备名片段>：把渲染结果从指定设备的录音端抓回来，
        // 用于验证"混音 → 虚拟声卡 → 被 CS2 当成麦克风读到"这条完整链路。
        string? verifyDevice = null;
        int vi = Array.IndexOf(args, "--loopback");
        if (vi >= 0 && vi + 1 < args.Length) verifyDevice = args[vi + 1];

        if (!args.Contains("--run"))
        {
            Console.WriteLine("提示：加 --run 参数可实测音频通路（5 秒）。");
            Console.WriteLine("==============================================");
            return 0;
        }

        // 4) 实际混音
        int seconds = 5;
        int runIdx = Array.IndexOf(args, "--run");
        if (runIdx >= 0 && runIdx + 1 < args.Length && int.TryParse(args[runIdx + 1], out int parsed))
            seconds = Math.Clamp(parsed, 2, 60);

        Console.WriteLine($"[4] 实测混音 {seconds} 秒");

        string outputId = string.IsNullOrEmpty(cfg.OutputDeviceId)
            ? defOut?.Id ?? ""
            : cfg.OutputDeviceId;
        string? micId = string.IsNullOrEmpty(cfg.MicDeviceId) ? null : cfg.MicDeviceId;

        if (string.IsNullOrEmpty(outputId))
        {
            Console.WriteLine("  失败：没有可用的输出设备");
            return 1;
        }

        uint? musicPid = pids.Count > 0 ? pids[0] : null;

        // --music-pid <pid>：强制指定音乐源进程，用于受控测试
        int mpi = Array.IndexOf(args, "--music-pid");
        if (mpi >= 0 && mpi + 1 < args.Length && uint.TryParse(args[mpi + 1], out uint forced))
            musicPid = forced;

        // --mic <设备名片段>：强制指定麦克风设备
        int mi = Array.IndexOf(args, "--mic");
        if (mi >= 0 && mi + 1 < args.Length)
        {
            var mdev = mics.FirstOrDefault(m =>
                m.Name.Contains(args[mi + 1], StringComparison.OrdinalIgnoreCase));
            if (mdev is not null)
            {
                micId = mdev.Id;
                Console.WriteLine($"  已指定麦克风: {mdev.Name}");
            }
        }

        // --output <设备名片段>：强制指定输出设备
        int oi = Array.IndexOf(args, "--output");
        if (oi >= 0 && oi + 1 < args.Length)
        {
            var dev = outputs.FirstOrDefault(o =>
                o.Name.Contains(args[oi + 1], StringComparison.OrdinalIgnoreCase));
            if (dev is not null)
            {
                outputId = dev.Id;
                Console.WriteLine($"  已指定输出设备: {dev.Name}");
            }
        }

        // --local <文件路径>：用本地音乐文件作为音源（验证 NAudio 解码 + 混音）
        int li = Array.IndexOf(args, "--local");
        string? localFile = li >= 0 && li + 1 < args.Length ? args[li + 1] : null;

        // --slot N：按配置里第 N 个槽位播放，验证"槽位 → 出声"这条路径
        int si = Array.IndexOf(args, "--slot");
        if (si >= 0 && si + 1 < args.Length && int.TryParse(args[si + 1], out int slotNo))
        {
            var slot = cfg.Slots.FirstOrDefault(x => x.Index == slotNo);
            if (slot is null || slot.Files.Count == 0)
            {
                Console.WriteLine($"  槽位 {slotNo} 没有绑定音乐");
            }
            else
            {
                localFile = slot.Files[0];
                Console.WriteLine($"  使用槽位 {slotNo}「{slot.DisplayName}」: {Path.GetFileName(localFile)}");
            }
        }

        // 可选的旁路验证：从虚拟声卡录音端抓回渲染结果
        AudioCaptureSource? verifier = null;
        if (verifyDevice is not null)
        {
            var target = mics.FirstOrDefault(m =>
                m.Name.Contains(verifyDevice, StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                Console.WriteLine($"  找不到名字包含 \"{verifyDevice}\" 的录音设备，跳过旁路验证");
            }
            else
            {
                try
                {
                    verifier = new AudioCaptureSource("旁路验证", 48000, 2);
                    verifier.OpenMicrophone(target.Id);
                    verifier.Initialize(isLoopback: false);
                    verifier.Start();
                    Console.WriteLine($"  旁路验证: 从「{target.Name}」抓回输出");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  旁路验证启动失败: {ex.Message}");
                    verifier = null;
                }
            }
        }

        using var engine = new AudioMixerEngine();
        try
        {
            engine.Start(outputId, micId, localFile is null ? musicPid : null);

            // 音乐源挂接失败不再让整体启动失败，这里要把原因打出来，
            // 否则自检只会显示"没有捕获到音乐声音"，看不出是挂接失败还是没在播放。
            if (engine.LastError is not null)
                Console.WriteLine($"  注意: {engine.LastError}");

            if (localFile is not null)
            {
                engine.EnableLocalMusic(new[] { localFile }, loop: false);
                Console.WriteLine($"  本地音乐: {localFile}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  启动失败: {ex.Message}");
            return 1;
        }

        Console.WriteLine($"  输出设备: {outputId}");
        Console.WriteLine($"  麦克风:   {micId ?? "(未使用)"}");
        Console.WriteLine($"  音乐 PID: {musicPid?.ToString() ?? "(无)"}");
        Console.WriteLine();

        var sw = Stopwatch.StartNew();
        float maxMusic = 0, maxMic = 0, maxVerify = 0;
        long lastFrames = 0;

        while (sw.Elapsed.TotalSeconds < seconds)
        {
            Thread.Sleep(500);
            maxMusic = Math.Max(maxMusic, engine.MusicPeak);
            maxMic = Math.Max(maxMic, engine.MicPeak);
            if (verifier is not null) maxVerify = Math.Max(maxVerify, verifier.PeakLevel);

            long now = engine.FramesRendered;
            double fps = (now - lastFrames) * 2.0;
            lastFrames = now;

            Console.WriteLine(
                $"  t={sw.Elapsed.TotalSeconds,5:F1}s  渲染 {fps,7:F0} 帧/秒  " +
                $"音乐 {engine.MusicPeak * 100,5:F1}%  麦克风 {engine.MicPeak * 100,5:F1}%");
        }

        engine.Stop();
        verifier?.Dispose();

        Console.WriteLine();
        Console.WriteLine("  结果：");
        Console.WriteLine($"    音乐峰值:   {maxMusic * 100:F1}%  {(maxMusic > 0.001f ? "✔ 有声音" : "✘ 无声音")}");
        Console.WriteLine($"    麦克风峰值: {maxMic * 100:F1}%  {(maxMic > 0.001f ? "✔ 有声音" : "✘ 无声音")}");
        if (verifier is not null)
        {
            Console.WriteLine($"    虚拟声卡回读峰值: {maxVerify * 100:F1}%  {(maxVerify > 0.001f ? "✔ 输出已到达虚拟声卡" : "✘ 虚拟声卡没有收到声音")}");
        }
        Console.WriteLine($"    音乐欠载:   {engine.MusicUnderruns} 帧   丢弃: {engine.MusicOverruns} 帧");
        Console.WriteLine($"    麦克风欠载: {engine.MicUnderruns} 帧   丢弃: {engine.MicOverruns} 帧");

        if (engine.LastError is not null)
            Console.WriteLine($"    错误: {engine.LastError}");

        bool ok = maxMusic > 0.001f;
        Console.WriteLine(ok
            ? "  ✔ 音频通路正常：QQ 音乐的声音已经能被捕获并混音输出。"
            : "  ✘ 没有捕获到音乐声音，请确认 QQ 音乐正在播放。");

        Console.WriteLine("==============================================");
        return ok ? 0 : 2;
    }

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    /// <summary>把输出同时写到控制台与文件。</summary>
    private sealed class TeeWriter : TextWriter
    {
        private readonly TextWriter _a;
        private readonly TextWriter _b;

        public TeeWriter(TextWriter a, TextWriter b)
        {
            _a = a;
            _b = b;
        }

        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override void Write(char value)
        {
            try { _a.Write(value); } catch { }
            try { _b.Write(value); } catch { }
        }

        public override void Write(string? value)
        {
            try { _a.Write(value); } catch { }
            try { _b.Write(value); } catch { }
        }

        public override void WriteLine(string? value)
        {
            try { _a.WriteLine(value); } catch { }
            try { _b.WriteLine(value); } catch { }
        }

        public override void Flush()
        {
            try { _a.Flush(); } catch { }
            try { _b.Flush(); } catch { }
        }
    }
}
