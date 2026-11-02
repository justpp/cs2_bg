using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Cs2VoiceMusic.Config;

/// <summary>
/// 可绑定的动作。刻意保持精简：一个开关音乐，四个"播放指定音乐"槽位。
/// </summary>
public enum HotkeyAction
{
    /// <summary>开 / 关音乐注入（队友能否听到音乐）。</summary>
    ToggleMusicInjection = 0,

    /// <summary>播放槽位 1 指定的音乐。</summary>
    PlaySlot1 = 1,

    /// <summary>播放槽位 2 指定的音乐。</summary>
    PlaySlot2 = 2,

    /// <summary>播放槽位 3 指定的音乐。</summary>
    PlaySlot3 = 3,

    /// <summary>播放槽位 4 指定的音乐。</summary>
    PlaySlot4 = 4,
}

/// <summary>
/// 一个"音乐槽位"：绑定若干本地音频文件，按一次热键就放它。
///
/// 为什么用本地文件而不是去操作 QQ 音乐：系统媒体接口（SMTC）只能做
/// 播放/暂停/上下一首，没法"播放某一首指定的歌"。要精确控制放什么，
/// 只有自己解码播放这一条路可靠。
/// </summary>
public sealed class MusicSlot
{
    /// <summary>槽位序号（1~4）。</summary>
    public int Index { get; set; }

    /// <summary>显示名，例如"开场曲"。空则显示为"槽位 N"。</summary>
    public string Name { get; set; } = "";

    /// <summary>该槽位的曲目列表，按顺序播放。</summary>
    public List<string> Files { get; set; } = new();

    /// <summary>
    /// 放完最后一首后是否从头循环。默认开启：
    /// 槽位是"按一下就一直放"的用法，放完一首就静音会让用户以为程序坏了。
    /// </summary>
    public bool Loop { get; set; } = true;

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"槽位 {Index}" : Name;

    /// <summary>
    /// 统计当前不可用的文件（不存在或暂时访问不到），但**不删除**。
    ///
    /// 早先的版本在加载配置时直接 RemoveAll 掉不存在的路径，
    /// 结果外接硬盘没插、网络盘掉线时用户的整个槽位会被永久清空。
    /// 现在只报告，由界面提示，用户自己决定要不要清理。
    /// </summary>
    public List<string> FindMissingFiles()
    {
        var missing = new List<string>();
        foreach (var f in Files)
        {
            try
            {
                if (!File.Exists(f)) missing.Add(f);
            }
            catch
            {
                missing.Add(f);
            }
        }
        return missing;
    }
}

/// <summary>热键绑定：修饰键 + 虚拟键码。</summary>
public sealed class HotkeyBinding
{
    public HotkeyAction Action { get; set; }

    /// <summary>Win32 修饰键位掩码（MOD_ALT=1, MOD_CONTROL=2, MOD_SHIFT=4, MOD_WIN=8）。</summary>
    public uint Modifiers { get; set; }

    /// <summary>虚拟键码（VK_*）。</summary>
    public uint VirtualKey { get; set; }

    [JsonIgnore]
    public bool IsBound => VirtualKey != 0;

    public HotkeyBinding Clone() => new()
    {
        Action = Action,
        Modifiers = Modifiers,
        VirtualKey = VirtualKey,
    };

    public override string ToString()
    {
        if (!IsBound) return "未绑定";

        var parts = new List<string>();
        if ((Modifiers & 2) != 0) parts.Add("Ctrl");
        if ((Modifiers & 1) != 0) parts.Add("Alt");
        if ((Modifiers & 4) != 0) parts.Add("Shift");
        if ((Modifiers & 8) != 0) parts.Add("Win");
        parts.Add(KeyNames.NameOf(VirtualKey));
        return string.Join(" + ", parts);
    }
}

