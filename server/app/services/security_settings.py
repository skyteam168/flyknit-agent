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
              device_locks: dict | None = None) -> dict[str, dict[str, Any]]:
    """
    算出这台机器最终看到的每一项：值是多少、能不能自己改。

    优先级：这台机器的设置 > 全厂设置 > 代码里的默认值。锁也是同样的顺序，
    所以「全厂锁住、这台放开」是能表达的——这正是按机器单独授权的用法。
    """
    values = {**defaults(), **sanitize(global_values), **sanitize(device_values)}
    locks = {s.key: s.locked for s in SETTINGS}
    locks.update(sanitize_locks(global_locks))
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
