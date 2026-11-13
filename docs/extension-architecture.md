# 扩展架构：底座 + 功能模块

状态：已采用（2026-11）。适用于员工端 Core、WPF 宿主、桌面 Web UI、服务端。

## 1. 为什么

团队模式第 1、2 期落地时，每加一个能力都往底座里加分支：

- `AgentHost.RunAsync` 里有一串 `if (team != null)`：改提示词、加工具、去掉子代理、改用量上限；
- 派遣器 `ExpertDispatcher` 专门认识「团队」；
- 界面桥 `WebBridge` 的大 `switch` 里加了 6 个 `case`；
- `store.ts` 的事件 `switch` 里加了 6 个 `case`；
- 服务端子任务路由里写了团队规则。

专家、派遣、子代理、团队，以后还有部门流程、定时团队任务……照这样下去，底座越来越胖，改一处牵动全身。

## 2. 原则

1. **底座只认扩展点，不认具体功能。** 底座指：
   - 办事循环 `AgentLoop`；
   - 安全闸门（`blocked_patterns` → `CommandAnalyzer` → 权限 → 授权规则）；
   - 工具表 `ToolRegistry`；
   - 网关；
   - 确认与审计。
2. **能用内容扩展的，不写代码。** 新部门、新专家、新团队靠后台配置；业务能力靠技能（`SKILL.md`）和连接器（MCP）。只有新的「编排方式」才写成功能模块。
3. **一个功能一个文件夹，接口窄。** 功能模块通过下面几个扩展点接进来，自己管自己的状态。
4. **扩展只做加法，安全只做减法。**
   - 扩展能加的：提示词、本轮参考资料、工具、模型、用量上限；
   - 扩展不能动：确认、拦截、审计、权限；
   - 对专家的约束（`DispatchConstraints`）只会更严。
5. **一个扩展坏了不影响底座。** 扩展准备时出错只算它这一轮缺席，并记日志；任务照常跑。
6. **关得掉。** 每个功能模块对应安全中心的开关。关着时它不出场，底座行为和没有这个功能时一样。

## 3. 扩展点

| 层 | 扩展点 | 位置 | 功能模块做什么 |
| --- | --- | --- | --- |
| Core | 运行扩展 `IRunExtension<RunScope>` | `Flyknit.Core/Agent/Extensions/RunExtensions.cs` | 每轮办事开始前决定要不要出场；加提示词、参考资料、工具、模型、上限；声明和谁互斥；一轮结束时收尾 |
| Core | 派遣约束 `DispatchConstraints` | `Flyknit.Core/Experts/Delegation/DispatchConstraints.cs` | 派专家时另加约束（开关、禁用工具类别、只读、模型、次数、开工授权）；派遣器不认识调用方是谁 |
| Core | 工具类别 `ToolKinds` | `Flyknit.Core/Tools/ToolKinds.cs` | 按类别（命令 / 写文件 / 浏览器 / 连接器 / 开发者工具）关掉一批工具 |
| 宿主 | 界面桥模块 `IBridgeModule` | `Flyknit.Core/Hosting/BridgeModules.cs` | 声明自己处理哪些界面方法；`WebBridge` 先查注册表，查不到才走自己的 `switch` |
| Web | 功能模块 `registerFeature` | `client/web/src/store.ts`、`client/web/src/features/` | 订阅一类宿主事件（按前缀）、一轮结束时收尾、应用设置变了时跟着变；状态放在模块自己的 `reactive` 里 |
| 服务端 | 功能服务 | `server/app/services/<功能>_service.py` | 功能规则（开关、范围、次数）集中在服务里；路由只调一个函数 |

### 3.1 运行扩展

```csharp
public interface IRunExtension<in TScope> where TScope : RunScope
{
    string Id { get; }
    IReadOnlyCollection<string> Excludes { get; }   // 出场后哪些扩展这一轮不出场
    ValueTask<RunContribution?> PrepareAsync(TScope scope, CancellationToken ct);   // null = 不出场
}
```

- **调用时机**：`AgentHost.RunAsync` 在定下场景之后调用 `RunExtensionPipeline.PrepareAsync`，把结果合进这一轮：
  - 系统提示词追加在末尾；
  - 参考资料跟着用户消息存下来；
  - 工具在专家对话里还要按专家的限制再筛一遍；
  - 模型的优先级是：对话指定 → 专家指定 → 扩展给的；
  - 有多个上限时取最小的。
