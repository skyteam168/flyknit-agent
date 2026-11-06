"""快捷指令：后台发布、员工端拉取；名字校验、不能重复、不能占用客户端自带的。"""

from conftest import ADMIN


async def test_admin_publishes_and_devices_fetch(client, device_headers):
    assert (await client.get("/api/v1/client/commands", headers=device_headers)).json() == {"items": []}

    items = [
        {"name": "周报", "title": "写本周工作周报", "description": "按日期汇总", "template": "帮我写 {日期} 这一周的工作周报"},
        {"name": "/qc-sum", "template": "汇总 {车间} 的质检数据"},
    ]
    r = await client.put("/api/v1/admin/commands", headers=ADMIN, json={"items": items})
    assert r.status_code == 200, r.text
    assert [c["name"] for c in r.json()["items"]] == ["周报", "qc-sum"]  # 开头的 / 去掉

    got = (await client.get("/api/v1/client/commands", headers=device_headers)).json()["items"]
    # 员工端多一个 source（来自哪个插件；后台直接发布的为空）
    assert got[0] == {"name": "周报", "title": "写本周工作周报", "description": "按日期汇总", "template": "帮我写 {日期} 这一周的工作周报",
                      "source": ""}
    assert (await client.get("/api/v1/admin/commands", headers=ADMIN)).json()["items"] == [
        {k: v for k, v in c.items() if k != "source"} for c in got]


async def test_bad_names_duplicates_and_reserved_are_refused(client, device_headers):
    async def put(*items):
        return await client.put("/api/v1/admin/commands", headers=ADMIN, json={"items": list(items)})

    assert (await put({"name": "有 空格", "template": "x"})).status_code == 422
    assert (await put({"name": "a/b", "template": "x"})).status_code == 422
    assert (await put({"name": "btw", "template": "x"})).status_code == 422
    assert (await put({"name": "x", "template": ""})).status_code == 422
    dup = await put({"name": "Report", "template": "a"}, {"name": "report", "template": "b"})
    assert dup.status_code == 400 and "重复" in dup.json()["detail"]


async def test_only_admins_publish_and_only_devices_fetch(client, device_headers):
    assert (await client.put("/api/v1/admin/commands", headers=device_headers, json={"items": []})).status_code in (401, 403)
    assert (await client.get("/api/v1/client/commands")).status_code in (401, 403)
