#nullable enable

// AgentCore.cs —— To-tode Studio Pro 的数据层：设置、会话、消息
//
// 这个 Agent 是照 DeepSeek Harness 的思路用 C# 重写的（官方那套是 Node/TS + Web UI，
// 这里是原生 WPF 实现，不依赖 Node，也不往 C 盘装任何东西）。
// 几点约定：
//   1) API 完全由用户自己填（BaseUrl + Key + Model），程序不内置任何密钥。
//   2) 会话按项目持久化，聊天记录永远保留（关面板、关项目、重启都还在）。
//   3) 所有文件操作都被 AgentTools 关在项目目录里（沙箱），越界一律拒绝。
//   4) 高危命令必须经用户确认才执行。

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace JavaStudio;

// ---------------------------------------------------------------- 思考过程

/// <summary>
/// 把模型输出里的「思考过程」和「真正要说的话」分开。
///
/// 为什么需要它：思考内容有两种来路 ——
///   1) 标准的 reasoning_content 字段（DeepSeek 的 R1 / reasoner 系列），本来就是分开的；
///   2) 有些模型不给那个字段，而是把思考用 &lt;think&gt;…&lt;think&gt; 标签包着塞进正文。
/// 第二种混在正文里就没法折叠了，所以这里统一切一刀：
/// 返回 (正文, 思考)，界面拿去分别渲染。
/// </summary>
public static class Think
{
    // 闭合标签写成 </think> 是规范，但不少模型偷懒写成第二个 <think>，
    // 甚至干脆不闭合（到串尾就当结束）。所以三种结尾都认。
    private static readonly Regex Block = new Regex(
        @"<\s*think\s*>(?<t>.*?)(?:<\s*/\s*think\s*>|<\s*think\s*>|$)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    public static (string body, string think) Split(string s)
    {
        if (string.IsNullOrEmpty(s)) return ("", "");
        // ⚠️ 这里只查 "think" 这个字样，不能查 "<think"：
        // 标签和尖括号之间可能有空格（< think >），查 "<think" 会把那种写法漏掉。
        // 正文里哪怕只是出现 "I think..." 这个词也无所谓 —— 正则匹配不上就原样返回。
        if (s.IndexOf("think", StringComparison.OrdinalIgnoreCase) < 0) return (s, "");

        var m = Block.Matches(s);
        if (m.Count == 0) return (s, "");

        var sb = new StringBuilder();
        foreach (Match x in m) sb.Append(x.Groups["t"].Value).Append('\n');

        // 挖掉思考块时补一个换行，免得前后两段正文被直接粘成一句
        string body = Block.Replace(s, "\n").Trim();
        return (body, sb.ToString().Trim());
    }
}

// ---------------------------------------------------------------- 消息

/// <summary>一条对话消息。Role 用 OpenAI 那套：user / assistant / tool / system。</summary>
public sealed class ChatMsg
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
    /// <summary>assistant 的思考过程。和 Content 分开存，界面上折叠显示。</summary>
    public string Reasoning { get; set; } = "";
    /// <summary>role=tool 时必填：对应哪次工具调用。</summary>
    public string ToolCallId { get; set; } = "";
    /// <summary>role=tool 时记一下工具名，界面上好看。</summary>
    public string ToolName { get; set; } = "";
    /// <summary>assistant 要求调用工具时填。</summary>
    public List<ToolCall>? ToolCalls { get; set; }
}

/// <summary>模型要求调用的一次工具。</summary>
public sealed class ToolCall
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>
    /// 原始 JSON 字符串（模型的 function.arguments）。
    ///
    /// ⚠️ 默认值必须是空串，**不能**是 "{}"。
    /// function.arguments 在流式响应里是一块一块下来的，客户端是按
    /// `ArgsJson += 分片` 累积的（见 AgentClient.cs）。初值要是 "{}"，
    /// 拼出来就成了 '{}{"path":"*"}'，前面那截空对象把整个 JSON 弄坏。
    /// 更坑的是报错信息会显示成「工具参数不是合法 JSON：{"path":"*"}」——
    /// 看着完全合法（前面的 {} 太小、太容易被看漏），查半天查不出所以然，
    /// 模型也不知道该改什么，就一遍遍重发同样的调用。
    /// 需要「没有参数就当成空对象」的兜底，由拿到完整串之后的调用方补
    /// （AgentClient 收尾时已经做了）。
    /// </summary>
    public string ArgsJson { get; set; } = "";
}

/// <summary>工具执行结果。</summary>
public sealed class ToolResult
{
    public string ToolCallId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Content { get; set; } = "";
    public bool IsError { get; set; }
}

// ---------------------------------------------------------------- 设置

