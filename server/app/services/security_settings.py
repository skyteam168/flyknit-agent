"""安全中心里每一项的定义、默认值，以及这台机器的用户能不能自己改。

设计上的关键一条：**开关归 IT 管，不归员工管。**

参考的那个界面来自开发者工具，跑在开发者自己的电脑上，所以所有开关归本地用户。
FlyknitBuddy 跑在纳管的工厂 PC 上，用户是车间员工，策略归 IT——照搬过来，员工
就能自己关掉沙箱，整个管控模型当场失效。

所以每一项带一个 locked：
  locked=True   界面上显示当前值和一把锁，改不了，鼠标移上去说明「由 IT 统一配置」
  locked=False  正常开关，改动存本地

默认只放开「改了也不降低安全线」的那几项（备份、通知）。要给某台机器更多权限，
IT 在后台针对那台机器单独放开，而不是全厂一起放。
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Any, Literal

SettingKind = Literal["bool", "int"]


@dataclass(frozen=True)
class Setting:
    key: str
    kind: SettingKind
    default: Any
    #: 默认是否锁住。锁住 = 只有 IT 能改
    locked: bool
    title: str
    #: 关掉它会怎样。界面在用户关闭时把这句话原样显示出来
    risk: str
    minimum: int | None = None
    maximum: int | None = None

    def coerce(self, value: Any) -> Any:
        """把外部传进来的值规整成合法值；不合法就退回默认值。"""
        if self.kind == "bool":
            if isinstance(value, bool):
                return value
            if isinstance(value, (int, float)):
                return bool(value)
            if isinstance(value, str):
                return value.strip().lower() in ("1", "true", "yes", "on")
            return self.default
        try:
            number = int(value)
        except (TypeError, ValueError):
            return self.default
        if self.minimum is not None:
            number = max(self.minimum, number)
        if self.maximum is not None:
            number = min(self.maximum, number)
        return number


#: 安全中心的全部条目。加一项就在这里加一行，界面和校验都跟着走，不用改别处。
SETTINGS: tuple[Setting, ...] = (
    # ---- 默认放开：改了也不降低安全线 ----
    Setting(
        key="auto_backup", kind="bool", default=True, locked=False,
        title="修改文件前自动备份",
        risk="关闭后 AI 覆盖文件将无法还原。删除仍会进回收站，但覆盖写不会。",
    ),
    Setting(
        key="backup_quota_mb", kind="int", default=512, locked=False,
        title="备份容量上限（MB）", minimum=64, maximum=20480,
        risk="调小之后较早的备份会被提前清掉。",
    ),
    Setting(
        key="notifications", kind="bool", default=True, locked=False,
        title="任务完成通知",
        risk="关闭后任务跑完不会提醒，需要自己回来看。",
    ),
    Setting(
        key="notification_sound", kind="bool", default=False, locked=False,
        title="通知提示音",
        risk="",
    ),
    # ---- 默认锁住：关掉会实打实降低安全性 ----
    Setting(
        key="sandbox", kind="bool", default=True, locked=True,
        title="工作区隔离",
        risk="关闭后 AI 可以在工作区之外读写文件。危险命令仍会拦截，但范围限制没有了。",
    ),
    Setting(
        key="delete_protection", kind="bool", default=True, locked=True,
        title="删除保护（回收站）",
        risk="关闭后 AI 删除的文件直接永久删除，不进回收站，无法恢复。",
    ),
    Setting(
        key="network_allowlist", kind="bool", default=True, locked=True,
        title="网络访问白名单",
        risk="关闭后 AI 可以访问任意网址。",
    ),
    Setting(
        key="system_tools", kind="bool", default=False, locked=True,
        title="系统级工具（注册表 / 服务 / 计划任务 / WMI）",
        risk="开启后 AI 可以改注册表、服务和计划任务。这些东西留在工作区之外，长期生效，隔离和删除保护都管不着。",
    ),
    Setting(
        key="batch_delete_threshold", kind="int", default=20, locked=True,
        title="批量删除确认阈值", minimum=1, maximum=10000,
        risk="调大之后，一次删除更多文件也不再额外确认。",
    ),
    # ---- 学习策略：AI 自动学习哪些东西。默认都学、由 IT 统一管 ----
    # 关掉只是不让 AI 自己记；员工在记忆面板里亲手加的不受影响，已经记下的也不会被删。
    Setting(
        key="learn_preferences", kind="bool", default=True, locked=True,
        title="学习用户偏好",
        risk="关闭后 AI 不再记住员工的习惯和要求（如报表存哪、用什么格式），每次都要重新交代。",
    ),
    Setting(
        key="learn_facts", kind="bool", default=True, locked=True,
        title="学习常用信息",
        risk="关闭后 AI 不再记住路径、系统地址、同事称呼这类信息。",
    ),
    Setting(
        key="learn_experience", kind="bool", default=True, locked=True,
        title="学习经验与教训",
        risk="关闭后 AI 不再总结做成的办法和踩过的坑，同样的错可能再犯。",
    ),
    Setting(
        key="learn_episodes", kind="bool", default=True, locked=True,
        title="记录历史任务",
        risk="关闭后不再保存做过的任务及当时的做法；技能也因此无法自动沉淀。",
    ),
    Setting(
        key="learn_skills", kind="bool", default=True, locked=True,
        title="自动沉淀技能",
        risk="关闭后同类任务做成多次也不会自动总结成技能。",
    ),
    # ---- 任务质量：只提醒、不拦截，出错时自动退回原来的做法。默认开；想先小范围试，可以全厂关、单台机器开 ----
    Setting(
        key="plan_guidance", kind="bool", default=True, locked=True,
        title="规划提醒",
        risk="关闭后 AI 做多步任务时不再被提醒先列计划，卡住时也不提醒先调整计划。",
    ),
    Setting(
        key="verify_outputs", kind="bool", default=True, locked=True,
        title="检查产出文件",
        risk="关闭后 AI 生成的文件不再自动检查（是否存在、能否打开、行数页数），文件没保存成功或打不开时可能发现不了。",
    ),
    Setting(
        key="long_tasks", kind="bool", default=True, locked=True,
        title="长任务模式",
        risk="关闭后员工不能开长任务模式：每次任务最多走设置里的步数，到了就停下等人说「继续」。"
             "开着时，开了长任务模式的对话步数上限放宽到 300 步，到上限会自动接着做（最多再续 5 次），每一轮仍受单次任务的 token 预算约束。",
    ),
    Setting(
        key="task_token_budget", kind="int", default=0, locked=True,
        title="单个任务 token 上限（万）", minimum=0, maximum=1000000,
        risk="一件事（一个任务）所有轮次合计最多用多少 token，单位万：员工说一句话后 AI 连着做的几轮、长任务模式的自动续跑、"
             "中断后续跑都算同一个任务；手机、钉钉、定时任务、指令中心、PR 自动修复每次触发各算一个。用完后不再自动续跑，"
             "这一轮也不会超过剩下的额度，任务记成「任务额度用完」；员工再说话算新任务。单次任务的上限照样生效。默认 0 = 不限。",
    ),
    Setting(
        key="watchdog", kind="bool", default=False, locked=True,
        title="看门狗：检查任务有没有进展",
        risk="打开后，员工端每分钟看一眼正在跑的任务：一直在动、却十几分钟没有新进展（没有新操作、计划没推进、没产出文件）时，"
             "请便宜模型（title 场景，用量记在「看门狗」用途下）看一眼是不是卡住或跑偏了，是就提醒 AI 换办法、通知员工。"
             "命令执行中（编译、跑测试）、等员工确认、暂停时不计时。关着时只有原来的「同一个操作反复做」检测。",
    ),
    Setting(
        key="watchdog_auto_stop", kind="bool", default=False, locked=True,
        title="看门狗：提醒后仍没进展就自动停下",
        risk="需要先打开「看门狗」。打开后，提醒过一次、再过一段时间程序和模型仍都认定没进展的任务会被自动停下（和员工点停止一样，"
             "已经做完的不会撤回），任务记成「看门狗：长时间没有进展」。关着时只提醒和通知，由员工决定停不停。模型判断失败时从不自动停。",
    ),
    Setting(
        key="watchdog_minutes", kind="int", default=10, locked=True,
        title="看门狗：多久没进展算可疑（分钟）", minimum=5, maximum=60,
        risk="任务一直在动、却连续这么多分钟没有新进展，才请模型判断。长任务模式的对话按 2 倍算。调小会更早提醒，也更容易误报。",
    ),
    Setting(
        key="router", kind="bool", default=False, locked=True,
        title="调度：归档追问、合并重复事件",
        risk="打开后，钉钉 / 企业微信里同一个人有事在办时，新消息先由便宜模型（title 场景，用量记在「调度」用途下）判断："
             "是追问就交给正在做的那件（不再新开对话），问进度就按任务记录直接回复（不跑 AI），是新的事照常排队。"
             "事件触发同样内容 10 分钟内只处理一次。调度只做归档决定，不执行任何操作、不放宽权限。手机端不受影响。",
    ),
    Setting(
        key="router_split", kind="bool", default=False, locked=True,
        title="调度：一句话里的几件事分开做",
        risk="打开后，员工一句话里像有好几件互相独立的事时，会问一句「要分开做吗？」，员工点了才拆："
             "每件事在自己的对话里做，原对话汇总进度、合并确认（高危操作仍要逐条点）。不会自动拆。",
    ),
    Setting(
        key="router_parallel", kind="int", default=1, locked=True,
        title="分开做时同时做几件", minimum=1, maximum=3,
        risk="1 = 一件接一件（默认）。调大后几件同时做，读可以并行，写文件、跑会改东西的命令仍然排队一次一个；"
             "同时做会同时用 token，也会同时弹确认。",
    ),
    Setting(
        key="run_token_budget", kind="int", default=200, locked=True,
        title="单次任务 token 上限（万）", minimum=0, maximum=100000,
        risk="一次办事任务（员工说一句话引起的一轮）最多用多少 token，输入 + 输出累计，单位万；用到 70% 提醒 AI 收尾，用完就停下总结、等员工说「继续」。"
             "每调用一次模型都会把整段上下文算一遍，读大文件、几十步的任务很容易累计到几百万。开了「长任务模式」的对话按 5 倍算，到上限自动接着做。"
             "填 0 表示不限（只受每日额度约束）。调大会让单个任务花得更多。",
    ),
    Setting(
        key="browser_tools", kind="bool", default=False, locked=True,
        title="浏览器操作（AI 浏览器）",
        risk="打开后，办事模式下 AI 能在一个看得见的浏览器窗口里打开网页、读内容、点击、填表（公司内部的网页系统最有用）。"
             "外部网站、提交 / 保存 / 删除类按钮、身份证银行卡号会先问员工；密码和验证码一律不代填；「仅可查看」权限下不能填写和提交。默认关闭。",
    ),
    Setting(
        key="background_commands", kind="bool", default=False, locked=True,
        title="后台命令（调试用）",
        risk="打开后，办事模式下 AI 能在后台启动一直运行的程序（开发服务器、监听编译），随时查看输出、用完停掉，用来调试程序和网页。"
             "命令本身仍按权限和命令规则判定（该确认的照样确认、该拦的照样拦）；每个对话最多同时 4 个，员工可以在右侧「终端」里随时停止，"
             "删掉对话、退出程序时全部停掉。默认关闭。",
    ),
    Setting(
        key="developer_tools", kind="bool", default=False, locked=True,
        title="开发者工具（写程序用）",
        risk="打开后，办事模式下 AI 多了按内容搜代码（grep）、一次改多处（multi_edit）和 git 工具（查看状态 / 改动 / 历史、提交），"
             "这类任务走「编程任务」场景（后台没单独配模型时用「任务」的模型），输入框可以用 /init 生成项目说明 FLYKNIT.md。"
             "提交照样按权限和命令规则确认；推送、强制推送、删分支、丢弃修改等 git 操作按高危逐次确认。只给写程序的同事打开。默认关闭。",
    ),
    Setting(
        key="subagents", kind="bool", default=False, locked=True,
        title="子代理（AI 开分身干活）",
        risk="打开后，办事模式下 AI 可以按任务临时开子代理（spawn_agent）：把翻代码、查资料这类要很多步的调研交给子代理，"
             "几个只读子代理可以同时跑，做完把结果交回主助手。子代理用的是同一台电脑的模型额度、同一套权限和确认规则（只会更少）："
             "只读子代理只能查看、不弹确认；能改东西的一次一个，每个操作照常要员工确认。不能再开子代理、不能向员工提问、不写长期记忆。"
             "一个任务最多开 8 个，会多用 token。默认关闭。",
    ),
    Setting(
        key="cli_access", kind="bool", default=False, locked=True,
        title="命令行 / IDE 接入",
        risk="打开后，这台电脑可以用 flyknit 命令行（flyknit -p \"要做的事\"，给脚本和 CI 用）和 ACP 服务（flyknit acp，"
             "让 Zed、JetBrains 等开发工具把 FlyknitBuddy 当 AI 助手用）无界面地执行办事任务。"
             "用的是员工端的登录和同一套安全设置：权限档、命令规则、IT 工具规则、额度、审计都一样，审计里来源记为 cli / ide。"
             "命令行默认拒绝所有需要确认的操作（加 --allow-writes 只放行普通写入）；IDE 里需要确认的操作由 IDE 弹确认框；"
             "删除、覆盖、改系统设置等高危操作只能逐次确认、不能设成自动。没有长期记忆、浏览器、连接器。只给写程序的同事打开。默认关闭。",
    ),
    # ---- 沙箱任务（本机 Docker）：见 docs/sandbox-execution-design.md ----
    Setting(
        key="sandbox_tasks", kind="bool", default=False, locked=True,
        title="沙箱任务（本机 Docker）",
        risk="打开后，开了开发者工具、装了 Docker Desktop 的电脑可以把写代码的任务交给沙箱：AI 在一次性的 Docker 容器里拉代码、改代码、"
             "装依赖、跑测试，全程不弹确认（影响出不了容器，员工的工作副本不会被碰）；做完只能把任务分支（flyknit/…）推到 GitHub 并建 PR，"
             "不能推 main、不能强推，PR 由人审核合并。容器里没有 GitHub 令牌，推送用员工自己的登录在容器外完成。默认关闭。",
    ),
    Setting(
        key="sandbox_network", kind="bool", default=True, locked=True,
        title="沙箱可以上网",
        risk="沙箱容器能不能访问网络（装依赖、拉代码都要）。关掉后容器完全断网，大多数开发任务做不了。",
    ),
    Setting(
        key="sandbox_allow_ci_changes", kind="bool", default=False, locked=True,
        title="沙箱可以改 CI 配置",
        risk="打开后，沙箱任务推出去的分支可以包含 .github/workflows 等 CI 配置的改动。CI 配置能拿到仓库的密钥，默认不允许。",
    ),
    Setting(
        key="sandbox_cpus", kind="int", default=4, locked=True, minimum=1, maximum=32,
        title="沙箱 CPU 核数",
        risk="每个沙箱容器最多用几个 CPU 核。",
    ),
    Setting(
        key="sandbox_memory_gb", kind="int", default=8, locked=True, minimum=1, maximum=128,
        title="沙箱内存（GB）",
        risk="每个沙箱容器最多用多少内存。",
    ),
    # ---- 运维代理自升级：见 docs/agent-self-update.md。代理以 SYSTEM 身份把自己换成新版本，默认关 ----
    Setting(
        key="agent_self_update", kind="bool", default=False, locked=True,
        title="运维代理自动升级",
        risk="打开后，运维代理（FlyknitAgent，SYSTEM 身份的 Windows 服务）替员工端升级时，如果新版本里带的代理比自己新，"
             "会在空闲时把自己也换成新版本：只用代理自己下载并校验过的员工端安装包里的代理，只升不降；"
             "新版本启动后 3 分钟内没连上服务器就自动换回原版本，同一个版本失败两次就不再尝试。代理版本每次变化都记审计。"
             "关闭时代理只替员工端升级、补命令行 flyknit 的 PATH，自己不变。默认关闭。",
    ),
    # ---- 个性化：员工可以在设置里选回复语气、给 AI 起名字。这两项决定哪些能选 ----
    Setting(
        key="persona_playful", kind="bool", default=True, locked=True,
        title="允许玩笑类语气（天马行空、毒舌吐槽）",
        risk="关闭后这两种语气不能选，已经选了的按默认语气回答。",
    ),
    Setting(
        key="persona_custom", kind="bool", default=True, locked=True,
        title="允许自定义语气和 AI 的名字",
        risk="关闭后员工不能自己写语气说明、不能给 AI 改名字，已经设置的按默认处理。",
    ),
    # ---- 锁屏运行：阻止电脑睡眠，让定时任务和下发的指令在锁屏后照常执行 ----
    Setting(
        key="keep_awake", kind="bool", default=True, locked=True,
        title="允许锁屏运行（阻止电脑睡眠）",
        risk="关闭后电脑按系统电源设置正常睡眠，睡着时定时任务不会执行、收不到下发的指令。",
    ),
    # ---- 截图提问：框选屏幕的一块发给 AI。屏幕上可能有敏感信息，IT 可以统一关掉 ----
    Setting(
        key="screenshot_ask", kind="bool", default=True, locked=True,
        title="允许截图提问",
        risk="关闭后悬浮球菜单和快捷键里的「截图提问」不可用。员工仍然可以自己粘贴截图。",
    ),
    Setting(
        key="keep_screen_on", kind="bool", default=True, locked=True,
        title="允许保持屏幕常亮",
        risk="关闭后“保持屏幕常亮”按“熄屏后保持唤醒”处理，屏幕照常熄灭，比较省电。",
    ),
    # ---- 专家派遣：主助手自己判断、把子任务交给专家去做。默认关，IT 想好了再开 ----
    Setting(
        key="expert_delegation", kind="bool", default=False, locked=True,
        title="允许主助手派遣专家",
        risk="开启后主助手可以自行把子任务派给已发布、允许派遣的专家执行（每轮最多 3 次，有 token 和时间上限），"
             "专家会用自己的工具读写数据，员工不一定逐一看到每一步。",
    ),
    # ---- 选项提问：信息不够时 AI 给几个选项让员工点选，不用打字。默认开 ----
    Setting(
        key="ask_user", kind="bool", default=True, locked=True,
        title="AI 用选项向员工提问",
        risk="关闭后 AI 缺信息时只能在回答里用文字提问，员工要自己打字回复，任务也要等下一条消息才能继续。",
    ),
    # ---- 会话信息面板：员工能不能展开看上下文的详细拆分（系统提示各段、每个连接器的工具占多少）。默认能 ----
    Setting(
        key="context_details", kind="bool", default=True, locked=True,
        title="员工可以查看上下文用量的详细拆分",
        risk="关闭后员工在「会话信息」里只看到总用量和「整理对话」，看不到系统提示、记忆、技能、各连接器分别占多少。",
    ),
)

BY_KEY = {s.key: s for s in SETTINGS}


def defaults() -> dict[str, Any]:
    return {s.key: s.default for s in SETTINGS}


def sanitize(values: dict | None) -> dict[str, Any]:
    """只保留认识的键，并把值规整到合法范围。未知的键直接丢掉。"""
    clean: dict[str, Any] = {}
    for key, value in (values or {}).items():
        setting = BY_KEY.get(key)
        if setting is not None:
            clean[key] = setting.coerce(value)
    return clean


def sanitize_locks(values: dict | None) -> dict[str, bool]:
    clean: dict[str, bool] = {}
    for key, value in (values or {}).items():
        if key in BY_KEY:
            clean[key] = bool(value)
    return clean


def effective(global_values: dict | None = None,
              device_values: dict | None = None,
              global_locks: dict | None = None,
              device_locks: dict | None = None,
              department_values: dict | None = None,
              department_locks: dict | None = None) -> dict[str, dict[str, Any]]:
    """
    算出这台机器最终看到的每一项：值是多少、能不能自己改。

    优先级：这台机器的设置 > 所在部门的设置 > 全厂设置 > 代码里的默认值。锁也是同样的顺序，
    所以「全厂锁住、研发部放开」「研发部放开、这台再锁住」都能表达。
    """
    values = {**defaults(), **sanitize(global_values), **sanitize(department_values), **sanitize(device_values)}
    locks = {s.key: s.locked for s in SETTINGS}
    locks.update(sanitize_locks(global_locks))
    locks.update(sanitize_locks(department_locks))
    locks.update(sanitize_locks(device_locks))

    return {
        s.key: {
            "value": values[s.key],
            "locked": locks[s.key],
            "kind": s.kind,
            "title": s.title,
            "risk": s.risk,
            "default": s.default,
            **({"min": s.minimum, "max": s.maximum} if s.kind == "int" else {}),
        }
        for s in SETTINGS
    }


def describe() -> list[dict[str, Any]]:
    """给管理端用的条目清单，知道有哪些可配、默认锁不锁。"""
    return [
        {
            "key": s.key,
            "kind": s.kind,
            "default": s.default,
            "locked_by_default": s.locked,
            "title": s.title,
            "risk": s.risk,
        }
        for s in SETTINGS
    ]
