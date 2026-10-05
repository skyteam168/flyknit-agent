from functools import lru_cache
from pathlib import Path

from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    # .env 固定从 server 目录读取（不受启动时所在目录影响）；utf-8-sig 兼容记事本保存的 BOM
    model_config = SettingsConfigDict(
        env_prefix="FLYKNIT_",
        env_file=Path(__file__).resolve().parents[1] / ".env",
        env_file_encoding="utf-8-sig",
        extra="ignore",
    )

    admin_token: str = "change-me-admin-token"
    secret_key: str = "change-me-to-a-long-random-string"
    enrollment_key: str = "flyknit-enroll"
    database_url: str = "sqlite+aiosqlite:///./flyknit.db"
    upstream_timeout: float = 300.0
    # 技能包等文件的存放目录（相对路径相对于 server 目录）
    data_dir: str = str(Path(__file__).resolve().parents[1] / "data")
    cors_origins: list[str] = ["*"]


@lru_cache
def get_settings() -> Settings:
    return Settings()
