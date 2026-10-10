using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Flyknit.Client.Bridge;
using Flyknit.Client.Tools;
using Flyknit.Core.Agent;
using Flyknit.Core.Chat;
using Flyknit.Core.Context;
using Flyknit.Core.Gateway;
using Flyknit.Core.Memory;
using Flyknit.Core.Security;
using Flyknit.Core.Settings;
using Flyknit.Core.Skills;
using Flyknit.Core.Storage;
using Flyknit.Core.Tools;

namespace Flyknit.Client.Services;

/// <summary>界面事件的出口（由 WebBridge 实现）。</summary>
public interface IHostEvents
{
    void Post(object evt);
}

/// <summary>
/// 客户端的核心编排：会话存储、Agent 循环、工具、策略、记忆、Skills、审计。
/// 与界面无关，界面通过 WebBridge 调用这里。
/// </summary>
public sealed class AgentHost : IDisposable
{
    /// <summary>发给模型的最大历史消息数（兜底；正常情况下由上下文压缩控制长度）。</summary>
    private const int HistoryWindow = 300;

    private readonly AppSettings _settings;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _runs = new();
    private readonly Timer _configTimer;
    private readonly CancellationTokenSource _watchCts = new();
    private CommandPolicy _policy = CommandPolicy.Default();

    /// <summary>当前生效的命令与网络策略（每 10 分钟随配置刷新）。</summary>
    public CommandPolicy Policy => _policy;

    public FlyknitServerClient Server { get; }
    public ConversationStore Store { get; }
    public MemoryStore Memory { get; }
    public SkillCatalog Skills { get; }
    public SkillService SkillManager { get; }
    public ToolRegistry Tools { get; }

    /// <summary>MCP 连接器。和技能（Skills / SkillManager）各管各的，只在工具注册表里相遇，且工具名带 mcp__ 前缀。</summary>
    public McpService Mcp { get; }
    public AuditQueue Audit { get; }

    /// <summary>上报服务端的同时，把拦截与放行记录存一份在本机（用量与安全面板用）。</summary>
    private readonly RecordingAuditSink _auditSink;
    public ApprovalStore Approvals { get; }

    /// <summary>覆盖写之前留一份原文件。删除有回收站，覆盖写在这之前什么都没有。</summary>
    private Flyknit.Core.Tools.FileBackup _backup = new(AppPaths.Backups, warn: m => Log.Warn(m));

    /// <summary>安全中心每一项的值和锁状态，由服务端下发。</summary>
    public Flyknit.Core.Security.SecuritySettings Security { get; } = new();

    /// <summary>
    /// 把安全设置落到实处。界面上置灰只是提示，真正的管控要在这里生效。
    /// </summary>
    /// <summary>用户在安全中心改了可改的项之后，让它立刻生效。</summary>
    public void RefreshSecurity() => ApplySecurity();

    /// <summary>自动更新。下载在后台做完，安装只在能重启的那一刻发生。</summary>
    public UpdateService Updater { get; }

    private void ApplySecurity()
    {
        var quota = Security.Number(Flyknit.Core.Security.SecuritySettings.BackupQuotaMb, 512);
        _backup = new Flyknit.Core.Tools.FileBackup(AppPaths.Backups, quota * 1024L * 1024L, m => Log.Warn(m));
        if (Security.Get(Flyknit.Core.Security.SecuritySettings.Notifications) is { } notify)
        {
            _settings.EnableNotifications = notify.AsBool();
        }
        _settings.Save();
        RefreshKeepAwake(); // IT 可能刚关掉了锁屏运行
    }

    // ---------- 锁屏运行 ----------

    private readonly KeepAwakeService _keepAwake = new();
    private Timer? _keepAwakeTimer;

    public bool KeepAwakeAllowed => Security.On(Flyknit.Core.Security.SecuritySettings.KeepAwakeAllowed);
    public bool KeepScreenOnAllowed => Security.On(Flyknit.Core.Security.SecuritySettings.KeepScreenOn);

    /// <summary>按现在的设置、公司策略和有没有任务，决定要不要挡住睡眠。有任务开始/结束、策略变了、每分钟都会算一次。</summary>
    public void RefreshKeepAwake()
    {
        try
        {
            var now = DateTimeOffset.Now;
            var busy = _runs.Count > 0
                || Store.Schedules.List().Any(t => t.Enabled && t.NextRunAt is { } next && next <= now + KeepAwake.ScheduleLead);
            _keepAwake.Set(KeepAwake.Resolve(_settings.KeepAwakeMode, KeepAwakeAllowed, KeepScreenOnAllowed, busy));
        }
        catch (Exception ex)
        {
            Log.Warn("更新锁屏运行状态失败", ex);
        }
    }

    // ---------- 个性化 ----------

    /// <summary>现在选的语气预设（还没选过时按 soul.md 有没有被改过推断）。</summary>
    public string PersonaKey => Personas.Infer(_settings.Persona, Memory.Read(MemoryStore.SoulFile));

    public bool PlayfulPersonasAllowed => Security.On(Flyknit.Core.Security.SecuritySettings.PersonaPlayful);
    public bool CustomPersonaAllowed => Security.On(Flyknit.Core.Security.SecuritySettings.PersonaCustom);

    /// <summary>放进提示词的语气、称呼、名字（已按公司策略换算）。</summary>
    private (string Tone, string CallName, string AssistantName) PersonaForPrompt()
    {
        var soul = Memory.Read(MemoryStore.SoulFile);
        var key = Personas.Effective(Personas.Infer(_settings.Persona, soul), PlayfulPersonasAllowed, CustomPersonaAllowed);
        return (Personas.ToneText(key, soul), Personas.CleanName(_settings.UserCallName),
            CustomPersonaAllowed ? Personas.CleanName(_settings.AssistantName) : "");
    }

    /// <summary>
    /// 实际要放的提示音：none / soft / alert。安全项「通知提示音」管开关（IT 可以统一静音），
    /// 本机设置记住选的是哪种音色；服务端没下发这一项时只看本机设置。
    /// </summary>
    public string NotificationSoundChoice()
    {
        var chosen = _settings.NotificationSound is "soft" or "alert" ? _settings.NotificationSound : "none";
        if (Security.Get(Flyknit.Core.Security.SecuritySettings.NotificationSound) is not { } item)
        {
            return chosen;
        }
        return !item.AsBool(false) ? "none" : chosen == "none" ? "soft" : chosen;
    }
    public EpisodeStore Episodes { get; }

    /// <summary>学习技能的使用记录与生命周期（候选 → 启用 → 退役）。</summary>
    public Flyknit.Core.Skills.LearnedSkills LearnedSkillLedger { get; }

    /// <summary>资料库：对话里上传的文件、截图、AI 产出，以及在资料库里新建的东西。</summary>
    public LibraryService Library { get; }
    public ScheduleRunner Scheduler { get; }

    /// <summary>
    /// 代理设置改了之后换一个 HttpClient。
    /// HttpClientHandler 的代理在创建后不能改，所以只能重建；
    /// 换的是 FlyknitServerClient 内部那个客户端，上层拿到的还是同一个实例，不用重启。
    /// </summary>
    public void ApplyProxy()
    {
        try
        {
            Server.ReplaceHttpClient(ProxyFactory.CreateHttpClient(_settings));
            Mcp.ApplyProxy();
            Log.Info($"网络代理已切换为 {_settings.ProxyMode}");
            _ = RefreshConfigAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("切换网络代理失败", ex);
        }
    }

