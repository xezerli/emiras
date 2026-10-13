"""In-memory store for multi-step (ASAN approval) sessions. Replace with Redis for >1 worker."""
import secrets
import time
from dataclasses import dataclass, field
from typing import Any

TTL = 600


@dataclass
class Session:
    token: str
    created: float = field(default_factory=time.time)
    state: dict[str, Any] = field(default_factory=dict)


_store: dict[str, Session] = {}


def _gc() -> None:
    now = time.time()
    for k in [k for k, s in _store.items() if now - s.created > TTL]:
        del _store[k]


def create() -> Session:
    _gc()
    s = Session(secrets.token_urlsafe(16))
    _store[s.token] = s
    return s


def get(token: str) -> Session | None:
    _gc()
    return _store.get(token)


def drop(token: str) -> None:
    _store.pop(token, None)