- **出场顺序**：按注册顺序问。先出场的扩展排除掉的，后面的不再问；后出场的扩展排除掉先出场的，先出场的那份作废，并调用它的收尾。
- **晚绑定**：`RunScope.Binding` 里的工具表、模型要组装完才知道。扩展加的工具在执行时再取，例如子代理要继承主助手最终的工具表。
- **收尾**：`RunAssembly.Dispose()` 在 `finally` 里调用。不管成功、失败还是停止，每个扩展的 `OnEnd` 都会被调用，且只调一次。

目前注册的扩展（`AgentHost` 构造函数里）：

| 编号 | 实现 | 出场条件 | 互斥 |
| --- | --- | --- | --- |
| `team` | `Teams/TeamFeature.cs` | 团队对话、团队模式开着、团队还在范围内 | 排除 `delegation`、`subagents` |
| `delegation` | `Experts/Delegation/DelegationRunExtension.cs` | 普通办事对话、「派遣专家」开着、挑得出候选专家 | — |
| `subagents` | `Agent/SubAgents/SubAgentRunExtension.cs` | 办事、「子代理」开着 | — |

### 3.2 一个功能模块长什么样（以团队为例）

| 层 | 文件 |
| --- | --- |
| Core | `Teams/`：`TeamFeature`（运行扩展 + 界面桥模块 + 设置）、`TeamPlan`、`TeamScheduler`、`TeamBoard`、`TeamTools` |
| 宿主 | `AgentHost` 构造函数里两行注册，没有别的团队代码 |
| Web | `features/teams/`：`store.ts`（状态、事件、操作）、`TeamsPage` / `TeamEditor` / `TeamPlanCard` / `TeamPanel`、`meta.ts` |
| 服务端 | `routers/teams.py`、`services/team_service.py`；子任务路由只在带 `team_id` 时调 `team_service.check_dispatch` |
| 后台 | `admin/src/views/Teams.vue` |

### 3.3 还留在底座里的（有意的）

下面这些属于宿主级的契约，以后再说是否收拢：

- **会话上的 `TeamId`**：和 `ExpertId` 一样，是对话的属性。
- **应用状态里的 `teams` / `teamCreate` / `teamMaxMembers` 三个开关**：和 `developerTools` 等一样，是平铺的状态字段。
- **界面上的挂载点**：
  - 侧栏入口；
  - 右侧面板的「团队」页签；
  - 输入框上方的计划卡。

  宿主组件直接引用功能模块的组件。等功能多到值得做「界面插槽」再做，现在做是过度设计。

## 4. 新功能怎么加

1. **先问能不能不写代码。** 能用技能、连接器或专家配置实现的，不写功能模块。
2. **需要改每轮办事的，写一个运行扩展。**
   - 放在 Core 里，能单测；
   - 在 `AgentHost` 构造函数里注册；
   - 互斥写在 `Excludes` 里，不要在宿主里写 `if`。
3. **需要派专家的，用 `DispatchConstraints`**，不要给派遣器加参数。
4. **需要界面接口的，实现 `IBridgeModule`**，在 `AgentHost` 里 `Register`。不要往 `WebBridge` 的 `switch` 里加。
5. **界面：**
   - 在 `client/web/src/features/<功能>/` 下建文件夹，用 `registerFeature` 订阅事件；
   - 在 `features/index.ts` 加一行；
   - 不要往 `store.ts` 里加功能分支。
6. **服务端：**
   - 规则放 `services/<功能>_service.py`，路由只调用；
   - 给员工端下发条目的接口照旧要过范围检查（CLAUDE.md 5.1）。
7. **安全中心加开关**，默认关、锁定；关着时扩展不出场。

## 5. 验证

- **Core**：`RunExtensionTests`（出场、合并、互斥、出错隔离、取消、收尾）、`DelegationTests` 里的功能模块测试（团队出场与互斥、关着不出场、服务端连不上用缓存、界面桥模块、派遣扩展）。
- **宿主**：WPF 在 Linux 上编不了。改了 `Flyknit.Client` 后：
  - 以 Windows CI 为准；
  - 本地可以用 NuGet 上的 `Microsoft.WindowsDesktop.App.Ref` 做语义编译检查。
- **Web**：`npm run build`（类型 + 三语检查）；mock 宿主里能完整演示一次团队任务。