/// <summary>
/// Agent 设置。存 &lt;数据目录&gt;/agent.json。
/// 默认指向 DeepSeek（OpenAI 兼容协议），但 BaseUrl 可改，
/// 所以接自建 / 兼容端点也行 —— 这是「用户自己填 API」的意思。
/// </summary>
public sealed class AgentSettings
{
    public string BaseUrl { get; set; } = "https://api.deepseek.com";
    public string ApiKey { get; set; } = "";
    /// <summary>
    /// 默认模型。接口自己报的可用名字是 deepseek-flash / deepseek-v4-pro；
    /// 用户要的「Pro」就是 deepseek-v4-pro。下拉里可改，也能手敲别的。
    /// </summary>
    public string Model { get; set; } = "deepseek-v4-pro";
    public double Temperature { get; set; } = 0.2;
    /// <summary>一次提问最多跑几轮工具调用，防止模型陷在死循环里烧 token。</summary>
    public int MaxToolRounds { get; set; } = 12;
    /// <summary>高危命令（删文件、强制推送等）要不要弹窗确认。默认开。</summary>
    public bool ConfirmHighRisk { get; set; } = true;

    private static string FilePath => Path.Combine(Core.AppDataDir, "agent.json");

    private static AgentSettings? _cur;

    public static AgentSettings Current => _cur ??= Load();

    public static AgentSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                string s = File.ReadAllText(FilePath, new UTF8Encoding(false));
                var v = JsonSerializer.Deserialize<AgentSettings>(s);
                if (v != null)
                {
                    // 迁移：早前把默认模型写成了接口不认的 deepseek-v4f，存过盘的机器
                    // 光改默认值救不了（文件里那个坏名字会被读回来），这里顺手改掉。
                    if (string.IsNullOrWhiteSpace(v.Model) || v.Model == "deepseek-v4f")
                        v.Model = "deepseek-v4-pro";
                    return v;
                }
            }
        }
        catch { /* 坏了就用默认值，不能因为设置读不出来就开不了工 */ }
        return new AgentSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Core.AppDataDir);
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch (Exception ex) { Core.Log("WARN", "Agent 设置没存上：" + ex.Message); }
    }

    /// <summary>能不能开工：地址和密钥都填了才算。</summary>
    public bool Ready => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(BaseUrl);

    /// <summary>聊天补全的地址。用户填的 BaseUrl 可能带 /v1 也可能不带，这里补齐。</summary>
    public string ChatUrl
    {
        get
        {
            string b = (BaseUrl ?? "").Trim().TrimEnd('/');
            if (b.Length == 0) return "";
            if (!b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) b += "/v1";
            return b + "/chat/completions";
        }
    }
}

// ---------------------------------------------------------------- 会话

/// <summary>
/// 一个项目的会话。消息列表落盘在 &lt;数据目录&gt;/agent-sessions/&lt;key&gt;.json，
/// key 用「目录名 + 路径哈希」，跟项目日志那套一样 —— 不同位置的同名项目不会串。
/// 关面板不清空，这就是「聊天记录永远保留」。
/// </summary>
public sealed class AgentSession
{
    public List<ChatMsg> Messages { get; } = new();

    private readonly string _file;
    private readonly string _root;

    private AgentSession(string root, string file)
    {
        _root = root;
        _file = file;
        Load();
    }

    private static readonly Dictionary<string, AgentSession> Cache = new();

    public static AgentSession For(string projectRoot)
    {
        if (string.IsNullOrEmpty(projectRoot)) projectRoot = "?";
        if (Cache.TryGetValue(projectRoot, out var s)) return s;
        s = new AgentSession(projectRoot, FilePathFor(projectRoot));
        Cache[projectRoot] = s;
        return s;
    }

    private static string FilePathFor(string root)
    {
        string dir = Path.Combine(Core.AppDataDir, "agent-sessions");
        Directory.CreateDirectory(dir);
        string key;
        try
        {
            string norm = Path.GetFullPath(root).TrimEnd('\\', '/').ToLowerInvariant();
            using var md5 = MD5.Create();
            key = Convert.ToHexString(md5.ComputeHash(Encoding.UTF8.GetBytes(norm)), 0, 6).ToLowerInvariant();
            string name = Path.GetFileName(norm);
            if (string.IsNullOrEmpty(name)) name = "project";
            foreach (var bad in Path.GetInvalidFileNameChars()) name = name.Replace(bad, '_');
            key = name + "-" + key;
        }
        catch { key = "session"; }
        return Path.Combine(dir, key + ".json");
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_file)) return;
            var list = JsonSerializer.Deserialize<List<ChatMsg>>(File.ReadAllText(_file, new UTF8Encoding(false)));
            if (list != null) Messages.AddRange(list);
        }
        catch { /* 读不出来就当新会话，别让历史文件把面板搞崩 */ }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file) ?? Core.AppDataDir);
            File.WriteAllText(_file,
                JsonSerializer.Serialize(Messages, new JsonSerializerOptions { WriteIndented = false }),
                new UTF8Encoding(false));
        }
        catch (Exception ex) { Core.Log("WARN", "会话没存上：" + ex.Message); }
    }

    public void Add(ChatMsg m) { Messages.Add(m); Save(); }

    /// <summary>清空对话（系统提示词保留）。</summary>
    public void Clear()
    {
        Messages.Clear();
        Save();
    }

    /// <summary>项目根目录 —— 工具沙箱的边界。</summary>
    public string Root => _root;
}
