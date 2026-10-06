"""机器信息上报：台账要跟着 DHCP、换人、客户端升级一起动，而不是注册那一刻的快照。"""

import sqlalchemy as sa
import pytest

from app.migrate import ensure_schema
from conftest import ADMIN

pytestmark = pytest.mark.asyncio


def beat(client, headers, **fields):
    body = {
        "machine_name": "PC-QC-017",
        "user_name": "nguyen.van.a",
        "domain": "SHENZHOU",
        "os_version": "Microsoft Windows NT 10.0.26100.0",
        "client_version": "0.9.0",
        "ui_language": "vi-VN",
        "ip_addresses": ["10.10.131.188", "192.168.56.1"],
        "mac_address": "A4-BB-6D-11-22-33",
    }
    body.update(fields)
    return client.post("/api/v1/devices/heartbeat", headers=headers, json=body)


async def test_requires_device_token(client):
    r = await client.post("/api/v1/devices/heartbeat", json={})
    assert r.status_code == 401


async def test_records_what_the_client_reports(client, device_headers):
    assert (await beat(client, device_headers)).status_code == 204

    devices = (await client.get("/api/v1/admin/devices", headers=ADMIN)).json()
    d = devices[0]
    assert d["domain"] == "SHENZHOU"
    assert d["ip_addresses"] == "10.10.131.188,192.168.56.1"
    assert d["mac_address"] == "A4-BB-6D-11-22-33"
    assert d["ui_language"] == "vi-VN"
    assert d["client_version"] == "0.9.0"


async def test_observed_ip_comes_from_the_connection_not_the_client(client, device_headers):
    # 客户端报什么都不影响这一列，它是服务端自己看到的
    assert (await beat(client, device_headers, ip_addresses=["1.2.3.4"])).status_code == 204
    d = (await client.get("/api/v1/admin/devices", headers=ADMIN)).json()[0]
    assert d["ip_addresses"] == "1.2.3.4"
    assert d["observed_ip"] and d["observed_ip"] != "1.2.3.4"


async def test_trusts_the_proxy_header_when_behind_one(client, device_headers):
    headers = {**device_headers, "X-Forwarded-For": "10.10.9.9, 172.16.0.1"}
    assert (await beat(client, headers)).status_code == 204
    d = (await client.get("/api/v1/admin/devices", headers=ADMIN)).json()[0]
    assert d["observed_ip"] == "10.10.9.9"  # 取第一跳，也就是真正的客户端


async def test_later_reports_overwrite_earlier_ones(client, device_headers):
    await beat(client, device_headers, ip_addresses=["10.0.0.1"], user_name="nguyen")
    await beat(client, device_headers, ip_addresses=["10.0.0.99"], user_name="tran")

    d = (await client.get("/api/v1/admin/devices", headers=ADMIN)).json()[0]
    assert d["ip_addresses"] == "10.0.0.99"  # DHCP 换了地址要跟上
    assert d["user_name"] == "tran"          # 换人登录也要跟上


async def test_blank_fields_do_not_wipe_good_data(client, device_headers):
    """采不到某一项时客户端会报空串，那是「这次没采到」，不是「清空它」。"""
    await beat(client, device_headers)
    await beat(client, device_headers, domain="", mac_address="", os_version="")

    d = (await client.get("/api/v1/admin/devices", headers=ADMIN)).json()[0]
    assert d["domain"] == "SHENZHOU"
    assert d["mac_address"] == "A4-BB-6D-11-22-33"
    assert d["os_version"].startswith("Microsoft Windows")


async def test_disabled_device_cannot_report(client, device_headers):
    devices = (await client.get("/api/v1/admin/devices", headers=ADMIN)).json()
    await client.patch(f"/api/v1/admin/devices/{devices[0]['id']}", headers=ADMIN, json={"disabled": True})
    assert (await beat(client, device_headers)).status_code == 403


async def test_many_addresses_are_capped(client, device_headers):
    r = await beat(client, device_headers, ip_addresses=[f"10.0.0.{i}" for i in range(40)])
    assert r.status_code == 422  # 四十个网卡不是真实情况，拒掉比截断更诚实


@pytest.mark.filterwarnings("ignore")
def test_old_database_gains_the_new_columns(tmp_path):
    """已经在跑的服务端升级上来，devices 表要自动补列，设备不能掉。"""
    engine = sa.create_engine(f"sqlite:///{tmp_path / 'old.db'}")
    with engine.begin() as conn:
        conn.exec_driver_sql(
            """
            CREATE TABLE devices (
                id INTEGER NOT NULL PRIMARY KEY,
                token_hash VARCHAR(64) NOT NULL,
                machine_name VARCHAR(200) NOT NULL,
                user_name VARCHAR(200) NOT NULL,
                os_version VARCHAR(200) NOT NULL,
                client_version VARCHAR(50) NOT NULL,
                ui_language VARCHAR(10) NOT NULL,
                disabled BOOLEAN NOT NULL,
                created_at DATETIME NOT NULL,
                last_seen DATETIME
            )
            """
        )
        conn.exec_driver_sql(
            "INSERT INTO devices (token_hash, machine_name, user_name, os_version, client_version,"
            " ui_language, disabled, created_at) VALUES"
            " ('h1', 'PC-001', 'nguyen', 'Win10', '0.8.0', 'zh-CN', 0, '2026-09-01 10:00:00')"
        )

    with engine.begin() as conn:
        ensure_schema(conn)

    with engine.begin() as conn:
        cols = {r[1] for r in conn.exec_driver_sql("PRAGMA table_info(devices)")}
        assert {"domain", "ip_addresses", "observed_ip", "mac_address"} <= cols
        # 老设备还在，令牌没变，不用重新注册
        row = conn.exec_driver_sql("SELECT machine_name, token_hash FROM devices").fetchone()
        assert row == ("PC-001", "h1")
