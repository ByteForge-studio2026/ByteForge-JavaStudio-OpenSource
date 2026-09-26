// Config.cs —— 用户设置持久化（Configuration.json）
//
// 把「新建项目首选 JDK / 连接的本地 JDK / 深色还是浅色 / 中文还是英文」
// 写进 %APPDATA%/JavaStudio/Configuration.json。
// 启动时（App）先 Load，之后主题、语言、JDK 都从这里读；改动当场 Save。
// 第一次运行时没有这个文件，就从老的 settings.txt（Native 设置）里把
// theme / lang / extJava 搬过来当默认值，保证升级不丢配置。

using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace JavaStudio;

internal static class Config
{
    public static string FilePath => Path.Combine(Core.AppDataDir, "Configuration.json");

    /// <summary>新建项目时默认用的 JDK（主版本号字符串，如 "17"；空=用检测到的最后一个）。</summary>
    public static string PreferredJdk { get; internal set; } = "";

    /// <summary>用户手动连上的本地 JDK 目录（外部 Java）。</summary>
    public static string LocalJdk { get; internal set; } = "";

    /// <summary>"dark" / "light"。</summary>
    public static string Theme { get; internal set; } = "light";

    /// <summary>"zh" / "en"。</summary>
    public static string Language { get; internal set; } = "zh";

    /// <summary>
    /// md 拆分时编辑区是否在**右**边（预览在左）。
    /// 用户点过一次「交换左右」就记下来：本次会话里新开的 md 跟着走，
    /// 关掉软件再开也还是那样（存进 Configuration.json）。
    /// </summary>
    public static bool MdSwap { get; internal set; } = false;

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
                var r = doc.RootElement;
                PreferredJdk = Str(r, "preferredJdk");
                LocalJdk    = Str(r, "localJdk");
                Theme       = Str(r, "theme");
                Language    = Str(r, "language");
                MdSwap      = Str(r, "mdSwap") == "1";
            }
        }
        catch { /* 文件坏了就当没有，下面用老设置兜底 */ }

        // 文件里没给的字段，从老的 settings.txt 搬过来（升级兼容）
        if (string.IsNullOrEmpty(Theme))    Theme    = Core.Setting("theme") == "dark" ? "dark" : "light";
        if (string.IsNullOrEmpty(Language)) Language = Core.Setting("lang")  == Lang.EN  ? Lang.EN  : Lang.ZH;
        if (string.IsNullOrEmpty(LocalJdk)) LocalJdk = Core.Setting("extJava");
    }

    public static void Save()
    {
        try
        {
            var obj = new Dictionary<string, string>
            {
                ["preferredJdk"] = PreferredJdk,
                ["localJdk"]     = LocalJdk,
                ["theme"]        = Theme,
                ["language"]     = Language,
                ["mdSwap"]       = MdSwap ? "1" : "0",
            };
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 写盘失败别崩，顶多这次设置没存住 */ }
    }

    private static string Str(JsonElement e, string n)
        => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
}
