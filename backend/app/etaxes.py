"""e-taxes.gov.az (e-qaimə) integration.

ETaxesClient is the only thing that talks to the tax portal. `DemoETaxes` lets the whole
chain (1C -> API -> rows) be tested without a phone. `LiveETaxes` must be filled in from a
real browser session (see README, "Canlı e-taxes")."""
from abc import ABC, abstractmethod
from datetime import datetime
from typing import Any

from app.schemas import Certificate


class ETaxesClient(ABC):
    @abstractmethod
    async def start_auth(self, asan_mob: str, asan_id: str) -> tuple[dict[str, Any], str]:
        """Start ASAN login. Returns (opaque auth state, verification code to show the user)."""

    @abstractmethod
    async def poll_auth(self, auth: dict[str, Any]) -> bool:
        """True once the user approved on the phone."""

    @abstractmethod
    async def certificates(self, auth: dict[str, Any]) -> list[Certificate]: ...

    @abstractmethod
    async def list_invoices(self, auth: dict[str, Any], tax_id: str, flt: dict[str, Any]) -> list[dict[str, Any]]:
        """Normalized invoices, one dict per invoice, with `lines` (list of dicts). See _DEMO."""


_DEMO = [{
    "id": "demo-0001", "kind": "1", "status": "Təsdiqlənmiş",
    "sender_tin": "1234567891", "sender_name": "Satıcı MMC",
    "receiver_tin": "9876543211", "receiver_name": "Alıcı MMC",
    "created": datetime(2026, 10, 12, 10, 30), "approved": datetime(2026, 10, 12, 11, 0),
    "sent": datetime(2026, 10, 12, 10, 45), "date": datetime(2026, 10, 12),
    "serial": "MQ0000000001",
    "lines": [
        {"code": "A-1", "name": "Kağız A4", "unit": "ədəd", "qty": 10, "amount": 100.0,
         "vat": 18.0, "extra": 0.0, "barcode": "4760001", "type": "mal"},
        {"code": "S-1", "name": "Çatdırılma", "unit": "xidmət", "qty": 1, "amount": 20.0,
         "vat": 3.6, "extra": 0.0, "barcode": "", "type": "xidmət"},
    ],
}]


class DemoETaxes(ETaxesClient):
    async def start_auth(self, asan_mob, asan_id):
        return {"polls": 0}, "4821"

    async def poll_auth(self, auth):
        auth["polls"] += 1
        return auth["polls"] >= 2

    async def certificates(self, auth):
        return [Certificate(tax_id="1234567891", tax_name="Demo MMC")]

    async def list_invoices(self, auth, tax_id, flt):
        return _DEMO


class LiveETaxes(ETaxesClient):
    """TODO: real portal calls. Needs the request/response sequence of the browser
    (ASAN login -> certificate choice -> invoice search); see README."""

    async def start_auth(self, asan_mob, asan_id):
        raise NotImplementedError("LiveETaxes is not implemented yet")

    async def poll_auth(self, auth):
        raise NotImplementedError

    async def certificates(self, auth):
        raise NotImplementedError

    async def list_invoices(self, auth, tax_id, flt):
        raise NotImplementedError


def to_rows(invoices: list[dict[str, Any]]) -> list[dict[str, Any]]:
    """Flatten to one row per invoice line using the column names the 1C code reads:
    c1 id, c3 kind, c4 status, c5/c6 sender TIN/name, c7/c8 receiver TIN/name,
    c9..c12 dates (c12 = invoice date), c13 series+number,
    c27 item code, c28 name, c29 unit, c30 qty, c35 amount, c40 VAT, c41 other tax,
    c43 barcode, c44 type (mal|xidmət)."""
    iso = lambda d: d.strftime("%Y-%m-%dT%H:%M:%S.000")
    rows = []
    for inv in invoices:
        for ln in inv["lines"]:
            rows.append({
                "c1": inv["id"], "c3": inv["kind"], "c4": inv["status"],
                "c5": inv["sender_tin"], "c6": inv["sender_name"],
                "c7": inv["receiver_tin"], "c8": inv["receiver_name"],
                "c9": iso(inv["created"]), "c10": iso(inv["approved"]),
                "c11": iso(inv["sent"]), "c12": iso(inv["date"]), "c13": inv["serial"],
                "c27": ln["code"], "c28": ln["name"], "c29": ln["unit"], "c30": ln["qty"],
                "c35": ln["amount"], "c40": ln["vat"], "c41": ln["extra"],
                "c43": ln["barcode"], "c44": ln["type"],
            })
    return rows
