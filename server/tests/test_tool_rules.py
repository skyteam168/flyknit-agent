"""工具规则：后台保存、校验正则，员工端配置里只下发启用的。"""

from conftest import ADMIN


async def test_rules_reach_devices_through_config(client, device_headers):
    rules = [
        {"name": "ERP 只读", "tools": "mcp__erp__*", "match": "delete|update", "action": "block", "message": "ERP 只能查，不能改"},
        {"name": "打码身份证", "tools": "*", "action": "none", "redact": r"\d{17}[\dXx]"},
        {"name": "停用的", "enabled": False, "tools": "run_shell", "action": "confirm"},
    ]
    r = await client.put("/api/v1/admin/tool-rules", headers=ADMIN, json={"items": rules})
    assert r.status_code == 200, r.text
    assert len((await client.get("/api/v1/admin/tool-rules", headers=ADMIN)).json()["items"]) == 3

    cfg = (await client.get("/api/v1/client/config", headers=device_headers)).json()
    assert [x["name"] for x in cfg["tool_rules"]] == ["ERP 只读", "打码身份证"]
    assert cfg["tool_rules"][0]["action"] == "block"


async def test_bad_regex_and_action_are_refused(client):
    bad = await client.put("/api/v1/admin/tool-rules", headers=ADMIN, json={"items": [{"name": "x", "match": "(unclosed"}]})
    assert bad.status_code == 422
    assert "正则" in bad.text
    act = await client.put("/api/v1/admin/tool-rules", headers=ADMIN, json={"items": [{"name": "x", "action": "allow"}]})
    assert act.status_code == 422  # 没有「放行」：规则只能收紧


async def test_only_admins_edit_rules(client, device_headers):
    assert (await client.put("/api/v1/admin/tool-rules", headers=device_headers, json={"items": []})).status_code in (401, 403)