public static class KeyNames
{
    private static readonly Dictionary<uint, string> Map = new()
    {
        [0x08] = "Backspace",
        [0x09] = "Tab",
        [0x0D] = "Enter",
        [0x13] = "Pause",
        [0x14] = "CapsLock",
        [0x1B] = "Esc",
        [0x20] = "Space",
        [0x21] = "PageUp",
        [0x22] = "PageDown",
        [0x23] = "End",
        [0x24] = "Home",
        [0x25] = "←",
        [0x26] = "↑",
        [0x27] = "→",
        [0x28] = "↓",
        [0x2C] = "PrintScreen",
        [0x2D] = "Insert",
        [0x2E] = "Delete",
        [0x5B] = "LWin",
        [0x5C] = "RWin",
        [0x90] = "NumLock",
        [0x91] = "ScrollLock",
        [0xBA] = ";",
        [0xBB] = "=",
        [0xBC] = ",",
        [0xBD] = "-",
        [0xBE] = ".",
        [0xBF] = "/",
        [0xC0] = "`",
        [0xDB] = "[",
        [0xDC] = "\\",
        [0xDD] = "]",
        [0xDE] = "'",
        [0x6A] = "Num*",
        [0x6B] = "Num+",
        [0x6D] = "Num-",
        [0x6E] = "Num.",
        [0x6F] = "Num/",
    };

    static KeyNames()
    {
        for (uint vk = 0x30; vk <= 0x39; vk++) Map[vk] = ((char)vk).ToString();
        for (uint vk = 0x41; vk <= 0x5A; vk++) Map[vk] = ((char)vk).ToString();
        for (uint vk = 0x70; vk <= 0x87; vk++) Map[vk] = $"F{vk - 0x6F}";
        for (uint vk = 0x60; vk <= 0x69; vk++) Map[vk] = $"Num{vk - 0x60}";
    }

    public static string NameOf(uint vk) => Map.TryGetValue(vk, out var n) ? n : $"VK{vk:X2}";
}

/// <summary>应用配置，持久化到 %APPDATA%\Cs2VoiceMusic\config.json。</summary>
public sealed class AppConfig
{
    /// <summary>
    /// 配置版本。2 = 精简为「开关音乐 + 4 个槽位」的热键模型；
    /// 3 = MusicSlot.Loop 开始真正生效。
    /// 升级时旧版本的热键会被重置，但设备与槽位设置保留。
    /// </summary>
    public const int ConfigVersion = 3;

    public int Version { get; set; } = ConfigVersion;

    /// <summary>输出设备（虚拟声卡）ID。空 = 使用默认播放设备。</summary>
    public string OutputDeviceId { get; set; } = "";

    /// <summary>麦克风设备 ID。空 = 不使用麦克风直通。</summary>
    public string MicDeviceId { get; set; } = "";

    /// <summary>QQ 音乐进程名候选，按顺序匹配。</summary>
    public List<string> MusicProcessNames { get; set; } = new() { "QQMusic", "QQMusicMini", "qqmusic" };

    /// <summary>音乐音量（0..1）。</summary>
    public float MusicGain { get; set; } = 0.6f;

    /// <summary>麦克风音量（0..1）。</summary>
    public float MicGain { get; set; } = 1.0f;

    /// <summary>启动时是否自动开始混音。</summary>
    public bool AutoStartOnLaunch { get; set; } = true;

    /// <summary>启动时音乐注入默认开启。</summary>
    public bool MusicInjectionDefault { get; set; } = true;

    /// <summary>是否启用按键提示音。</summary>
    public bool EnableFeedbackBeep { get; set; } = false;

    /// <summary>热键绑定。只包含开关音乐与 4 个槽位。</summary>
    public List<HotkeyBinding> Hotkeys { get; set; } = new();

    /// <summary>4 个音乐槽位。</summary>
    public List<MusicSlot> Slots { get; set; } = new();

    [JsonIgnore]
    public string ConfigPath { get; private set; } = "";

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cs2VoiceMusic");

    public static AppConfig Load()
    {
        string dir = DefaultDirectory;
        string path = Path.Combine(dir, "config.json");
        AppConfig cfg;

        try
        {
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                json = Migrate(json);
                cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? CreateDefault();
            }
            else
            {
                cfg = CreateDefault();
            }
        }
        catch
        {
            cfg = CreateDefault();
        }

