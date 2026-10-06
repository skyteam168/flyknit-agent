"""
运维代理的任务目录。

只能下发这里列出的任务，参数也只能在给定的范围里选——后台不能拼任意命令发到员工
电脑上。真要加新能力，是在这里加一种任务、在代理里加一个执行器，走代码评审，
而不是在网页上敲一段脚本。
"""

from __future__ import annotations

from typing import Literal

from pydantic import BaseModel, Field, ValidationError

CLEAN_TARGETS = ("windows_temp", "user_temp", "recycle_bin", "browser_cache", "thumbnails", "update_cache")
REPAIR_ACTIONS = ("dism", "sfc", "network", "windows_update")


class CollectInfoParams(BaseModel):
    pass


class CleanParams(BaseModel):
    targets: list[Literal[CLEAN_TARGETS]] = Field(  # type: ignore[valid-type]
        default_factory=lambda: ["windows_temp", "user_temp", "recycle_bin", "browser_cache", "thumbnails"],
        min_length=1,
    )


class OptimizeParams(BaseModel):
    clean: bool = True
    flush_dns: bool = True
    optimize_disks: bool = True


class InstallParams(BaseModel):
    package_id: int


class RepairParams(BaseModel):
    actions: list[Literal[REPAIR_ACTIONS]] = Field(default_factory=lambda: ["dism", "sfc"], min_length=1)  # type: ignore[valid-type]


class RestartParams(BaseModel):
    delay_minutes: int = Field(default=5, ge=1, le=60)
    #: 显示在员工电脑上的倒计时提示
    message: str = Field(default="IT 将重启这台电脑以完成维护，请保存好正在编辑的文件。", max_length=120)


CATALOG: dict[str, tuple[str, type[BaseModel]]] = {
    "collect_info": ("采集电脑信息", CollectInfoParams),
    "clean": ("清理缓存", CleanParams),
    "optimize": ("系统提速", OptimizeParams),
    "install": ("安装软件", InstallParams),
    "repair": ("系统修复", RepairParams),
    "restart": ("重启电脑", RestartParams),
}

CLEAN_LABELS = {
    "windows_temp": "系统临时文件",
    "user_temp": "用户临时文件",
    "recycle_bin": "回收站",
    "browser_cache": "浏览器缓存",
    "thumbnails": "缩略图缓存",
    "update_cache": "Windows 更新下载缓存",
}
REPAIR_LABELS = {
    "dism": "修复系统映像（DISM）",
    "sfc": "检查系统文件（SFC）",
    "network": "重置网络",
    "windows_update": "重置 Windows 更新组件",
}


class TaskError(ValueError):
    pass


def validate(kind: str, params: dict | None) -> dict:
    """校验并补齐默认值，返回规整后的参数。"""
    entry = CATALOG.get(kind)
    if entry is None:
        raise TaskError(f"不支持的任务类型：{kind}")
    try:
        model = entry[1].model_validate(params or {})
    except ValidationError as exc:
        first = exc.errors()[0]
        where = ".".join(str(p) for p in first.get("loc", ()))
        raise TaskError(f"任务参数不正确：{where} {first.get('msg', '')}".strip()) from exc
    data = model.model_dump()
    if kind in ("clean", "repair"):
        key = "targets" if kind == "clean" else "actions"
        data[key] = list(dict.fromkeys(data[key]))
    return data


def title_of(kind: str, params: dict, package_name: str = "") -> str:
    base = CATALOG[kind][0]
    if kind == "install" and package_name:
        return f"{base}：{package_name}"
    if kind == "clean":
        return f"{base}：" + "、".join(CLEAN_LABELS[t] for t in params["targets"])
    if kind == "repair":
        return f"{base}：" + "、".join(REPAIR_LABELS[a] for a in params["actions"])
    if kind == "restart":
        return f"{base}（{params['delay_minutes']} 分钟后）"
    return base
