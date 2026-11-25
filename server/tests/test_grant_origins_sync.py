"""员工端申请授权用的来源清单（TaskGrant.Origins）必须和服务端收的一致：少一个，那个来源的任务就会被服务端拒收。"""

import re
from pathlib import Path

from app.routers.grants import ORIGINS

CLIENT = Path(__file__).resolve().parents[2] / "client/src/Flyknit.Core/Security/TaskGrant.cs"


def test_client_and_server_grant_origins_match():
    source = CLIENT.read_text(encoding="utf-8")
    m = re.search(r"Origins\s*=\s*\{([^}]*)\}", source)
    assert m, "TaskGrant.cs 里找不到 Origins 清单"
    client = re.findall(r'"([a-z_]+)"', m.group(1))
    assert sorted(client) == sorted(ORIGINS)