    /// <summary>测试能不能连上服务端，用来验证代理设置。</summary>
    public async Task<(bool Ok, string Message)> TestConnectionAsync(CancellationToken ct)
    {
        try
        {
            await Server.GetConfigAsync(ct);
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>界面就绪后由 WebBridge 挂上，定时任务用它推送事件和请求确认。</summary>
    public IHostEvents? Events { get; private set; }
    public IConfirmationHandler? Confirm { get; private set; }

    public string UiLanguage => _settings.ResolveUiLanguage();

    /// <summary>界面（WebBridge）准备好之后调用一次。</summary>
    public void AttachUi(IHostEvents events, IConfirmationHandler confirm)
    {
        Events = events;
        Confirm = confirm;
    }

    /// <summary>复盘沉淀出的技能放在个人技能目录下的 learned 子目录。</summary>
    public static string LearnedSkills => AppPaths.LearnedSkills;

    /// <summary>最近一次得知的模型上下文长度。</summary>
    private int _contextLength;

    public bool Connected { get; private set; }
    public string ServerMessage { get; private set; } = "";
    public string ModelName { get; private set; } = "";

    /// <summary>后台填的台账：这台电脑的使用者实名与所属部门。界面上显示。</summary>
    public string Owner { get; private set; } = "";
    public string Department { get; private set; } = "";

    /// <summary>服务端配置版本号，长轮询用它判断配置有没有变。</summary>
    private int _configRevision;

    public event Action? StatusChanged;

    /// <summary>
    /// 本机的注册信息服务端不认了，需要重新注册。
    ///
    /// 只在 401 时触发，403 不触发——403 是管理员停用了这台机器，
    /// 要是停用后重启一下就能自己重新注册，那这个开关等于没有。
    /// </summary>
    public event Action? ReregistrationRequired;

    /// <summary>员工在设置里点了「退出登录」，服务端那边已经处理过。App 清掉本机令牌后重启到登录窗口。</summary>
    public event Action? LogoutRequested;

    /// <summary>
    /// 退出登录：先请服务端作废令牌（连不上也照样退——本机令牌清掉就登不回来了，
    /// 服务端那条记录只是成了一台不再上线的旧设备），再交给 App 重启到登录窗口。
    /// </summary>
    public async Task LogoutAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await Server.LogoutAsync(cts.Token);
        }
        catch (Exception ex)
        {
            Log.Warn("通知服务端退出登录失败（本机照样退出）", ex);
        }
        Log.Info("员工退出登录");
        LogoutRequested?.Invoke();
    }

    /// <summary>「重启升级」：更新器已经起来了，App 收拾干净后退出（不能只关窗口，进程在代理就换不了文件）。</summary>
    public event Action? ExitRequested;

    public void RequestExit() => ExitRequested?.Invoke();

    /// <summary>有运行中的任务或等待确认时变化，悬浮球据此显示状态。</summary>
    public event Action<int>? ActiveRunsChanged;

    /// <summary>技能目录发生变化（安装、卸载、手动拷入）。</summary>
    public event Action? SkillsChanged;

