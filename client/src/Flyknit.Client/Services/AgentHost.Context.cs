using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Flyknit.Core.Agent;
using Flyknit.Core.Chat;
using Flyknit.Core.Context;
using Flyknit.Core.Gateway;
using Flyknit.Core.Storage;
using Flyknit.Core.Tools;

namespace Flyknit.Client.Services;

/// <summary>
/// 「会话信息」面板：每轮回答结束时记下上下文用在哪了（系统提示各段、工具定义、对话、工具结果），
/// 存进对话记录，面板打开时直接读；员工可以手动「整理对话」，把较早的内容压成摘要。
/// </summary>
public sealed partial class AgentHost
{
    private static readonly JsonSerializerOptions UsageJson = new(JsonSerializerDefaults.Web);

    /// <summary>一轮回答结束：算出这一轮最后一次请求的上下文用量，存下来并推给界面。</summary>
    private void RecordContextUsage(
        string id,
        IReadOnlyList<(string Key, string Text)> sections,
        string prompt,
        string delegationPrompt,
        ContextManager? context,
        IEnumerable<ITool> tools,
        IReadOnlyList<ChatMessage> history,
        AgentRunResult result,
        IHostEvents events)
    {
        try
        {
            var all = sections.ToList();
            if (delegationPrompt.Length > 0)
            {
                all.Add(("experts", delegationPrompt));
            }
            // 压缩出的摘要和带上的任务计划：系统提示里比原始提示多出来的部分
            var system = history.FirstOrDefault(m => m.Role == ChatRole.System)?.Content ?? prompt;
            if (system.Length > prompt.Length && system.StartsWith(prompt, StringComparison.Ordinal))
            {
                all.Add(("summary", system[prompt.Length..]));
            }
            var options = context?.Options ?? new ContextOptions();
            var length = context is { ContextLength: > 0 } ? context.ContextLength : _contextLength > 0 ? _contextLength : options.DefaultContextLength;
            var last = result.NewMessages.LastOrDefault(m => m.Role == ChatRole.Assistant && m.PromptTokens > 0)?.PromptTokens;
            var report = ContextUsage.Measure(
                all,
                tools,
                history.Where(m => m.Role != ChatRole.System).ToList(),
                length,
                context?.CompactThreshold ?? (int)((length - options.ReserveOutputTokens) * options.CompactRatio),
                options.ReserveOutputTokens,
                last);
            SaveContextUsage(id, report, events);
        }
        catch (Exception ex)
        {
            Log.Warn("记录上下文用量失败", ex);
        }
    }

    private void SaveContextUsage(string id, ContextUsageReport report, IHostEvents? events)
    {
        var json = JsonSerializer.Serialize(report, UsageJson);
        Store.SetContextUsage(id, json);
        events?.Post(new { type = "context.usage", conversationId = id, usage = JsonNode.Parse(json) });
    }

    /// <summary>界面要的用量（没回答过的对话返回 null）。</summary>
    public JsonNode? ContextUsageOf(string id)
    {
        var json = Store.GetContextUsage(id);
        return string.IsNullOrEmpty(json) ? null : JsonNode.Parse(json);
    }

    /// <summary>
    /// 员工手动「整理对话」：最近一问一答保留原文，之前的压成摘要（和自动压缩存的是同一份摘要）。
    /// AI 正在回答时不能整理。
    /// </summary>
    public async Task<(bool Ok, string Message)> CompactConversationAsync(string id, IHostEvents events, CancellationToken ct)
    {
        if (IsRunning(id))
        {
            return (false, "AI 正在回答，等这一轮做完再整理");
        }
        var conv = Store.Get(id);
        if (conv is null)
        {
            return (false, "对话不存在");
        }
        if (conv.Mode == ConversationMode.Translate)
        {
            return (false, "翻译对话不带上下文，不需要整理");
        }
        var stored = Store.GetMessages(id);
        var summary = conv.Summary;
        var recent = (IEnumerable<ChatMessage>)stored;
        var uptoIndex = conv.SummaryUpto is null ? -1 : stored.FindIndex(m => m.Id == conv.SummaryUpto);
        if (uptoIndex >= 0)
        {
            recent = stored.Skip(uptoIndex + 1);
        }
        else
        {
            summary = null;
        }

        var context = new ContextManager(Server, "", summary, _contextLength, enableSessionMemory: false)
        {
            Scene = conv.Mode == ConversationMode.Chat ? Scenes.Chat : Scenes.Agent,
            ModelId = conv.ModelId,
        };
        context.Progress += p => events.Post(new { type = "context.compacting", conversationId = id, phase = p.Phase, percent = p.Percent, messages = p.MessagesCompacted });
        CompactionInfo? info;
        var history = new List<ChatMessage> { ChatMessage.System(context.SystemPrompt) };
        history.AddRange(TrimHistory(recent.ToList()));
        try
        {
            info = await context.CompactNowAsync(history, ct);
        }
        catch (Exception ex)
        {
            Log.Warn($"手动整理对话 {id} 失败", ex);
            events.Post(new { type = "context.compacting", conversationId = id, phase = "done", percent = 100, messages = 0 });
            return (false, "整理失败：" + ex.Message);
        }
        if (info is null)
        {
            return (false, "对话还很短，不需要整理");
        }

        Store.SetSummary(id, info.Summary, info.UptoMessageId);
        Log.Info($"对话 {id} 手动整理：{info.MessagesCompacted} 条消息，{info.TokensBefore} → {info.TokensAfter} tokens");
        events.Post(new { type = "context.compacted", conversationId = id, uptoMessageId = info.UptoMessageId, tokensBefore = info.TokensBefore, tokensAfter = info.TokensAfter });

        // 面板上的数字跟着变：系统提示和工具不变，对话换成整理后的，摘要单独算一段
        if (Store.GetContextUsage(id) is { Length: > 0 } json && JsonSerializer.Deserialize<ContextUsageReport>(json, UsageJson) is { } old)
        {
            var updated = old.WithConversation(history.Where(m => m.Role != ChatRole.System).ToList());
            var system = updated.Parts.FirstOrDefault(p => p.Key == ContextUsage.SystemPrompt);
            if (system is not null)
            {
                var items = system.Items.Where(i => i.Key != "summary").ToList();
                items.Add(ContextPart.Of("summary", ContextUsage.SectionLabels["summary"], TokenEstimator.Estimate(history[0].Content)));
                var parts = updated.Parts.Select(p => p.Key == ContextUsage.SystemPrompt
                    ? new ContextPart(p.Key, p.Label, items.Sum(i => i.Tokens), items.OrderByDescending(i => i.Tokens).ToList())
                    : p).ToList();
                updated = new ContextUsageReport
                {
                    ContextLength = updated.ContextLength,
                    ReserveOutput = updated.ReserveOutput,
                    CompactAt = updated.CompactAt,
                    Parts = parts,
                    LastPromptTokens = null, // 整理后还没发过请求
                };
            }
            SaveContextUsage(id, updated, events);
        }
        return (true, $"已整理 {info.MessagesCompacted} 条较早的消息");
    }
}
