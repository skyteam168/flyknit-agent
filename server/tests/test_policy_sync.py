"""客户端内置的默认策略（离线时使用）必须与服务端保持一致。

修改 app/default_policy.py 后运行：
    python -m scripts.export_policy
"""

import json
from pathlib import Path

from app.default_policy import DEFAULT_POLICY

CLIENT_COPY = Path(__file__).resolve().parents[2] / "client/src/Flyknit.Core/Resources/default-policy.json"


def test_client_default_policy_matches_server():
    assert json.loads(CLIENT_COPY.read_text(encoding="utf-8")) == DEFAULT_POLICY
