from functools import lru_cache
from pathlib import Path
from urllib.parse import quote

from pydantic import AliasChoices, Field, model_validator
from pydantic_settings import BaseSettings, SettingsConfigDict


def _pg(name: str, default: str = "") -> str:
    """POSTGRES_HOST 这类写法（和 postgres 镜像、DBA 给的配置一致），也认 FLYKNIT_POSTGRES_HOST。"""
    return Field(default=default, validation_alias=AliasChoices(f"FLYKNIT_{name}", name))


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
    # PostgreSQL：填了 POSTGRES_HOST 就用它（下面几项拼成连接串），优先于 SQLite 的 FLYKNIT_DATABASE_URL。
    # FLYKNIT_DATABASE_URL 直接写成 postgresql+asyncpg://... 也行，那样以它为准
    postgres_host: str = _pg("POSTGRES_HOST")
    postgres_port: str = _pg("POSTGRES_PORT", "5432")
    postgres_db: str = _pg("POSTGRES_DB", "postgres")
    postgres_user: str = _pg("POSTGRES_USER", "postgres")
    postgres_password: str = _pg("POSTGRES_PASSWORD")
    #: 和别的系统共用一个库时，本系统的表放进这个 schema（自动创建），不和 public 里别人的表混在一起
    postgres_schema: str = _pg("POSTGRES_SCHEMA")
    #: 换成 PostgreSQL 之前用的 SQLite 库。迁移脚本从这里读旧数据
    sqlite_url: str = ""
    upstream_timeout: float = 300.0
    # 技能包等文件的存放目录（相对路径相对于 server 目录）
    data_dir: str = str(Path(__file__).resolve().parents[1] / "data")
    cors_origins: list[str] = ["*"]
    #: 后台的定期清理与备份。测试里关掉，免得每个用例都起一个任务
    housekeeping: bool = True


    @model_validator(mode="after")
    def _use_postgres(self) -> "Settings":
        if self.database_url.startswith("sqlite"):
            self.sqlite_url = self.database_url
            if self.postgres_host.strip():
                self.database_url = postgres_url(
                    self.postgres_host, self.postgres_port, self.postgres_db, self.postgres_user, self.postgres_password
                )
        return self


def postgres_url(host: str, port: str, db: str, user: str, password: str) -> str:
    """密码里的 @ : / 之类要转义，不然连接串会被拆错。"""
    auth = quote(user.strip(), safe="") + (f":{quote(password, safe='')}" if password else "")
    return f"postgresql+asyncpg://{auth}@{host.strip()}:{(port or '5432').strip()}/{quote(db.strip(), safe='')}"


@lru_cache
def get_settings() -> Settings:
    return Settings()
