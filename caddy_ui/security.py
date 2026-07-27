from __future__ import annotations

import base64
import hashlib
import hmac
import os
import secrets
import struct
import threading
import time
from dataclasses import dataclass


SCRYPT_N = 2**14
SCRYPT_R = 8
SCRYPT_P = 1
MAX_PASSWORD_LENGTH = 1024
DUMMY_PASSWORD_HASH = "scrypt$16384$8$1$Y2FkZHktdWktZHVtbXkhIQ$iNzIqemGPOs382q_wtdscmpsP6jmvPDAPj2FMiWE1Yc"
_HASH_WORKERS = max(1, min(8, int(os.getenv("CADDY_UI_PASSWORD_HASH_WORKERS", "2"))))
_HASH_SEMAPHORE = threading.BoundedSemaphore(_HASH_WORKERS)


def hash_password(password: str) -> str:
    if len(password) < 10:
        raise ValueError("Password must contain at least 10 characters.")
    if len(password) > MAX_PASSWORD_LENGTH:
        raise ValueError(f"Password must contain at most {MAX_PASSWORD_LENGTH} characters.")
    with _HASH_SEMAPHORE:
        salt = secrets.token_bytes(16)
        digest = hashlib.scrypt(
            password.encode("utf-8"),
            salt=salt,
            n=SCRYPT_N,
            r=SCRYPT_R,
            p=SCRYPT_P,
            dklen=32,
        )
    return "scrypt${}${}${}${}${}".format(
        SCRYPT_N,
        SCRYPT_R,
        SCRYPT_P,
        base64.urlsafe_b64encode(salt).decode("ascii").rstrip("="),
        base64.urlsafe_b64encode(digest).decode("ascii").rstrip("="),
    )


def _decode(value: str) -> bytes:
    return base64.urlsafe_b64decode(value + "=" * (-len(value) % 4))


def verify_password(password: str, encoded: str) -> bool:
    if len(password) > MAX_PASSWORD_LENGTH:
        password = ""
    try:
        algorithm, n_value, r_value, p_value, salt_value, expected_value = encoded.split("$", 5)
        n, r, p = int(n_value), int(r_value), int(p_value)
        salt, expected = _decode(salt_value), _decode(expected_value)
        if (
            algorithm != "scrypt"
            or n < 2**12
            or n > 2**18
            or n & (n - 1)
            or not 1 <= r <= 16
            or not 1 <= p <= 4
            or not 16 <= len(salt) <= 64
            or not 16 <= len(expected) <= 64
        ):
            return False
        if not _HASH_SEMAPHORE.acquire(timeout=2):
            return False
        try:
            digest = hashlib.scrypt(
                password.encode("utf-8"),
                salt=salt,
                n=n,
                r=r,
                p=p,
                dklen=len(expected),
            )
        finally:
            _HASH_SEMAPHORE.release()
        return hmac.compare_digest(digest, expected)
    except (ValueError, TypeError, MemoryError):
        return False


def token_hash(token: str) -> str:
    return hashlib.sha256(token.encode("utf-8")).hexdigest()


def new_session_tokens() -> tuple[str, str, str]:
    token = secrets.token_urlsafe(32)
    csrf = secrets.token_urlsafe(32)
    return token, token_hash(token), csrf


def new_totp_secret() -> str:
    return base64.b32encode(secrets.token_bytes(20)).decode("ascii").rstrip("=")


def totp_code(secret: str, timestamp: int | None = None, period: int = 30) -> str:
    timestamp = int(time.time()) if timestamp is None else timestamp
    padded = secret.upper() + "=" * (-len(secret) % 8)
    key = base64.b32decode(padded)
    counter = struct.pack(">Q", timestamp // period)
    digest = hmac.new(key, counter, hashlib.sha1).digest()
    offset = digest[-1] & 0x0F
    number = (struct.unpack(">I", digest[offset : offset + 4])[0] & 0x7FFFFFFF) % 1_000_000
    return f"{number:06d}"


def verify_totp(secret: str, code: str, timestamp: int | None = None) -> bool:
    if not secret or not code.isdigit() or len(code) != 6:
        return False
    timestamp = int(time.time()) if timestamp is None else timestamp
    return any(hmac.compare_digest(totp_code(secret, timestamp + offset), code) for offset in (-30, 0, 30))


@dataclass(slots=True)
class LoginThrottle:
    failures: dict[str, list[float]]
    window_seconds: int = 300
    maximum_attempts: int = 8

    def __init__(self, window_seconds: int = 300, maximum_attempts: int = 8):
        self.failures = {}
        self.window_seconds = window_seconds
        self.maximum_attempts = maximum_attempts

    def allowed(self, key: str) -> bool:
        now = time.time()
        attempts = [item for item in self.failures.get(key, []) if item > now - self.window_seconds]
        self.failures[key] = attempts
        return len(attempts) < self.maximum_attempts

    def record_failure(self, key: str) -> None:
        self.failures.setdefault(key, []).append(time.time())

    def clear(self, key: str) -> None:
        self.failures.pop(key, None)
