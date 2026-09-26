#nullable enable

// AgentClient.cs —— 跟模型对话（OpenAI 兼容协议，DeepSeek 用的就是这套）
//
// 用流式（SSE）输出：模型一边想一边往外吐字，界面上能看着它写。
// 同时按 OpenAI 的 tool_calls 增量拼装工具调用参数
// （arguments 是分片来的，必须按 index 累加，直接取最后一个分片会拿到半个 JSON）。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace JavaStudio;

/// <summary>模型一轮回复的结果：正文 + 它想调的工具。</summary>
public sealed class AssistantTurn
{
    public string Content { get; set; } = "";
    /// <summary>
    /// 模型的思考过程（DeepSeek 的 reasoning_content，或正文里 <think> 块里的东西）。
    /// 单独存、不并进 Content：界面上要能折叠，混在一起就没法收起了。
    /// </summary>
    public string Reasoning { get; set; } = "";
    public List<ToolCall> ToolCalls { get; set; } = new();
    public bool HasToolCalls => ToolCalls.Count > 0;
}

public sealed class AgentClient
{
    private readonly AgentSettings _s;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public AgentClient(AgentSettings settings) { _s = settings; }

    /// <summary>系统提示词：把「只能碰项目目录」这条规矩直接钉给模型。</summary>
    private string SystemPrompt(string root) =>
        "你是 To-tode Studio Pro —— 集成在 Java Studio 里的编程 Agent。\n" +
        $"当前项目的根目录是：{root}\n" +
        "硬性约束，违反会让调用失败：\n" +
        "1. 所有文件操作只能给相对路径，且必须落在上面的项目根目录内；越界会被直接拒绝。\n" +
        "2. 动手前先用 read_file / grep / search_files 看清现状，不要凭记忆猜文件内容。\n" +
        "3. 改文件优先 str_replace 做最小改动；只有整篇重写时才用 write_file。\n" +
        "4. run_command 会弹窗让用户确认，命令要写清楚可预期，不要用破坏性命令。\n" +
        "5. 回答用中文，简洁务实，直接说明改了什么、为什么。\n";

    private JsonArray BuildMessages(IEnumerable<ChatMsg> history, string root)
    {
        var arr = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = SystemPrompt(root) }
        };

        foreach (var m in history)
        {
            if (m.Role == "assistant" && m.ToolCalls != null && m.ToolCalls.Count > 0)
            {
                var calls = new JsonArray();
                foreach (var c in m.ToolCalls)
                {
                    calls.Add(new JsonObject
                    {
                        ["id"] = c.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = c.Name,
                            ["arguments"] = string.IsNullOrWhiteSpace(c.ArgsJson) ? "{}" : c.ArgsJson,
                        }
                    });
                }
                var am = new JsonObject { ["role"] = "assistant", ["tool_calls"] = calls };
                if (!string.IsNullOrEmpty(m.Content)) am["content"] = m.Content;
                else am["content"] = "";
                arr.Add(am);
            }
            else if (m.Role == "tool")
            {
                arr.Add(new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = m.ToolCallId,
                    ["content"] = m.Content ?? "",
                });
            }
            else
            {
                arr.Add(new JsonObject { ["role"] = m.Role, ["content"] = m.Content ?? "" });
            }
        }
        return arr;
    }

    /// <summary>
    /// 发一轮对话。回调都在后台线程上，界面要自己 Dispatcher 切回去。
    /// onDelta      —— 每段正文
    /// onReasoning  —— 每段思考过程（DeepSeek 的 reasoning_content）
    /// </summary>
    public async Task<AssistantTurn> ChatAsync(IEnumerable<ChatMsg> history, string root,
                                               Action<string>? onDelta, CancellationToken ct,
                                               Action<string>? onReasoning = null)
    {
        var turn = new AssistantTurn();
        if (!_s.Ready) throw new InvalidOperationException("还没填 API：请在 Agent 设置里填 Base URL 和 API Key。");

        var body = new JsonObject
        {
            ["model"] = _s.Model,
            ["stream"] = true,
            ["temperature"] = _s.Temperature,
            ["messages"] = BuildMessages(history, root),
            ["tools"] = AgentTools.Definitions(),
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, _s.ChatUrl);
        req.Headers.Add("Authorization", "Bearer " + _s.ApiKey.Trim());
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            string err = "";
            try { err = await resp.Content.ReadAsStringAsync(ct); } catch { }
            if (err.Length > 600) err = err.Substring(0, 600);
            throw new InvalidOperationException($"接口返回 {(int)resp.StatusCode}：{err}");
        }

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        // arguments 分片累加：key = tool_calls 数组下标
        var pending = new Dictionary<int, ToolCall>();

        while (!ct.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync();
            if (line == null) break;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            string data = line.Substring(5).Trim();
            if (data.Length == 0 || data == "[DONE]") break;

            JsonElement rootEl;
            try
            {
                using var doc = JsonDocument.Parse(data);
                rootEl = doc.RootElement.Clone();
            }
            catch { continue; }

            if (!rootEl.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array) continue;
            if (choices.GetArrayLength() == 0) continue;
            var first = choices[0];

            if (first.TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.Object)
            {
                if (d.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                {
                    string piece = c.GetString() ?? "";
                    if (piece.Length > 0) { turn.Content += piece; onDelta?.Invoke(piece); }
                }

                // 思考过程走单独的字段（reasoning_content，DeepSeek 的 R1/reasoner 系列）。
                // 它和正文是两条不同的流，必须分开接 —— 混进正文就没法折叠了。
                if (d.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String)
                {
                    string piece = rc.GetString() ?? "";
                    if (piece.Length > 0) { turn.Reasoning += piece; onReasoning?.Invoke(piece); }
                }

                if (d.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var t in tcs.EnumerateArray())
                    {
                        int idx = t.TryGetProperty("index", out var ix) && ix.ValueKind == JsonValueKind.Number
                            ? ix.GetInt32() : 0;
                        if (!pending.TryGetValue(idx, out var cur)) { cur = new ToolCall(); pending[idx] = cur; }

                        if (t.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                            cur.Id = id.GetString() ?? cur.Id;

                        if (t.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
                        {
                            if (fn.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String)
                                cur.Name = nm.GetString() ?? cur.Name;
                            if (fn.TryGetProperty("arguments", out var ag) && ag.ValueKind == JsonValueKind.String)
                                cur.ArgsJson += ag.GetString() ?? "";
                        }
                    }
                }
            }
        }

        // 正文里混进来的 <think> 块，收尾统一挪到思考区。
        // 走 reasoning_content 的模型不受影响（那里本来就是空的，拼上去即可）。
        var (text, thought) = Think.Split(turn.Content);
        if (thought.Length > 0)
        {
            turn.Content = text;
            turn.Reasoning = turn.Reasoning.Length == 0 ? thought : turn.Reasoning + "\n" + thought;
        }

        foreach (var kv in pending.OrderBy(k => k.Key))
        {
            // 收尾时才补空对象兜底。
            // ⚠️ 别把这行挪到上面去、也别把 ToolCall.ArgsJson 的初值设成 "{}"：
            // 循环里是 `+=` 累积分片的，初值非空就会变成 '{}{...}'，
            // 整个参数 JSON 直接坏掉（而且报错看着还像合法的）。
            if (string.IsNullOrWhiteSpace(kv.Value.ArgsJson)) kv.Value.ArgsJson = "{}";
            turn.ToolCalls.Add(kv.Value);
        }
        return turn;
    }
}
