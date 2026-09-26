#nullable enable

// AgentTools.cs —— To-tode Studio Pro 的工具集 + 项目沙箱
//
// 两条硬规矩，写在文件头是因为改工具的人最容易顺手破坏它们：
//
//   1) 沙箱：任何路径都先过 TryResolve()，解析出来的绝对路径必须落在项目根目录里，
//      越界一律拒绝（不让模型去读 ../ 甚至 C:\）。
//   2) 高危：run_command / delete_file 一律走 Confirm 钩子，用户点头才执行。
//
// 工具协议是 OpenAI 的 function calling 格式（DeepSeek 兼容这套）。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JavaStudio;

public sealed class AgentTools
{
    private readonly string _rootFull;

    /// <summary>
    /// 高危操作确认钩子：(标题, 详情) => 是否放行。
    /// 由界面注入；工具跑在后台线程上，所以注入的实现自己要切回 UI 线程再弹窗。
    /// </summary>
    public Func<string, string, bool>? Confirm { get; set; }

    public AgentTools(string projectRoot)
    {
        try { _rootFull = Path.GetFullPath(projectRoot ?? "").TrimEnd('\\', '/'); }
        catch { _rootFull = projectRoot ?? ""; }
    }

    public string Root => _rootFull;

    // ---------------------------------------------------------------- 沙箱

    /// <summary>把模型给的路径解析成项目内的绝对路径。越界返回 false。</summary>
    private bool TryResolve(string rel, out string full, out string err)
    {
        full = ""; err = "";
        if (string.IsNullOrWhiteSpace(rel)) { err = "路径为空"; return false; }
        try
        {
            string p = rel.Replace('/', '\\');
            full = Path.IsPathRooted(p)
                ? Path.GetFullPath(p)
                : Path.GetFullPath(Path.Combine(_rootFull, p));
        }
        catch (Exception ex) { err = "路径不合法：" + ex.Message; return false; }

        bool inside = full.Equals(_rootFull, StringComparison.OrdinalIgnoreCase)
                   || full.StartsWith(_rootFull + "\\", StringComparison.OrdinalIgnoreCase);
        if (!inside)
        {
            err = $"越界：Agent 只能在项目目录内操作（{_rootFull}），拒绝访问 {full}";
            return false;
        }
        return true;
    }

    private string Rel(string full)
        => full.Length > _rootFull.Length ? full.Substring(_rootFull.Length + 1) : full;

    // 目录遍历要跳过的噪音目录
    private static readonly string[] SkipDirs = { ".git", "out", "bin", "build", "target", "node_modules", ".javastudio", ".idea", ".vs" };

    private static bool IsSkipped(string dir)
    {
        string n = Path.GetFileName(dir);
        return n.Length > 0 && SkipDirs.Contains(n, StringComparer.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- 风险判定

    /// <summary>这个工具调用算不算高危（要用户确认）。Shell 什么都能干，所以一律高危。</summary>
    public static bool IsHighRisk(string name)
        => name is "run_command" or "delete_file";

    /// <summary>Shell 命令里有没有明显作死的写法，界面上标红用。</summary>
    public static bool LooksDangerous(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        string c = command.ToLowerInvariant();
        string[] bad =
        {
            "rm -rf", "rm -fr", "del /f", "del /s", "format ", "rd /s", "rmdir /s",
            "git push -f", "git push --force", "git reset --hard", "git clean -f",
            "shutdown", "taskkill", "mkfs", ":(){", "curl | sh", "wget | sh",
        };
        return bad.Any(c.Contains);
    }

    // ---------------------------------------------------------------- 工具定义

    public static JsonArray Definitions()
    {
        JsonObject Fn(string name, string desc, JsonObject props, JsonArray required)
            => new()
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = name,
                    ["description"] = desc,
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = props,
                        ["required"] = required,
                    }
                }
            };

