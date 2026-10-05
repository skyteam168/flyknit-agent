from functools import lru_cache

from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    model_config = SettingsConfigDict(env_prefix="FLYKNIT_", env_file=".env", extra="ignore")

    admin_token: str = "change-me-admin-token"
    secret_key: str = "change-me-to-a-long-random-string"
    enrollment_key: str = "flyknit-enroll"
    database_url: str = "sqlite+aiosqlite:///./flyknit.db"
    upstream_timeout: float = 300.0
    cors_origins: list[str] = ["*"]


@lru_cache
def get_settings() -> Settings:
    return Settings()