        cfg.ConfigPath = path;
        cfg.Version = ConfigVersion;
        cfg.Normalize();
        return cfg;
    }

    /// <summary>
    /// 旧版本升级：v1 的热键枚举与本版语义不同，直接反序列化会因未知枚举值报错，
    /// 所以解析前先把热键摘掉，让它回落到默认值。
    ///
    /// 但槽位（Slots）的语义没变，必须保留：早先这里连 Slots 一起删了，
    /// 而首次启动又会把配置落盘，等于用户一升级，绑好的音乐就被永久清空。
    /// </summary>
    private static string Migrate(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            if (node is not JsonObject obj) return json;

            int version = 0;
            if (obj.TryGetPropertyValue("Version", out var v) && v is not null)
            {
                int.TryParse(v.ToJsonString(), out version);
            }

            if (version < 2)
            {
                // 只摘掉无法反序列化的热键；Slots 的结构与 v1 兼容，保留。
                // 早先连 Slots 一起删，而首次启动又会立刻落盘，等于升级一次就把
                // 用户绑好的音乐永久清空。
                obj.Remove("Hotkeys");
            }

            if (version < 3)
            {
                // v2 里 MusicSlot.Loop 是死字段，UI 也没暴露它，所以用户配置里的
                // "Loop": false 多半只是默认序列化的结果，并非用户意图。
                // 这个版本起它开始真正生效，需要把这些槽位重置为"循环"，
                // 否则老用户会突然遇到"放完一首就停"。
                if (obj.TryGetPropertyValue("Slots", out var slotsNode) && slotsNode is JsonArray slots)
                {
                    foreach (var item in slots)
                    {
                        if (item is JsonObject slotObj) slotObj["Loop"] = true;
                    }
                }
            }

            return obj.ToJsonString();
        }
        catch
        {
            return json;
        }
    }

    public void Save()
    {
        try
        {
            string dir = DefaultDirectory;
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "config.json");
            string json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(path, json);
            ConfigPath = path;
        }
        catch (Exception ex)
        {
            throw new IOException($"保存配置失败: {ex.Message}", ex);
        }
    }

    private void Normalize()
    {
        MusicGain = Math.Clamp(MusicGain, 0f, 1f);
        MicGain = Math.Clamp(MicGain, 0f, 1f);

        // 槽位固定 4 个，补齐缺失项
        for (int i = 1; i <= 4; i++)
        {
            if (!Slots.Any(s => s.Index == i))
            {
                Slots.Add(new MusicSlot { Index = i });
            }
        }

        var seenSlots = new HashSet<int>();
        Slots.RemoveAll(s => !seenSlots.Add(s.Index));
        Slots.Sort((a, b) => a.Index.CompareTo(b.Index));

        // 热键：补齐缺失动作
        foreach (HotkeyAction action in Enum.GetValues<HotkeyAction>())
        {
            if (!Hotkeys.Any(h => h.Action == action))
            {
                Hotkeys.Add(new HotkeyBinding { Action = action });
            }
        }

        // 去掉重复动作，并按枚举顺序排列
        var seen = new HashSet<HotkeyAction>();
        Hotkeys.RemoveAll(h => !seen.Add(h.Action));
        Hotkeys.Sort((a, b) => a.Action.CompareTo(b.Action));
    }

    /// <summary>默认热键：Ctrl+Alt+M 开关音乐，Ctrl+Alt+1~4 播放对应槽位。</summary>
    public static AppConfig CreateDefault()
    {
        const uint CtrlAlt = 2 | 1;
        return new AppConfig
        {
            Hotkeys = new List<HotkeyBinding>
            {
                new() { Action = HotkeyAction.ToggleMusicInjection, Modifiers = CtrlAlt, VirtualKey = 0x4D }, // M
                new() { Action = HotkeyAction.PlaySlot1,            Modifiers = CtrlAlt, VirtualKey = 0x31 }, // 1
                new() { Action = HotkeyAction.PlaySlot2,            Modifiers = CtrlAlt, VirtualKey = 0x32 }, // 2
                new() { Action = HotkeyAction.PlaySlot3,            Modifiers = CtrlAlt, VirtualKey = 0x33 }, // 3
                new() { Action = HotkeyAction.PlaySlot4,            Modifiers = CtrlAlt, VirtualKey = 0x34 }, // 4
            },
            Slots = new List<MusicSlot>
            {
                new() { Index = 1 },
                new() { Index = 2 },
                new() { Index = 3 },
                new() { Index = 4 },
            },
        };
    }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };
}
