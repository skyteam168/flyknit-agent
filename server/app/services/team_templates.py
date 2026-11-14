"""IT 建团队用的模板（docs/team-mode.md 第 10 节第 2 条）：几个验证过的常见组队方式。

模板只给名字、职责说明、队长要求、每个位置要什么样的专家、示例任务；具体选哪位专家由 IT 在本厂的专家里挑
（模板不认识各厂的专家 ID）。建出来就是一个普通的 IT 团队，照常设使用范围。
"""

from __future__ import annotations

TEMPLATES: list[dict] = [
    {
        "key": "delivery_delay",
        "name": "交付延期分析组",
        "description": "查清订单为什么延期：生产进度、质量返工、排产插单一起看，给出主要原因和改进建议",
        "icon": "truck",
        "leader_note": "报告先写结论（主要原因排前 3，带占比），再写各成员要点和交叉验证；数字要能对上同一批工单，对不上的写明口径差异。",
        "roles": [
            {"role": "MES / 生产数据专家", "note": "查延期工单、各工序完工时间和延误小时"},
            {"role": "质检 / QMS 专家", "note": "查同期返工、不良，和延期工单对单号"},
            {"role": "排产 / 计划专家", "note": "查插单、急单、产能占用"},
            {"role": "报表专家（可选）", "note": "把结果整理成 Excel 表"},
        ],
        "examples": [
            "查一下上个月 A 车间订单为什么延期这么多，给出主要原因和改进建议",
            "本周交期风险最高的 10 张订单是哪些，卡在哪道工序",
        ],
        "disabled_tools": ["shell"],
        "read_only": False,
    },
    {
        "key": "monthly_report",
        "name": "月度经营报告组",
        "description": "每月汇总产量、质量、交付和成本数据，出一份月报",
        "icon": "chart",
        "leader_note": "月报固定四节：产量、质量、交付、成本；每节先给本月数字和环比，再给异常说明。数据取不到的写「未取到」，不要估算。",
        "roles": [
            {"role": "MES / 生产数据专家", "note": "产量、稼动率"},
            {"role": "质检 / QMS 专家", "note": "不良率、客诉数"},
            {"role": "ERP / 财务数据专家", "note": "成本、出货金额"},
            {"role": "报表专家", "note": "汇总成月报文件"},
        ],
        "examples": [
            "出一份上个月的经营月报",
            "对比今年和去年同期的月度产量和不良率",
        ],
        "disabled_tools": ["shell"],
        "read_only": False,
    },
    {
        "key": "complaint_trace",
        "name": "客诉追溯组",
        "description": "收到客诉后追溯批次、工序、物料和检验记录，找到原因并给出围堵措施",
        "icon": "search",
        "leader_note": "按 8D 的前 4 步写：问题描述、围堵措施、根本原因（列证据）、纠正措施建议；没有证据的推测要标出来。",
        "roles": [
            {"role": "质检 / QMS 专家", "note": "查检验记录、同批次不良"},
            {"role": "MES / 生产数据专家", "note": "按批次追溯工序、机台、人员"},
            {"role": "仓库 / 物料专家", "note": "查物料批次、供应商、库存"},
        ],
        "examples": [
            "客户反馈批次 B2410 鞋面开胶，追溯原因并给出围堵措施",
        ],
        "disabled_tools": ["shell", "write"],
        "read_only": True,
    },
]


def find(key: str) -> dict | None:
    return next((t for t in TEMPLATES if t["key"] == key), None)