    public AgentHost(AppSettings settings)
    {
        _settings = settings;
        Server = new FlyknitServerClient(ProxyFactory.CreateHttpClient(settings), settings.ServerUrl, settings.DeviceToken);
        Store = new ConversationStore(AppPaths.Database);
        // 记忆条目和会话放在同一个数据库里（数据目录），md 文件仍在记忆目录，可以直接编辑
        Memory = new MemoryStore(AppPaths.Memory, AppPaths.Database);
        Memory.SensitiveRejected += (kind, findings) => Log.Warn($"一条{MemoryStore.HeaderOf(kind)}含{string.Join("、", findings)}，没有记入记忆");
        // 服务端配了向量模型（embedding 场景）时，按意思也能找回记忆；没配就只按字面匹配
        Memory.Semantic = new SemanticIndex(Memory, Server);
        Skills = new SkillCatalog()
            // 机器级目录优先：IT 统一预装的技能对所有 Windows 用户可见
            .AddRoot(AppPaths.MachineSkills, SkillSource.Organization)
            .AddRoot(AppPaths.OrgSkills, SkillSource.Organization)
            .AddRoot(AppPaths.LearnedSkills, SkillSource.Learned)
            .AddRoot(AppPaths.Skills);
        SkillManager = new SkillService(Skills, settings, Server, () => _policy);
        Tools = ToolRegistry.CreateDefault().Add(new OpenAppTool(() => _settings.AppAliases));
        Audit = new AuditQueue(Server);
        _auditSink = new RecordingAuditSink(Audit, Store);
        Approvals = new ApprovalStore(AppPaths.ApprovalRules);
        // 启动时清一次，免得上次退出前超了上限一直留着
        _ = Task.Run(() => _backup.Trim());
        // 历史任务和记忆条目在同一个库里：教训只存一份，按 ID 引用
        Episodes = new EpisodeStore(AppPaths.Memory, AppPaths.Database);
        LearnedSkillLedger = new Flyknit.Core.Skills.LearnedSkills(AppPaths.LearnedSkills, AppPaths.Database);
        Library = new LibraryService(AppPaths.Database);
        Scheduler = new ScheduleRunner(this);
        RunFinished += Scheduler.OnRunFinished;
        Updater = new UpdateService(Server, typeof(AgentHost).Assembly.GetName().Version?.ToString(3) ?? "0.1.0");
        Mcp = new McpService(settings, Server, Tools, typeof(AgentHost).Assembly.GetName().Version?.ToString(3) ?? "0.1.0");
        _configTimer = new Timer(_ => _ = RefreshConfigAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public async Task InitializeAsync()
    {
        Memory.EnsureDefaults();
        Library.Initialize(Store);
        _settings.EnsureWorkspaces();
        Directory.CreateDirectory(AppPaths.Skills);
        Directory.CreateDirectory(AppPaths.OrgSkills);
        Directory.CreateDirectory(AppPaths.LearnedSkills);
        SkillManager.Refresh();
        Skills.Changed += () => SkillsChanged?.Invoke();
        Skills.StartWatching();
        await Task.Run(() => Store.PurgeExpired());
        await RefreshConfigAsync();
        Scheduler.Start();
        Updater.Start();
        // 锁屏运行：任务开始、结束时重新算；每分钟再算一次（定时任务快到点时提前挡住睡眠）
        ActiveRunsChanged += _ => RefreshKeepAwake();
        _keepAwakeTimer = new Timer(_ => RefreshKeepAwake(), null, TimeSpan.Zero, TimeSpan.FromMinutes(1));
        // 把上次连着的 MCP 连接器连回来。放后台：哪家连不上都不该拖慢启动
        _ = Task.Run(Mcp.StartAsync);
        // 长轮询监听配置变更，IT 一改安全中心/策略就近乎即时拉取生效
        _ = Task.Run(ConfigWatchLoopAsync);
    }

    /// <summary>长轮询监听服务端配置版本号，一变就立刻重新拉取配置（安全策略即时生效）。</summary>
    private async Task ConfigWatchLoopAsync()
    {
        var token = _watchCts.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!Connected)
                {
                    // 还没连上（未注册/被停用/网络不通）就先歇会儿，等定时刷新去重建连接
                    await Task.Delay(TimeSpan.FromSeconds(5), token);
                    continue;
                }
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(TimeSpan.FromSeconds(35));
                var rev = await Server.WaitForConfigChangeAsync(_configRevision, cts.Token);
                if (rev != _configRevision)
                {
                    await RefreshConfigAsync();
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn($"监听配置变更失败：{ex.Message}");
                try { await Task.Delay(TimeSpan.FromSeconds(10), token); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    /// <summary>从服务端拉取策略与模型信息，失败时 1 分钟后重试，成功后每 10 分钟刷新。</summary>
    public async Task RefreshConfigAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var config = await Server.GetConfigAsync(cts.Token);
            if (config.Policy is not null)
            {
                _policy = new CommandPolicy(config.Policy);
            }
            if (config.Security.Count > 0)
            {
                // 锁住的以服务端为准，没锁的保留用户改过的值
                Security.MergeFromServer(config.Security);
                Security.ApplyLocal(_settings.SecurityChoices);
                ApplySecurity();
            }
            var agent = config.Scenes.FirstOrDefault(s => s.Scene == Scenes.Agent);
            if (agent is { ContextLength: > 0 } && _contextLength == 0)
            {
                _contextLength = agent.ContextLength;
            }
            Connected = true;
            ServerMessage = agent is { Available: true } ? "" : "服务端尚未配置模型";
            ModelName = agent?.ModelName ?? "";
            _configRevision = config.Revision;
            Owner = config.Owner;
            Department = config.Department;
            _configTimer.Change(TimeSpan.FromMinutes(10), Timeout.InfiniteTimeSpan);
            // 顺带把本机信息报上去。配置拉得到就说明在线，两件事本来就是同一个信号。
            // 失败不影响对话，所以吞掉异常，不改 Connected。
            try
            {
                var version = typeof(AgentHost).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
                var info = MachineInfo.Collect(version, _settings.ResolveUiLanguage());
                await Server.ReportMachineAsync(info, cts.Token);
            }
            catch (Exception ex)
            {
                Log.Warn($"上报本机信息失败：{ex.Message}");
            }
            // 同步公司技能库里管理员标记为必装的技能
            _ = SkillManager.SyncRequiredAsync(CancellationToken.None);
            // MCP 连接器另走一条：下架的断开、改了配置的重连
            _ = Task.Run(Mcp.SyncAsync);
        }
        catch (GatewayException ex) when (ex.StatusCode is 401 or 403)
        {
            // 管理端把这台机器停用了，或者服务端不认这个令牌了。
            // 以前这里只会显示「连接失败」，用户和 IT 都看不出发生了什么。
            Connected = false;
            Log.Warn($"设备认证被拒（HTTP {ex.StatusCode}）：{ex.Message}");
            // 这种状态重试再快也没用，退到十分钟一次
            _configTimer.Change(TimeSpan.FromMinutes(10), Timeout.InfiniteTimeSpan);

            if (ex.StatusCode == 403)
            {
                ServerMessage = $"本机已被管理员停用。{ex.Message}";
            }
            else
            {
                // 401 = 服务端查无此设备。常见于换了服务器、或者服务端的库被重建过。
                // 引导重新注册，而不是让用户自己去删 settings.json。
                ServerMessage = "本机需要重新注册到服务器";
                ReregistrationRequired?.Invoke();
            }
        }
        catch (Exception ex)
        {
            Connected = false;
            ServerMessage = ex.Message;
            Log.Warn("拉取服务端配置失败", ex);
            _configTimer.Change(TimeSpan.FromMinutes(1), Timeout.InfiniteTimeSpan);
        }
        StatusChanged?.Invoke();
    }

    public bool IsRunning(string conversationId) => _runs.ContainsKey(conversationId);

    public void Stop(string conversationId)
    {
        if (_runs.TryGetValue(conversationId, out var cts))
        {
            cts.Cancel();
        }
    }

    /// <summary>发送一条用户消息并在后台运行，过程通过 events 推送给界面。</summary>
    /// <param name="messageId">界面生成的消息 ID，保证界面与数据库中的 ID 一致（编辑、重发时使用）。</param>
    public void Send(string conversationId, string text, IReadOnlyList<Attachment> attachments, string? messageId, string uiLanguage, IConfirmationHandler confirm, IHostEvents events)
    {
        StartRun(conversationId, uiLanguage, confirm, events, id => NewUserMessage(messageId, text, IngestUploads(attachments, id)));
    }

    /// <summary>重新生成最后一个问题的回答：删除最后一条用户消息之后的内容，再运行一次。</summary>
    public void Regenerate(string conversationId, string uiLanguage, IConfirmationHandler confirm, IHostEvents events)
    {
        StartRun(conversationId, uiLanguage, confirm, events, id =>
        {
            var last = Store.GetMessages(id).LastOrDefault(m => m.Role == ChatRole.User)
                       ?? throw new InvalidOperationException("没有可以重新生成的问题");
            Store.DeleteMessagesFrom(id, last.Id, inclusive: false);
            return null;
        });
    }

    /// <summary>编辑某条用户消息后重新发送：删除这条消息及之后的所有内容，用新内容重新提问（保留原附件）。</summary>
    public void EditAndResend(string conversationId, string messageId, string text, string? newMessageId, string uiLanguage, IConfirmationHandler confirm, IHostEvents events)
    {
        StartRun(conversationId, uiLanguage, confirm, events, id =>
        {
            var original = Store.GetMessages(id).FirstOrDefault(m => m.Id == messageId && m.Role == ChatRole.User)
                           ?? throw new InvalidOperationException("找不到要编辑的消息");
            Store.DeleteMessagesFrom(id, messageId, inclusive: true);
            return NewUserMessage(newMessageId, text, original.Attachments);
        });
    }

    /// <summary>附件收进资料库；收录失败不影响发送。</summary>
    private IReadOnlyList<Attachment> IngestUploads(IReadOnlyList<Attachment> attachments, string conversationId)
    {
        if (attachments.Count == 0)
        {
            return attachments;
        }
        try
        {
            return Library.IngestUploads(attachments, conversationId);
        }
        catch (Exception ex)
        {
            Log.Warn("附件收入资料库失败", ex);
            return attachments;
        }
    }

    private static readonly System.Text.RegularExpressions.Regex SafeId = new("^[A-Za-z0-9_-]{8,64}$");

    private static ChatMessage NewUserMessage(string? id, string text, IEnumerable<Attachment> attachments) => new()
    {
        Id = id is not null && SafeId.IsMatch(id) ? id : Guid.NewGuid().ToString("N"),
        Role = ChatRole.User,
        Content = text,
        Attachments = attachments.ToList(),
    };

    /// <summary>占用会话的运行槽位后，在后台准备消息并运行，保证同一会话同时只有一个任务。</summary>
    private void StartRun(string conversationId, string uiLanguage, IConfirmationHandler confirm, IHostEvents events, Func<string, ChatMessage?> prepare)
    {
        var conv = Store.Get(conversationId) ?? throw new InvalidOperationException("会话不存在");
        var cts = new CancellationTokenSource();
        if (!_runs.TryAdd(conversationId, cts))
        {
            cts.Dispose();
            throw new InvalidOperationException("该对话正在处理中");
        }
        ChatMessage? newUser;
        try
        {
            newUser = prepare(conversationId);
        }
        catch
        {
            _runs.TryRemove(conversationId, out _);
            cts.Dispose();
            throw;
        }
        ActiveRunsChanged?.Invoke(_runs.Count);
        _ = Task.Run(() => RunAsync(conv, newUser, uiLanguage, confirm, events, cts));
    }

    private async Task RunAsync(Conversation conv, ChatMessage? newUser, string uiLanguage, IConfirmationHandler confirm, IHostEvents events, CancellationTokenSource cts)
    {
        var id = conv.Id;
        var observer = new RunObserver(id, events);
        try
        {
            if (newUser is not null)
            {
                Store.AddMessages(id, new[] { newUser });
            }
            var stored = Store.GetMessages(id);
            var user = stored.LastOrDefault(m => m.Role == ChatRole.User) ?? throw new InvalidOperationException("没有可以回答的问题");
            var text = user.Content;
            var attachments = user.Attachments;

            var needsTitle = conv.TitleSource == "auto" && string.IsNullOrWhiteSpace(conv.Title);
            if (needsTitle)
            {
                // 先用问题开头作为临时标题，AI 总结出来后再替换
                var draft = text.Trim().Replace('\n', ' ');
                draft = draft.Length > 24 ? draft[..24] + "…" : draft;
                if (draft.Length == 0 && attachments.Count > 0)
                {
                    draft = attachments[0].FileName;
                }
                if (draft.Length > 0 && Store.SetAutoTitle(id, draft) && Store.Get(id) is { } drafted)
                {
                    events.Post(new { type = "conversation.updated", conversation = ConversationDto.From(drafted) });
                }
            }

            var workspace = _settings.ResolveWorkspace(conv.Workspace);
            var promptBuilder = new PromptBuilder(Memory, Skills, Episodes);
            var persona = PersonaForPrompt();
            var prompt = promptBuilder.Build(new PromptContext
            {
                Tone = persona.Tone,
                CallName = persona.CallName,
                AssistantName = persona.AssistantName,
                Mode = conv.Mode,
                UiLanguage = uiLanguage,
                TranslateFrom = conv.TranslateFrom,
                TranslateTo = conv.TranslateTo,
                Workspace = workspace,
                Permission = conv.Permission,
                Sandboxed = Security.On(Flyknit.Core.Security.SecuritySettings.Sandbox),
                Query = text,
                SemanticScores = conv.Mode == ConversationMode.Translate ? null : await SemanticScoresAsync(text, cts.Token),
                McpServers = conv.Mode == ConversationMode.Agent ? Mcp.PromptInfo() : Array.Empty<McpPromptInfo>(),
            });

            var scene = conv.Mode switch
            {
                ConversationMode.Translate => Scenes.Translate,
                ConversationMode.Chat => Scenes.Chat,
                _ => Scenes.Agent,
            };

            // 短期记忆：之前压缩过的部分用摘要代替，只发送摘要之后的消息
            var summary = conv.Summary;
            var recent = (IEnumerable<ChatMessage>)stored;
            var uptoIndex = conv.SummaryUpto is null ? -1 : stored.FindIndex(m => m.Id == conv.SummaryUpto);
            if (uptoIndex >= 0)
            {
                recent = stored.Skip(uptoIndex + 1);
            }
            else
            {
                summary = null; // 摘要覆盖的消息已不存在（编辑或重新生成过），摘要作废
            }

            // 任务计划跟着对话保存：上一轮没做完的计划带进这一轮，压缩上下文后原样附在摘要后面
            var plan = TaskPlan.Parse(conv.Plan);
            if (!TaskPlan.HasOpenSteps(plan))
            {
                plan.Clear();
            }

            ContextManager? context = null;
            var history = new List<ChatMessage>();
            if (conv.Mode == ConversationMode.Translate)
            {
                history.Add(ChatMessage.System(prompt));
                history.Add(user); // 翻译不需要上下文，避免把上一段译文混进来
            }
            else
            {
                context = new ContextManager(Server, prompt, summary, _contextLength) { Scene = scene, ModelId = conv.ModelId, Plan = () => plan };
                context.Progress += p => events.Post(new
                {
                    type = "context.compacting",
                    conversationId = id,
                    phase = p.Phase,
                    percent = p.Percent,
                    messages = p.MessagesCompacted,
                });
                context.Compacted += info =>
                {
                    Store.SetSummary(id, info.Summary, info.UptoMessageId);
                    Log.Info($"对话 {id} 上下文已压缩：{info.MessagesCompacted} 条消息，{info.TokensBefore} → {info.TokensAfter} tokens");
                    events.Post(new { type = "context.compacted", conversationId = id, uptoMessageId = info.UptoMessageId, tokensBefore = info.TokensBefore, tokensAfter = info.TokensAfter });
                };
                history.Add(ChatMessage.System(context.SystemPrompt));
                var window = TrimHistory(recent.ToList()).ToList();
                if (window.Count > 0 && window[0].Role != ChatRole.User)
                {
                    history.Add(ChatMessage.User(ContextManager.ContinueMarker));
                }
                history.AddRange(window);
            }

            var ctx = new ToolContext
            {
                Policy = _policy,
                ConversationId = id,
                UserRequest = text,
                Workspace = workspace,
                Permission = conv.Permission,
                Memory = Memory,
                Episodes = Episodes,
                Skills = Skills,
                Deleter = Security.On(Flyknit.Core.Security.SecuritySettings.DeleteProtection)
                    ? new RecycleBinDeleter()
                    : new PermanentDeleter(),
            Backup = Security.On(Flyknit.Core.Security.SecuritySettings.AutoBackup) ? _backup : null,
            Security = Security,
            };

            ctx.Plan.AddRange(plan);
            ctx.PlanChanged += updated =>
            {
                plan = updated.ToList();
                try
                {
                    Store.SetPlan(id, TaskPlan.Serialize(updated));
                }
                catch (Exception ex)
                {
                    Log.Warn("保存任务计划失败", ex);
                }
            };

            var loop = new AgentLoop(Server, Tools, confirm, _auditSink, AgentOptionsFor(), Approvals);
            var result = await loop.RunAsync(history, scene, ctx, observer, useTools: conv.Mode == ConversationMode.Agent, cts.Token, conv.ModelId, context);
            // 把本轮产出的文件和执行链路挂到最后一条回答上，重开会话时还能查
            var answer = result.NewMessages.LastOrDefault(m => m.Role == ChatRole.Assistant);
            if (observer.Outputs.Count > 0)
            {
                answer?.Outputs.AddRange(observer.Outputs);
                Library.IngestOutputs(observer.Outputs, id);
            }
            if (answer is not null && result.Trace is { } trace)
            {
                answer.TraceJson = Bridge.TraceDto.Serialize(trace, result.StopReason.ToString());
                // 回答是边生成边推给界面的，那时候链路还没走完，所以这里补发一次，
                // 不然耗时芯片只有切走再切回来（从库里重读）才看得到。
                events.Post(new
                {
                    type = "chat.trace",
                    conversationId = id,
                    messageId = answer.Id,
                    trace = answer.TraceJson,
                });
            }
            Store.AddMessages(id, result.NewMessages);
            if (answer is not null)
            {
                try
                {
                    Memory.RecordUsage(id, answer.Id, promptBuilder.MemoryIdsUsed, promptBuilder.MemoryTokens, promptBuilder.EpisodesUsed);
                }
                catch (Exception ex)
                {
                    Log.Warn("记录记忆使用情况失败", ex);
                }
                RecordSkillRun(id, answer.Id, result.NewMessages, result.StopReason == AgentStopReason.Completed, promptBuilder.SkillsPreloaded);
                if (conv.Mode == ConversationMode.Agent)
                {
                    RecordRunStats(id, answer.Id, result);
                }
            }
            if (context is { ContextLength: > 0 })
            {
                _contextLength = context.ContextLength;
            }

            events.Post(new
            {
                type = "chat.done",
                conversationId = id,
                stopReason = result.StopReason.ToString(),
                modelName = result.ModelName,
                usage = result.Usage is { } u ? new { promptTokens = u.PromptTokens, completionTokens = u.CompletionTokens } : null,
            });
            RunFinished?.Invoke(new RunFinishedInfo(id, Store.Get(id)?.Title ?? "", LastAnswer(result.NewMessages), result.StopReason));

            if (needsTitle && result.StopReason != AgentStopReason.Cancelled)
            {
                await SummarizeTitleAsync(id, text, result, uiLanguage, events);
            }

            // 长期记忆：办事任务结束后在后台复盘，提炼偏好、经验和可复用的做法。
            // 被用户中途停止的任务同样复盘——用户喊停往往说明做错了方向，这是最该记下来的教训（只记教训，不记成功经验）。
            if (_settings.EnableLearning && conv.Mode == ConversationMode.Agent && !LearningPolicy.From(Security).Nothing
                && Reflector.ShouldReflect(result.NewMessages, 0))
            {
                var previous = stored.Take(stored.Count - 1).LastOrDefault(m => m.Role == ChatRole.Assistant && m.Content.Length > 0)?.Content ?? "";
                _ = Task.Run(() => ReflectAsync(conv, workspace, text, previous, result.NewMessages, result.StopReason.ToString(), 0, uiLanguage, events));
            }
        }
        catch (Exception ex)
        {
            Log.Error($"对话 {id} 运行失败", ex);
            events.Post(new { type = "chat.error", conversationId = id, message = ex is GatewayException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}" });
            RunFinished?.Invoke(new RunFinishedInfo(id, Store.Get(id)?.Title ?? "", ex.Message, null));
        }
        finally
        {
            _runs.TryRemove(id, out _);
            cts.Dispose();
            ActiveRunsChanged?.Invoke(_runs.Count);
        }
    }

    /// <summary>
    /// 语义检索：把用户这句话和记忆比一比意思。服务端没配向量模型、超时或出错时返回 null（只按字面挑记忆），
    /// 最多等几秒，不会卡住对话。
    /// </summary>
    private async Task<IReadOnlyDictionary<string, double>?> SemanticScoresAsync(string query, CancellationToken ct)
    {
        if (Memory.Semantic is not { } index)
        {
            return null;
        }
        try
        {
            // 技能也一起比：和记忆共用这一次把用户的话向量化
            var scores = await index.ScoreAsync(query, ct, Skills.SemanticTexts());
            if (scores is null && index.LastError is { } error)
            {
                Log.Info($"语义检索暂不可用，按字面匹配：{error}");
            }
            return scores;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn("语义检索失败", ex);
            return null;
        }
    }

    /// <summary>Agent 循环的设置：步数上限来自本机设置，规划提醒、产出检查由 IT 在安全中心开关（没下发时默认开）。</summary>
    private AgentOptions AgentOptionsFor() => new()
    {
        MaxSteps = _settings.ResolveAgentMaxSteps(),
        PlanGuidance = Security.On(Flyknit.Core.Security.SecuritySettings.PlanGuidance),
        VerifyOutputs = Security.On(Flyknit.Core.Security.SecuritySettings.VerifyOutputs),
    };

    /// <summary>记一轮任务的效果（只有数字），记忆面板里的“任务效果”据此统计，用来对比规划提醒、产出检查开关前后。</summary>
    private void RecordRunStats(string conversationId, string messageId, AgentRunResult result)
    {
        try
        {
            Store.AddRunStats(new RunStatsRecord(messageId, conversationId, result.StopReason.ToString(), result.Steps, result.ToolCalls,
                result.OutputProblems, result.OutputProblemsAtEnd,
                Security.On(Flyknit.Core.Security.SecuritySettings.PlanGuidance),
                Security.On(Flyknit.Core.Security.SecuritySettings.VerifyOutputs),
                result.PlanNudged));
        }
        catch (Exception ex)
        {
            Log.Warn("记录任务效果失败", ex);
        }
    }

    /// <summary>这一轮加载过的学习技能：记下用得怎么样，该转正的转正、该退役的退役。</summary>
    /// <param name="preloaded">提示词里直接给出全文的技能（自动匹配上的），和模型自己 load_skill 的一样算用过。</param>
    private void RecordSkillRun(string conversationId, string messageId, IReadOnlyList<ChatMessage> messages, bool ok, IReadOnlyList<string> preloaded)
    {
        try
        {
            var used = Flyknit.Core.Skills.LearnedSkills.LoadedIn(messages).Concat(preloaded)
                .Distinct(StringComparer.OrdinalIgnoreCase).Select(Skills.FindAny).OfType<SkillInfo>().Where(s => s.IsLearned).ToList();
            var changed = LearnedSkillLedger.RecordRun(conversationId, messageId, used, ok);
            ApplySkillChanges(changed);
        }
        catch (Exception ex)
        {
            Log.Warn("记录技能使用情况失败", ex);
        }
    }

    private void ApplySkillChanges(IReadOnlyList<(string Name, string Status)> changed)
    {
        if (changed.Count == 0)
        {
            return;
        }
        foreach (var (name, status) in changed)
        {
            Log.Info($"学习技能 {name} → {status}");
        }
        SkillManager.Refresh();
        SkillsChanged?.Invoke();
    }

    /// <summary>一次运行结束（用于系统通知）。StopReason 为 null 表示出错。</summary>
    public event Action<RunFinishedInfo>? RunFinished;

    private static string LastAnswer(IReadOnlyList<ChatMessage> messages) =>
        messages.LastOrDefault(m => m.Role == ChatRole.Assistant && m.Content.Trim().Length > 0)?.Content.Trim() ?? "";

    private async Task ReflectAsync(Conversation conv, string workspace, string request, string previousAnswer, IReadOnlyList<ChatMessage> messages, string stopReason, int feedback, string uiLanguage, IHostEvents events)
    {
        try
        {
            var reflector = new Reflector(Server, Memory, Episodes, LearnedSkills)
            {
                Scene = Scenes.Agent,
                ModelId = conv.ModelId,
                Learned = LearnedSkillLedger,
                Policy = LearningPolicy.From(Security),
            };
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var report = await reflector.ReflectAsync(new ReflectionInput
            {
                ConversationId = conv.Id,
                Workspace = workspace,
                UserRequest = request,
                PreviousAnswer = previousAnswer,
                Messages = messages,
                StopReason = stopReason,
                Feedback = feedback,
                UiLanguage = uiLanguage,
            }, cts.Token);
            if (report is null)
            {
                Log.Warn($"对话 {conv.Id} 复盘失败：{reflector.LastError}");
                return;
            }
            Log.Info($"对话 {conv.Id} 复盘完成：新增记忆 {report.Added.Count} 条（其中更新 {report.Updated} 条），再次确认 {report.Reinforced} 条，拒绝 {report.Rejected} 条，未达写入门槛 {report.Filtered.Values.Sum()} 条（{string.Join("，", report.Filtered.Select(f => $"{f.Key} {f.Value}"))}），历史任务 {(report.Episode is null ? "无" : report.Episode.Title)}，技能 {report.SkillName ?? "无"}");
            if (report.SkillName is not null)
            {
                SkillManager.Refresh();
            }
            if (report.Added.Count > 0 || report.SkillName is not null)
            {
                events.Post(new
                {
                    type = "memory.learned",
                    conversationId = conv.Id,
                    items = report.Added.Select(a => new { kind = a.Kind.ToString().ToLowerInvariant(), text = a.Text }).ToList(),
                    skill = report.SkillName,
                });
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"对话 {conv.Id} 复盘异常", ex);
        }
    }

    /// <summary>
    /// 用户对回答点赞或点踩：保存评价，同步到历史任务；点踩时复盘这一轮，记下教训。
    /// </summary>
    public void Feedback(string conversationId, string messageId, int? value, string uiLanguage, IHostEvents events)
    {
        Store.SetFeedback(messageId, value);
        try
        {
            // 这条回答用到的记忆跟着记上评价：常被点踩的记忆以后排得靠后，满了先被淘汰
            Memory.ApplyFeedback(messageId, value);
        }
        catch (Exception ex)
        {
            Log.Warn("记录记忆评价失败", ex);
        }
        try
        {
            // 用过的学习技能也跟着记上评价：点踩算一次失败
            var skills = LearnedSkillLedger.ApplyFeedback(messageId, value).Select(Skills.FindAny).OfType<SkillInfo>();
            ApplySkillChanges(skills.Select(LearnedSkillLedger.Review).OfType<(string, string)>().ToList());
        }
        catch (Exception ex)
        {
            Log.Warn("记录技能评价失败", ex);
        }
        if (value is null)
        {
            return;
        }
        Episodes.SetFeedback(conversationId, value.Value);
        if (value.Value >= 0 || !_settings.EnableLearning || Store.Get(conversationId) is not { } conv)
        {
            return;
        }
        var messages = Store.GetMessages(conversationId);
        var index = messages.FindIndex(m => m.Id == messageId);
        var userIndex = index < 0 ? -1 : messages.FindLastIndex(index, m => m.Role == ChatRole.User);
        if (userIndex < 0)
        {
            return;
        }
        var nextUser = messages.FindIndex(userIndex + 1, m => m.Role == ChatRole.User);
        var turn = messages.Skip(userIndex + 1).Take((nextUser < 0 ? messages.Count : nextUser) - userIndex - 1).ToList();
        var previous = messages.Take(userIndex).LastOrDefault(m => m.Role == ChatRole.Assistant && m.Content.Length > 0)?.Content ?? "";
        var workspace = _settings.ResolveWorkspace(conv.Workspace);
        _ = Task.Run(() => ReflectAsync(conv, workspace, messages[userIndex].Content, previous, turn, "Completed", -1, uiLanguage, events));
    }

    /// <summary>整理记忆：把换了说法重复记下的条目合并。</summary>
    public async Task<ConsolidationReport> ConsolidateMemoryAsync(CancellationToken ct)
    {
        var consolidator = new MemoryConsolidator(Server, Memory) { Scene = Scenes.Agent };
        var report = await consolidator.RunAsync(ct);
        Log.Info($"整理记忆：合并 {report.Groups} 组共 {report.ItemsMerged} 条{(report.Errors.Count > 0 ? "，出错：" + string.Join("；", report.Errors) : "")}");
        return report;
    }

    private async Task SummarizeTitleAsync(string id, string text, AgentRunResult result, string uiLanguage, IHostEvents events)
    {
        try
        {
            var answer = result.NewMessages.LastOrDefault(m => m.Role == ChatRole.Assistant && m.Content.Length > 0)?.Content ?? "";
            var generator = new TitleGenerator(Server);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var title = await generator.GenerateAsync(text, answer, uiLanguage, cts.Token);
            if (string.IsNullOrWhiteSpace(title))
            {
                Log.Warn($"会话 {id} 标题生成失败：{generator.LastError}");
                return; // 保留临时标题
            }
            if (Store.SetAutoTitle(id, title) && Store.Get(id) is { } updated)
            {
                events.Post(new { type = "conversation.updated", conversation = ConversationDto.From(updated) });
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"会话 {id} 标题生成异常", ex);
        }
    }

    private List<ClientModel>? _models;
    private DateTime _modelsAt;

    /// <summary>输入框可选的模型列表，缓存 5 分钟。</summary>
    public async Task<List<ClientModel>> GetModelsAsync(bool refresh = false)
    {
        if (!refresh && _models is not null && DateTime.Now - _modelsAt < TimeSpan.FromMinutes(5))
        {
            return _models;
        }
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            _models = await Server.GetModelsAsync(cts.Token);
            _modelsAt = DateTime.Now;
        }
        catch (Exception ex)
        {
            Log.Warn("获取模型列表失败", ex);
            _models ??= new List<ClientModel>();
        }
        return _models;
    }

    /// <summary>只保留最近的消息，并保证从一条用户消息开始，避免工具消息失去对应的调用。</summary>
    private static IEnumerable<ChatMessage> TrimHistory(List<ChatMessage> messages)
    {
        if (messages.Count <= HistoryWindow)
        {
            return messages;
        }
        var start = messages.Count - HistoryWindow;
        while (start < messages.Count && messages[start].Role != ChatRole.User)
        {
            start++;
        }
        return messages.Skip(start);
    }

    /// <summary>
    /// 执行一条后台下发的指令：新建一个会话，把指令当成用户消息，走和员工自己点「发送」
    /// 完全相同的那套通道——会话进侧栏、自动打开、逐字回答与逐个工具调用在界面上实时呈现，
    /// 办完把最终结果回报给调用方（指令轮询器据此上报服务端）。
    ///
    /// 和员工自己发消息的区别只有一处：确认环节自动放行（IT 下发即授权）。但这只对「需要确认」
    /// 的那一档生效——被安全策略判为「阻止」的危险命令照样执行不了，每一步工具调用也照常写进审计。
    /// </summary>
    public async Task<InstructionOutcome> RunInstructionAsync(string prompt, string title, CancellationToken ct)
    {
        var displayTitle = string.IsNullOrWhiteSpace(title) ? "IT 指令" : $"IT 指令 · {title}";
        var conv = Store.Create(ConversationMode.Agent, displayTitle);
        Store.Rename(conv.Id, conv.Title); // 固定标题，不让 AI 自动总结覆盖
        var confirm = new AutoApprove();

        // 界面还没就绪（窗口没加载过 webview）时静默执行，照样受策略约束、写审计
        if (Events is not { } events)
        {
            return await RunInstructionHeadlessAsync(conv, prompt, confirm, ct);
        }

        // 捕获本轮运行结果：RunAsync 收尾时（成功或失败）都会触发 RunFinished
        var tcs = new TaskCompletionSource<RunFinishedInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFinished(RunFinishedInfo info)
        {
            if (info.ConversationId == conv.Id)
            {
                tcs.TrySetResult(info);
            }
        }
        RunFinished += OnFinished;
        using var reg = ct.Register(() => Stop(conv.Id));
        try
        {
            // 让会话出现在侧栏并自动打开，接着像正常发消息一样跑，过程实时可见
            events.Post(new { type = "conversation.updated", conversation = ConversationDto.From(conv) });
            events.Post(new { type = "app.openConversation", conversationId = conv.Id });
            Send(conv.Id, prompt, Array.Empty<Attachment>(), null, UiLanguage, confirm, events);
            var info = await tcs.Task;
            Log.Info($"指令会话 {conv.Id} 结束：{info.StopReason}");
            return ToOutcome(conv.Id, info.StopReason, info.Answer);
        }
        catch (Exception ex)
        {
            Log.Error($"执行下发指令失败（会话 {conv.Id}）", ex);
            return new InstructionOutcome(false, "", ex.Message, conv.Id);
        }
        finally
        {
            RunFinished -= OnFinished;
        }
    }

    /// <summary>把运行结果翻成给服务端回报用的结构。</summary>
    private static InstructionOutcome ToOutcome(string conversationId, AgentStopReason? stopReason, string answer)
    {
        var ok = stopReason == AgentStopReason.Completed;
        var error = ok ? "" : stopReason switch
        {
            AgentStopReason.Cancelled => "执行被取消",
            AgentStopReason.MaxSteps => "超过最大步数仍未完成",
            AgentStopReason.TooManyFailures => "连续多次失败后停止",
            AgentStopReason.Stuck => "反复执行同一个操作没有进展，已停止",
            _ => answer.Length > 0 ? answer : "执行失败",
        };
        return new InstructionOutcome(ok, answer, error, conversationId);
    }

    /// <summary>界面没就绪时的兜底：不推界面事件，静默跑同一套 Agent 循环。</summary>
    private async Task<InstructionOutcome> RunInstructionHeadlessAsync(Conversation conv, string prompt, IConfirmationHandler confirm, CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (!_runs.TryAdd(conv.Id, cts))
        {
            cts.Dispose();
            return new InstructionOutcome(false, "", "会话正忙", conv.Id);
        }
        ActiveRunsChanged?.Invoke(_runs.Count);
        try
        {
            var uiLanguage = _settings.ResolveUiLanguage();
            var user = NewUserMessage(null, prompt, Array.Empty<Attachment>());
            Store.AddMessages(conv.Id, new[] { user });

            var workspace = _settings.ResolveWorkspace(conv.Workspace);
            var persona = PersonaForPrompt();
            var scheduledPrompt = new PromptBuilder(Memory, Skills, Episodes);
            var systemPrompt = scheduledPrompt.Build(new PromptContext
            {
                Tone = persona.Tone,
                CallName = persona.CallName,
                AssistantName = persona.AssistantName,
                Mode = ConversationMode.Agent,
                UiLanguage = uiLanguage,
                Workspace = workspace,
                Permission = conv.Permission,
                Sandboxed = Security.On(Flyknit.Core.Security.SecuritySettings.Sandbox),
                Query = prompt,
                SemanticScores = await SemanticScoresAsync(prompt, cts.Token),
                McpServers = Mcp.PromptInfo(),
            });

            var context = new ContextManager(Server, systemPrompt, null, _contextLength) { Scene = Scenes.Agent, ModelId = conv.ModelId };
            context.Compacted += info => Store.SetSummary(conv.Id, info.Summary, info.UptoMessageId);
            var history = new List<ChatMessage> { ChatMessage.System(context.SystemPrompt), user };

            var ctx = new ToolContext
            {
                Policy = _policy,
                ConversationId = conv.Id,
                UserRequest = prompt,
                Workspace = workspace,
                Permission = conv.Permission,
                Memory = Memory,
                Episodes = Episodes,
                Skills = Skills,
                Deleter = Security.On(Flyknit.Core.Security.SecuritySettings.DeleteProtection)
                    ? new RecycleBinDeleter()
                    : new PermanentDeleter(),
                Backup = Security.On(Flyknit.Core.Security.SecuritySettings.AutoBackup) ? _backup : null,
                Security = Security,
            };

            var loop = new AgentLoop(Server, Tools, confirm, _auditSink, AgentOptionsFor(), Approvals);
            var result = await loop.RunAsync(history, Scenes.Agent, ctx, new HeadlessObserver(), useTools: true, cts.Token, conv.ModelId, context);
            Store.AddMessages(conv.Id, result.NewMessages);
            if (result.NewMessages.LastOrDefault(m => m.Role == ChatRole.Assistant) is { } done)
            {
                RecordSkillRun(conv.Id, done.Id, result.NewMessages, result.StopReason == AgentStopReason.Completed, scheduledPrompt.SkillsPreloaded);
                RecordRunStats(conv.Id, done.Id, result);
            }
            if (context.ContextLength > 0)
            {
                _contextLength = context.ContextLength;
            }
            Log.Info($"指令会话 {conv.Id} 结束（静默）：{result.StopReason}");
            return ToOutcome(conv.Id, result.StopReason, LastAnswer(result.NewMessages));
        }
        catch (Exception ex)
        {
            Log.Error($"执行下发指令失败（会话 {conv.Id}）", ex);
            return new InstructionOutcome(false, "", ex.Message, conv.Id);
        }
        finally
        {
            _runs.TryRemove(conv.Id, out _);
            cts.Dispose();
            ActiveRunsChanged?.Invoke(_runs.Count);
        }
    }

    /// <summary>后台下发的指令：IT 已授权，确认环节一律自动放行（安全策略仍会拦阻止级操作）。</summary>
    private sealed class AutoApprove : IConfirmationHandler
    {
        public Task<ConfirmChoice> ConfirmAsync(ConfirmRequest request, CancellationToken ct) =>
            Task.FromResult(ConfirmChoice.AllowOnce);
    }

    /// <summary>无界面运行，事件都丢弃（执行结果通过返回值拿）。</summary>
    private sealed class HeadlessObserver : IAgentObserver
    {
        public void OnContent(string delta) { }
        public void OnReasoning(string delta) { }
        public void OnAssistantMessage(ChatMessage message) { }
        public void OnToolStarted(ToolCall call, string summary, PolicyDecision decision) { }
        public void OnToolFinished(ToolCall call, ToolResult result, string decision) { }
        public void OnToolMessage(ChatMessage message) { }
        public void OnPlanUpdated(IReadOnlyList<PlanItem> plan) { }
    }

    public void Dispose()
    {
        _watchCts.Cancel();
        foreach (var cts in _runs.Values)
        {
            cts.Cancel();
        }
        _configTimer.Dispose();
        _keepAwakeTimer?.Dispose();
        _keepAwake.Dispose();
        Scheduler.Dispose();
        Skills.Dispose();
        Mcp.Dispose();
        Audit.FlushAsync().Wait(TimeSpan.FromSeconds(3));
        Audit.Dispose();
    }

    /// <summary>
    /// 安全中心里的一项被用户改了。本机记一条「配置变更」，同时上报服务端——
    /// 出了事能查到是谁、什么时候关掉的。
    /// </summary>
    public void RecordSettingChange(string key, string title, string before, string after)
    {
        var detail = $"「{title}」：{before} → {after}";
        // 服务端只认这几种 decision，用 auto + status 区分，老版本服务端也收得下
        Audit.Record(new AuditEntry
        {
            ToolName = "security_settings",
            Arguments = JsonSerializer.Serialize(new { key, from = before, to = after }),
            Risk = "auto",
            Decision = "auto",
            Status = "changed",
            Summary = detail,
        });
        try
        {
            Store.AddSecurityEvent(new SecurityEvent
            {
                Tool = "security_settings",
                Detail = detail,
                Decision = "changed",
            });
        }
        catch (Exception ex)
        {
            Log.Warn("保存配置变更记录失败", ex);
        }
    }

    /// <summary>
    /// 审计出口：危险命令拦截、用户放行和拒绝都记在本机一份，同时照常上报服务端。
    /// 自动执行的只读操作不记（量大且没有安全意义）。
    /// </summary>
    private sealed class RecordingAuditSink : IAuditSink
    {
        private readonly IAuditSink _inner;
        private readonly ConversationStore _store;

        public RecordingAuditSink(IAuditSink inner, ConversationStore store)
        {
            _inner = inner;
            _store = store;
        }

        public void Record(AuditEntry entry)
        {
            _inner.Record(entry);
            var decision = entry.Decision;
            if (decision == "auto" && IsNetworkAccess(entry))
            {
                // 白名单内自动放行的联网操作也记一笔：员工和 IT 都该能看到 AI 访问过哪些网站
                decision = "allowed";
            }
            if (decision is not ("blocked" or "approved" or "remembered" or "rejected" or "allowed"))
            {
                return;
            }
            try
            {
                _store.AddSecurityEvent(new SecurityEvent
                {
                    ConversationId = entry.ConversationId,
                    Scene = entry.Scene,
                    Tool = entry.ToolName,
                    Detail = Detail(entry),
                    Decision = decision,
                    Reason = entry.Decision == "blocked" ? entry.Summary : "",
                    CreatedAt = entry.OccurredAt,
                });
            }
            catch (Exception ex)
            {
                Log.Warn("保存安全记录失败", ex);
            }
        }

        private static bool IsNetworkAccess(AuditEntry entry)
        {
            var text = ArgumentText(entry);
            return entry.ToolName switch
            {
                "open_app" => NetworkPolicy.UrlsIn(text).Count > 0 || NetworkPolicy.BareHostsIn(text).Count > 0,
                "run_shell" => NetworkPolicy.IsNetworkCommand(text) || NetworkPolicy.UrlsIn(text).Count > 0,
                _ => false,
            };
        }

        /// <summary>open_app 的网址常在启动参数里（msedge https://...），只记软件名看不出访问了哪里。</summary>
        private static string Detail(AuditEntry entry) =>
            entry.ToolName == "open_app" ? ArgumentText(entry) : ToolDetail.From(entry.Arguments);

        private static string ArgumentText(AuditEntry entry)
        {
            try
            {
                using var doc = JsonDocument.Parse(entry.Arguments);
                var root = doc.RootElement;
                string Get(string key) => root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                return entry.ToolName == "open_app" ? $"{Get("name")} {Get("arguments")}".Trim() : Get("command");
            }
            catch (JsonException)
            {
                return "";
            }
        }
    }

    /// <summary>把一次运行中的事件转成界面消息。</summary>
    private sealed class RunObserver : IAgentObserver
    {
        private readonly string _id;
        private readonly IHostEvents _events;

        public RunObserver(string id, IHostEvents events)
        {
            _id = id;
            _events = events;
        }

        public void OnContent(string delta) => _events.Post(new { type = "chat.delta", conversationId = _id, text = delta });

        public void OnReasoning(string delta) => _events.Post(new { type = "chat.reasoning", conversationId = _id, text = delta });

        public void OnAssistantMessage(ChatMessage message) =>
            _events.Post(new { type = "chat.message", conversationId = _id, message = MessageDto.From(message) });

        public void OnToolMessage(ChatMessage message) =>
            _events.Post(new { type = "chat.message", conversationId = _id, message = MessageDto.From(message) });

        public void OnToolStarted(ToolCall call, string summary, PolicyDecision decision) => _events.Post(new
        {
            type = "tool.started",
            conversationId = _id,
            callId = call.Id,
            name = call.Name,
            // MCP 工具的参数名各家不一样，用工具自己给的一句话；内置工具照旧取 command / path 这些
            summary = !Flyknit.Core.Mcp.McpNames.IsMcp(call.Name) && ToolDetail.From(call.ArgumentsJson) is { Length: > 0 } detail ? detail : summary,
            // 原始参数：界面展开卡片时逐项列出来，让人看得到 AI 到底拿什么去调的
            args = call.ArgumentsJson.Length > 4000 ? call.ArgumentsJson[..4000] : call.ArgumentsJson,
            risk = decision.Level switch { RiskLevel.Blocked => "blocked", RiskLevel.Confirm => "confirm", _ => "auto" },
        });

        public void OnToolFinished(ToolCall call, ToolResult result, string decision) => _events.Post(new
        {
            type = "tool.finished",
            conversationId = _id,
            callId = call.Id,
            ok = result.Ok,
            output = result.Output,
            decision,
        });

        public void OnContextCompacted(CompactionInfo info)
        {
            // 由 ContextManager.Compacted 事件统一处理（保存摘要并通知界面）
        }

        /// <summary>本轮产出的文件，按出现顺序去重。</summary>
        public List<string> Outputs { get; } = new();

        public void OnNotice(string text) => _events.Post(new { type = "chat.notice", conversationId = _id, text });

        public void OnOutputsProduced(IReadOnlyList<OutputFile> files)
        {
            foreach (var f in files)
            {
                if (!Outputs.Contains(f.Path, StringComparer.OrdinalIgnoreCase))
                {
                    Outputs.Add(f.Path);
                }
            }
            _events.Post(new
            {
                type = "files.produced",
                conversationId = _id,
                files = files.Select(f => Bridge.OutputFileDto.From(f.Path)).ToList(),
            });
        }

        public void OnPlanUpdated(IReadOnlyList<PlanItem> plan) => _events.Post(new
        {
            type = "plan.updated",
            conversationId = _id,
            plan = plan.Select(p => new { step = p.Step, status = p.Status }).ToList(),
        });
    }
}

public sealed record RunFinishedInfo(string ConversationId, string Title, string Answer, AgentStopReason? StopReason);

/// <summary>一条下发指令在本机执行后的结果。</summary>
public sealed record InstructionOutcome(bool Ok, string Answer, string Error, string ConversationId);
