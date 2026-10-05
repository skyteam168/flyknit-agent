"""把服务端默认策略导出到客户端内置资源。用法：python -m scripts.export_policy"""

import json
from pathlib import Path

from app.default_policy import DEFAULT_POLICY

TARGET = Path(__file__).resolve().parents[2] / "client/src/Flyknit.Core/Resources/default-policy.json"

if __name__ == "__main__":
    TARGET.write_text(json.dumps(DEFAULT_POLICY, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"已写入 {TARGET}")
