"""敏感配置（模型 Key、SMB 密码）的加解密与令牌哈希。"""

import base64
import hashlib
import hmac
import secrets

from cryptography.fernet import Fernet, InvalidToken

from .config import get_settings


def _fernet() -> Fernet:
    digest = hashlib.sha256(get_settings().secret_key.encode("utf-8")).digest()
    return Fernet(base64.urlsafe_b64encode(digest))


def encrypt(plain: str) -> str:
    return _fernet().encrypt(plain.encode("utf-8")).decode("ascii")


def decrypt(token: str) -> str:
    if not token:
        return ""
    try:
        return _fernet().decrypt(token.encode("ascii")).decode("utf-8")
    except InvalidToken as exc:  # SECRET_KEY 被修改过
        raise ValueError("无法解密配置，请检查 FLYKNIT_SECRET_KEY 是否被修改") from exc


def mask(secret: str) -> str:
    if not secret:
        return ""
    if len(secret) <= 8:
        return "****"
    return f"{secret[:3]}****{secret[-4:]}"


def new_token() -> str:
    return "fk_" + secrets.token_urlsafe(32)


def hash_token(token: str) -> str:
    return hashlib.sha256(token.encode("utf-8")).hexdigest()


# ---------- 管理端账号密码 ----------
# 用标准库的 scrypt，不引 passlib / argon2-cffi：工厂环境离线，依赖越少越好。
# scrypt 是内存困难的，比 PBKDF2 更抗 GPU 暴力破解。

_SCRYPT_N = 2**14  # 约 16MB 内存、几十毫秒一次，登录够快，爆破够慢
_SCRYPT_R = 8
_SCRYPT_P = 1


def hash_password(password: str) -> str:
    """返回 scrypt$N$r$p$salt$hash，参数存在串里，以后调强度也能验老密码。"""
    salt = secrets.token_bytes(16)
    derived = hashlib.scrypt(password.encode("utf-8"), salt=salt, n=_SCRYPT_N, r=_SCRYPT_R, p=_SCRYPT_P, dklen=32)
    return f"scrypt${_SCRYPT_N}${_SCRYPT_R}${_SCRYPT_P}${salt.hex()}${derived.hex()}"


def verify_password(password: str, stored: str) -> bool:
    """比较用 compare_digest，避免按耗时逐字节试出密码。"""
    try:
        scheme, n, r, p, salt_hex, want = stored.split("$")
        if scheme != "scrypt":
            return False
        derived = hashlib.scrypt(
            password.encode("utf-8"), salt=bytes.fromhex(salt_hex),
            n=int(n), r=int(r), p=int(p), dklen=len(want) // 2,
        )
    except (ValueError, TypeError):
        return False
    return hmac.compare_digest(derived.hex(), want)
