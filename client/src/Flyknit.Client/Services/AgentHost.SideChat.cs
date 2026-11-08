using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Flyknit.Core.Agent;
using Flyknit.Core.Chat;
using Flyknit.Core.Gateway;

namespace Flyknit.Client.Services;

/// <summary>侧聊：读任务对话、不写回。答案逐字推给界面（sidechat.delta），答完 sidechat.done。</summary>
public sealed partial class AgentHost
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _sideChats = new();

    public void AskSide(string conversationId, string requestId, string question, IReadOnlyList<ChatMessage> sideHistory, IHostEvents events)
    {
        var conv = Store.Get(conversationId) ?? throw new InvalidOperationException("会话不存在");
        if (question.Trim().Length == 0)
        {
            throw new InvalidOperationException("问题是空的");
        }
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        _sideChats[requestId] = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                var stored = Store.GetMessages(conversationId);
                var messages = SideChat.BuildMessages(stored, sideHistory, question);
                var turn = await Server.CompleteAsync(new ChatRequest
                {
                    Scene = Scenes.Chat,
                    Purpose = Flyknit.Core.Gateway.Purposes.SideChat,
                    Messages = messages,
                    Stream = true,
                    ModelId = conv.ModelId,
                    ConversationId = conversationId,
                }, new SideSink(requestId, events), cts.Token);
                events.Post(new { type = "sidechat.done", requestId, content = turn.Content });
            }
            catch (OperationCanceledException)
            {
                events.Post(new { type = "sidechat.done", requestId, stopped = true });
            }
            catch (Exception ex)
            {
                Log.Warn("侧聊失败", ex);
                events.Post(new { type = "sidechat.error", requestId, message = ex.Message });
            }
            finally
            {
                _sideChats.TryRemove(requestId, out _);
                cts.Dispose();
            }
        });
    }

    public void StopSide(string requestId)
    {
        if (_sideChats.TryGetValue(requestId, out var cts))
        {
            cts.Cancel();
        }
    }

    private sealed class SideSink : IStreamSink
    {
        private readonly string _id;
        private readonly IHostEvents _events;

        public SideSink(string id, IHostEvents events)
        {
            _id = id;
            _events = events;
        }

        public void OnContent(string delta) => _events.Post(new { type = "sidechat.delta", requestId = _id, text = delta });

        public void OnReasoning(string delta)
        {
        }
    }
}
