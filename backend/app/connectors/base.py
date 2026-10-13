from abc import ABC, abstractmethod
from app.schemas import ApiResult, BankRequest


class BankConnector(ABC):
    """One file per bank. Subclass, set `name` and `slug`, implement `fetch`, decorate with
    @register_bank. The bank then appears in the 1C list automatically (GET /api/1c/banks).

    `fetch` is called repeatedly for one logical operation while `result.retry` is True
    (ASAN / SMS approval pending). Keep step state in app.sessions keyed by `req.pw`.
    On success return rows in `data`; keys must match the 1C `bank_ops` columns (see README)."""

    name: str   # shown in the 1C list ("Kapital bank")
    slug: str   # URL part: POST /api/1c/bank/<slug>

    @abstractmethod
    async def fetch(self, req: BankRequest) -> ApiResult: ...
