"""安全中心每一项的默认值、锁状态与合并规则。

最要紧的一条：员工不能自己关掉降低安全线的开关。
"""

from app.services import security_settings as ss


def test_only_harmless_items_are_unlocked_by_default():
    """默认放开的必须只有「改了也不降低安全线」的那几项。"""
    unlocked = {s.key for s in ss.SETTINGS if not s.locked}

    assert unlocked == {"auto_backup", "backup_quota_mb", "notifications", "notification_sound"}
    # 这几项要是默认放开，员工就能自己把管控关掉
    for key in ("sandbox", "delete_protection", "network_allowlist", "system_tools"):
        assert ss.BY_KEY[key].locked, key


def test_every_risky_item_explains_what_turning_it_off_costs():
    for s in ss.SETTINGS:
        if s.key == "notification_sound":
            continue  # 提示音开不开没有安全后果
        assert s.risk, f"{s.key} 没有写关掉的后果"


def test_unknown_keys_are_dropped():
    assert ss.sanitize({"sandbox": False, "made_up": 1}) == {"sandbox": False}
    assert ss.sanitize_locks({"sandbox": True, "made_up": True}) == {"sandbox": True}
    assert ss.sanitize(None) == {}


def test_numbers_are_clamped_to_their_range():
    assert ss.sanitize({"backup_quota_mb": 10})["backup_quota_mb"] == 64        # 下限
    assert ss.sanitize({"backup_quota_mb": 999999})["backup_quota_mb"] == 20480  # 上限
    assert ss.sanitize({"backup_quota_mb": "abc"})["backup_quota_mb"] == 512     # 垃圾值退回默认


def test_booleans_accept_the_shapes_json_arrives_in():
    assert ss.sanitize({"sandbox": "false"})["sandbox"] is False
    assert ss.sanitize({"sandbox": "on"})["sandbox"] is True
    assert ss.sanitize({"sandbox": 0})["sandbox"] is False


def test_a_machine_can_be_unlocked_without_unlocking_everyone():
    """这是「按机器单独授权」的核心用法：全厂锁住，单台放开。"""
    everyone = ss.effective(global_locks={"system_tools": True})
    assert everyone["system_tools"]["locked"] is True

    just_this_one = ss.effective(
        global_locks={"system_tools": True},
        device_locks={"system_tools": False},
    )
    assert just_this_one["system_tools"]["locked"] is False


def test_device_values_win_over_global_which_win_over_defaults():
    assert ss.effective()["backup_quota_mb"]["value"] == 512
    assert ss.effective(global_values={"backup_quota_mb": 1024})["backup_quota_mb"]["value"] == 1024
    assert ss.effective(
        global_values={"backup_quota_mb": 1024},
        device_values={"backup_quota_mb": 2048},
    )["backup_quota_mb"]["value"] == 2048


def test_effective_carries_what_the_ui_needs():
    item = ss.effective()["backup_quota_mb"]

    assert item["kind"] == "int"
    assert item["min"] == 64 and item["max"] == 20480
    assert item["title"] and item["risk"]
    assert ss.effective()["sandbox"]["kind"] == "bool"
    assert "min" not in ss.effective()["sandbox"]


def test_a_setting_only_exists_if_something_enforces_it():
    """
    每一项都要有客户端代码真的去读它。

    曾经有三项（system_tools / sandbox / command_policy）下发了、画出来了、
    常量也定义了，但没有任何代码读过——后台显示「系统级工具：已禁用」，而
    wmic 和 schtasks /create 照跑。显示为关、实际是开的开关比没有这个开关更糟。

    客户端那边 SecuritySwitchTests 有对应的一条；这里守住服务端这一侧的清单，
    加项时两边都会提醒你。
    """
    assert {s.key for s in ss.SETTINGS} == {
        "sandbox",
        "delete_protection",
        "network_allowlist",
        "system_tools",
        "auto_backup",
        "backup_quota_mb",
        "batch_delete_threshold",
        "notifications",
        "notification_sound",
        # 客户端 LearningPolicy 读这几项：复盘和 memory_write 按类别跳过
        "learn_preferences",
        "learn_facts",
        "learn_experience",
        "learn_episodes",
        "learn_skills",
    }