        return new JsonArray
        {
            Fn("read_file", "读取项目内一个文件的内容（相对项目根目录的路径）。",
               new JsonObject { ["path"] = new JsonObject { ["type"] = "string", ["description"] = "相对项目根目录的路径，如 src/Main.java" } },
               new JsonArray { "path" }),

            Fn("write_file", "把内容完整写入项目内一个文件（会覆盖原文件）。",
               new JsonObject {
                   ["path"] = new JsonObject { ["type"] = "string" },
                   ["content"] = new JsonObject { ["type"] = "string", ["description"] = "文件全文" } },
               new JsonArray { "path", "content" }),

            Fn("str_replace", "把文件里某段文字替换成另一段（old 必须在文件里唯一存在）。",
               new JsonObject {
                   ["path"] = new JsonObject { ["type"] = "string" },
                   ["old"] = new JsonObject { ["type"] = "string", ["description"] = "要被替换的原文" },
                   ["new"] = new JsonObject { ["type"] = "string", ["description"] = "替换后的新文" } },
               new JsonArray { "path", "old", "new" }),

            Fn("list_dir", "列出项目内某个目录下的文件和子目录。",
               new JsonObject { ["path"] = new JsonObject { ["type"] = "string", ["description"] = "目录；留空或 . 表示项目根" } },
               new JsonArray()),

            Fn("search_files", "按文件名通配符在项目里找文件，如 *.java。",
               new JsonObject { ["pattern"] = new JsonObject { ["type"] = "string", ["description"] = "通配符，如 *.java 或 **/*Test.java" } },
               new JsonArray { "pattern" }),

            Fn("grep", "在项目里搜索文件内容（正则）。",
               new JsonObject {
                   ["pattern"] = new JsonObject { ["type"] = "string", ["description"] = "正则表达式" },
                   ["path"] = new JsonObject { ["type"] = "string", ["description"] = "限定目录，可留空" } },
               new JsonArray { "pattern" }),

            Fn("run_command", "在项目根目录执行一条 shell 命令（cmd）。高危，需用户确认。",
               new JsonObject { ["command"] = new JsonObject { ["type"] = "string", ["description"] = "要执行的命令" } },
               new JsonArray { "command" }),

            Fn("delete_file", "删除项目内的一个文件。高危，需用户确认。",
               new JsonObject { ["path"] = new JsonObject { ["type"] = "string" } },
               new JsonArray { "path" }),
        };
    }

    // ---------------------------------------------------------------- 执行

    /// <summary>
    /// 可用工具名。模型编了个不存在的工具名时，要把它摊开告诉模型有哪些可选 ——
    /// 只说一句「未知工具」，模型不知道能改成什么，就会接着瞎猜，
    /// 最后编出 $TOOL_NAME 这种占位符来。
    /// 新增工具时记得同步这里（就在 Schemas() 上面，改完顺手对一眼）。
    /// </summary>
    public static readonly string[] ToolNames =
    {
        "read_file", "write_file", "str_replace", "list_dir",
        "search_files", "grep", "run_command", "delete_file",
    };

    public ToolResult Invoke(ToolCall call)
    {
        var r = new ToolResult { ToolCallId = call.Id, Name = call.Name };

        // 参数解析单独做，不和执行逻辑混在一个 try 里。
        // 失败时要给出「到底坏在哪」的信息，见 TryParseArgs。
        JsonDocument? doc = TryParseArgs(call.ArgsJson, out string argErr);
        if (doc == null)
        {
            r.Content = argErr;
            r.IsError = true;
            return r;
        }

        try
        {
            using (doc)
            {
                var a = doc.RootElement;
                string Arg(string n) => a.TryGetProperty(n, out var v)
                    ? (v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : v.ToString())
                    : "";

                // 高危：先问用户
                if (IsHighRisk(call.Name) && Confirm != null)
                {
                    string detail = call.Name == "run_command" ? Arg("command") : Arg("path");
                    bool danger = call.Name != "run_command" || LooksDangerous(Arg("command"));
                    // 这两个是弹给用户看的窗，跟着界面语言走；
                    // 其余工具结果是喂给模型的，保持中文。
                    string title = danger ? Lang.T("agent.confirmDanger") : Lang.T("agent.confirmCmd");
                    if (!Confirm(title, call.Name == "run_command"
                                 ? Lang.T("agent.cmdPrefix") + detail
                                 : Lang.T("agent.delPrefix") + detail))
                    {
                        r.Content = Lang.T("agent.denied");
                        return r;
                    }
                }

                r.Content = call.Name switch
                {
                    "read_file"     => ReadFile(Arg("path")),
                    "write_file"    => WriteFile(Arg("path"), Arg("content")),
                    "str_replace"   => StrReplace(Arg("path"), Arg("old"), Arg("new")),
                    "list_dir"      => ListDir(Arg("path")),
                    "search_files"  => SearchFiles(Arg("pattern")),
                    "grep"          => Grep(Arg("pattern"), Arg("path")),
                    "run_command"   => RunCommand(Arg("command")),
                    "delete_file"   => DeleteFile(Arg("path")),
                    // 把可用工具摊开说。只回一句「未知工具」，模型不知道能改成什么，
                    // 就会继续瞎猜名字（甚至编出 $TOOL_NAME 这种占位符），
                    // 界面上于是一排一排地报同样的错。
                    _               => "未知工具：" + call.Name + "。可用工具只有："
                                       + string.Join("、", ToolNames),
                };
            }
        }
        catch (Exception ex)
        {
            r.Content = "工具执行出错：" + ex.Message;
            r.IsError = true;
        }
        return r;
    }

    /// <summary>
    /// 把模型给的参数串解析成 JSON。失败返回 null，err 里是能直接喂回模型的原因。
    ///
    /// 为什么不直接 JsonDocument.Parse(call.ArgsJson)：
    /// 模型给的参数经常裹着一层看不见的东西 —— BOM、零宽字符、Markdown 的
    /// ```json 围栏，甚至前后各来一句解释。这些字符界面上一个都不显示，
    /// 于是报错就成了「工具参数不是合法 JSON：{"path":"*"}」这种自相矛盾的话：
    /// 看着完全合法却说它不合法。用户没法查，模型更没法改 —— 它照着原样再发一遍，
    /// 界面上就刷出一排一模一样的失败卡片，最后干脆开始瞎编（$TOOL_NAME 那种）。
    ///
    /// 所以这里逐层剥壳：先去不可见字符，再剥 Markdown 围栏，还不行就退一步
    /// 只取最外层那对括号之间的内容。真解析不了才报错，而且报错时把原文里的
    /// 不可见字符转义成 \uXXXX 一并给出，让问题一眼可见。
    /// </summary>
    private static JsonDocument? TryParseArgs(string? raw, out string err)
    {
        err = "";
        if (string.IsNullOrWhiteSpace(raw)) raw = "{}";

        string s = StripInvisible(raw!).Trim();

        // 模型爱把参数包在 ```json ... ``` 里
        if (s.StartsWith("```", StringComparison.Ordinal))
        {
            int nl = s.IndexOf('\n');
            if (nl >= 0) s = s.Substring(nl + 1);
            int fence = s.LastIndexOf("```", StringComparison.Ordinal);
            if (fence >= 0) s = s.Substring(0, fence);
            s = s.Trim();
        }

        string lastError;
        if (TryParse(s, out var doc, out lastError)) return doc;

        // 前后粘了解释文字：退一步，只取最外层那对括号之间的内容
        int ob = s.IndexOfAny(new[] { '{', '[' });
        int cb = s.LastIndexOfAny(new[] { '}', ']' });
        if (ob >= 0 && cb > ob)
        {
            string inner = s.Substring(ob, cb - ob + 1);
            if (inner != s && TryParse(inner, out doc, out lastError)) return doc;
        }

        err = "工具参数不是合法 JSON，请重新给出一个完整的 JSON 对象，不要加解释文字。\n"
            + "原始内容（不可见字符已转义）：" + Visible(raw!) + "\n"
            + "解析器报错：" + lastError;
        return null;
    }

    private static bool TryParse(string s, out JsonDocument? doc, out string error)
    {
        try
        {
            doc = JsonDocument.Parse(s);
            error = "";
            return true;
        }
        catch (JsonException ex)
        {
            doc = null;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>丢掉 BOM / 零宽字符 —— 看不见，但会让 JSON 解析直接失败。</summary>
    private static string StripInvisible(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char ch in s)
        {
            if (ch == 0xFEFF || (ch >= 0x200B && ch <= 0x200F) || ch == 0x2060)
                continue;
            sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>
    /// 把不可见字符转义成 \uXXXX 摆出来。
    /// 报错信息必须这么做，否则「看着合法却说不合法」的锅全甩给读日志的人。
    /// 换行/制表符保留原样，那是能看见的、有意义的。
    /// </summary>
    private static string Visible(string s)
    {
        var sb = new StringBuilder(s.Length + 16);
        foreach (char ch in s)
        {
            bool invisible = (ch < 0x20 && ch != '\n' && ch != '\r' && ch != '\t');
            if (invisible || ch == 0x7F
                || (ch >= 0x200B && ch <= 0x200F)
                || (ch >= 0x2028 && ch <= 0x202E)
                || ch == 0xFEFF)
                sb.Append("\\u").Append(((int)ch).ToString("X4"));
            else
                sb.Append(ch);
        }
        return sb.ToString();
    }

    private string ReadFile(string path)
    {
        if (!TryResolve(path, out var full, out var err)) return err;
        if (!File.Exists(full)) return "文件不存在：" + Rel(full);
        try
        {
            var fi = new FileInfo(full);
            if (fi.Length > 400_000) return $"文件太大（{fi.Length / 1024} KB），先缩小范围再读。";
            return File.ReadAllText(full, new UTF8Encoding(false));
        }
        catch (Exception ex) { return "读失败：" + ex.Message; }
    }

    private string WriteFile(string path, string content)
    {
        if (!TryResolve(path, out var full, out var err)) return err;
        try
        {
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(full, content ?? "", new UTF8Encoding(false));
            return "已写入 " + Rel(full) + $"（{(content ?? "").Length} 字符）";
        }
        catch (Exception ex) { return "写失败：" + ex.Message; }
    }

    private string StrReplace(string path, string oldText, string newText)
    {
        if (!TryResolve(path, out var full, out var err)) return err;
        if (!File.Exists(full)) return "文件不存在：" + Rel(full);
        try
        {
            string text = File.ReadAllText(full, new UTF8Encoding(false));
            int n = 0, i = 0;
            while ((i = text.IndexOf(oldText ?? "", i, StringComparison.Ordinal)) >= 0) { n++; i += (oldText ?? "").Length; }
            if (n == 0) return "没找到要替换的原文，原文件未改动。";
            if (n > 1) return $"找到 {n} 处，不唯一，已放弃（请把 old 写得更具体）。";
            File.WriteAllText(full, text.Replace(oldText ?? "", newText ?? ""), new UTF8Encoding(false));
            return "已替换 " + Rel(full);
        }
        catch (Exception ex) { return "替换失败：" + ex.Message; }
    }

    private string ListDir(string path)
    {
        if (!TryResolve(string.IsNullOrWhiteSpace(path) ? "." : path, out var full, out var err)) return err;
        if (!Directory.Exists(full)) return "目录不存在：" + Rel(full);
        var sb = new StringBuilder();
        try
        {
            foreach (var d in Directory.GetDirectories(full).OrderBy(x => x))
                if (!IsSkipped(d)) sb.Append("  [dir]  ").Append(Rel(d)).Append('\n');
            foreach (var f in Directory.GetFiles(full).OrderBy(x => x))
                sb.Append("  [file] ").Append(Rel(f)).Append('\n');
        }
        catch (Exception ex) { return "列目录失败：" + ex.Message; }
        return sb.Length == 0 ? "（空目录）" : sb.ToString();
    }

    private string SearchFiles(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return "通配符为空";
        try
        {
            var hits = Directory.GetFiles(_rootFull, pattern, SearchOption.AllDirectories)
                                .Where(f => !f.Split('\\').Any(seg => SkipDirs.Contains(seg, StringComparer.OrdinalIgnoreCase)))
                                .OrderBy(f => f).Take(100).ToList();
            if (hits.Count == 0) return "没找到匹配的文件。";
            var sb = new StringBuilder();
            foreach (var f in hits) sb.Append(Rel(f)).Append('\n');
            if (hits.Count == 100) sb.Append("（只列前 100 个）");
            return sb.ToString();
        }
        catch (Exception ex) { return "搜索失败：" + ex.Message; }
    }

    private string Grep(string pattern, string path)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return "正则为空";
        System.Text.RegularExpressions.Regex re;
        try { re = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.Compiled); }
        catch (Exception ex) { return "正则不合法：" + ex.Message; }

        string start = _rootFull;
        if (!string.IsNullOrWhiteSpace(path) && !TryResolve(path, out start, out var err)) return err;

        var sb = new StringBuilder();
        int hits = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(start, "*", SearchOption.AllDirectories))
            {
                if (hits >= 60) { sb.Append("（结果太多，只列前 60 条）\n"); break; }
                if (f.Split('\\').Any(seg => SkipDirs.Contains(seg, StringComparer.OrdinalIgnoreCase))) continue;
                string[] lines;
                try { lines = File.ReadAllLines(f); } catch { continue; }
                for (int i = 0; i < lines.Length && hits < 60; i++)
                {
                    if (!re.IsMatch(lines[i])) continue;
                    sb.Append(Rel(f)).Append(':').Append(i + 1).Append(": ").Append(lines[i].Trim()).Append('\n');
                    hits++;
                }
            }
        }
        catch (Exception ex) { return "搜索失败：" + ex.Message; }
        return hits == 0 ? "没匹配到内容。" : sb.ToString();
    }

    private string RunCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return "命令为空";
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", "/c " + command)
            {
                WorkingDirectory = _rootFull,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p == null) return "启动进程失败";
            string o = p.StandardOutput.ReadToEnd();
            string e = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(120_000)) { try { p.Kill(); } catch { } return "命令超时（120 秒）已被终止。"; }
            string outp = (o + e).TrimEnd();
            if (outp.Length > 20_000) outp = outp.Substring(0, 20_000) + "\n…（输出过长已截断）";
            return $"退出码 {p.ExitCode}\n{outp}";
        }
        catch (Exception ex) { return "执行失败：" + ex.Message; }
    }

    private string DeleteFile(string path)
    {
        if (!TryResolve(path, out var full, out var err)) return err;
        if (!File.Exists(full)) return "文件不存在：" + Rel(full);
        try { File.Delete(full); return "已删除 " + Rel(full); }
        catch (Exception ex) { return "删除失败：" + ex.Message; }
    }
}
