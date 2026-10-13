import io
import re
from dataclasses import dataclass, field
from datetime import datetime
from typing import Any, Callable

import openpyxl

IBAN_RE = re.compile(r"AZ\d{2}[A-Z]{4}[A-Z0-9]{20}")
TIN_RE = re.compile(r"(?<!\d)\d{10}(?!\d)")


class StatementError(Exception):
    pass


@dataclass
class Tx:
    date: datetime
    ref: str = ""
    cp_tin: str = ""
    cp_name: str = ""
    cp_account: str = ""
    credit: float = 0.0     # Mədaxil (money in)
    debit: float = 0.0      # Məxaric (money out)
    purpose: str = ""
    balance: float | None = None   # balance after this operation, if the bank gives it


@dataclass
class Statement:
    bank: str
    iban: str = ""
    currency: str = "AZN"
    owner_tin: str = ""
    opening: float | None = None
    closing: float | None = None
    txs: list[Tx] = field(default_factory=list)

    def check(self) -> str:
        """Compare bank totals with our sums; non-empty string = something was parsed wrongly."""
        if self.opening is None or self.closing is None:
            return ""
        calc = self.opening + sum(t.credit for t in self.txs) - sum(t.debit for t in self.txs)
        if abs(calc - self.closing) > 0.01:
            return f"Yoxlama: ilkin qalıq + mədaxil - məxaric = {calc:.2f}, çıxarışın son qalığı = {self.closing:.2f}."
        return ""

    def to_rows(self) -> list[dict[str, Any]]:
        """Keys = 1C `bank_ops` columns: c1 bank, c2 IBAN, c3 currency, c4 date, c5 reference,
        c6 counterparty VÖEN, c7 counterparty name, c8 counterparty account, c9 in, c10 out,
        c11 purpose, c12 balance before, c13 balance after."""
        rows, running = [], self.opening
        for t in self.txs:
            after = t.balance if t.balance is not None else (
                None if running is None else round(running + t.credit - t.debit, 2))
            before = None if after is None else round(after - t.credit + t.debit, 2)
            running = after
            rows.append({
                "c1": self.bank, "c2": self.iban, "c3": self.currency,
                "c4": t.date.strftime("%Y-%m-%dT%H:%M:%S.000"),
                "c5": t.ref, "c6": t.cp_tin, "c7": t.cp_name, "c8": t.cp_account,
                "c9": t.credit, "c10": t.debit, "c11": t.purpose,
                "c12": before if before is not None else 0.0,
                "c13": after if after is not None else 0.0,
            })
        return rows


# ---------- helpers shared by parsers ----------
def num(v) -> float:
    if v is None or v == "":
        return 0.0
    if isinstance(v, (int, float)):
        return float(v)
    s = re.sub(r"[^\d,.\-]", "", str(v)).replace(",", ".")
    return float(s) if s not in ("", "-", ".") else 0.0


def date(v) -> datetime | None:
    if isinstance(v, datetime):
        return v
    s = str(v or "").strip()[:10]
    for f in ("%d.%m.%Y", "%d/%m/%Y", "%Y-%m-%d"):
        try:
            return datetime.strptime(s, f)
        except ValueError:
            pass
    return None


def text(v) -> str:
    return "" if v is None else re.sub(r"[ \t]+", " ", str(v)).strip()


def cells(ws) -> list[list[Any]]:
    return [list(r) for r in ws.iter_rows(values_only=True)]


def find_header(grid: list[list[Any]], *needles: str) -> int | None:
    """Index of the first row that contains all `needles` (case-insensitive substrings)."""
    for i, row in enumerate(grid):
        joined = " | ".join(text(c).lower() for c in row)
        if all(n.lower() in joined for n in needles):
            return i
    return None


def columns(header: list[Any], **want: str) -> dict[str, int]:
    """Map logical name -> column index by header substring. Fails loudly on a missing column."""
    out = {}
    for key, needle in want.items():
        for i, h in enumerate(header):
            if needle.lower() in text(h).lower():
                out[key] = i
                break
        else:
            raise StatementError(f"Sütun tapılmadı: {needle}")
    return out


def label_value(grid: list[list[Any]], label: str, upto: int | None = None) -> Any:
    """Value in the first non-empty cell to the right of the cell containing `label`."""
    for row in grid[:upto]:
        for i, c in enumerate(row):
            if label.lower() in text(c).lower():
                for v in row[i + 1:]:
                    if v not in (None, ""):
                        return v
    return None


def split_counterparty(raw: str) -> tuple[str, str, str]:
    """Free-text 'account name tin' blob -> (account, name, tin)."""
    raw = raw.replace("\n", " ")
    m = IBAN_RE.search(raw)
    acc = m.group(0) if m else ""
    t = TIN_RE.search(raw[m.end():] if m else raw)
    tin = t.group(0) if t else ""
    if m:
        name = raw[m.end():]
        name = name[:t.start()] if t else name.split("/")[0]
    else:
        name = "" if re.fullmatch(r"[\d\s/]*", raw) else raw
        acc = raw.strip() if not name else ""
    return acc, text(name.strip(" /")), tin


# ---------- registry ----------
Parser = Callable[[openpyxl.Workbook], Statement]
_parsers: list[tuple[Callable[[openpyxl.Workbook], bool], Parser]] = []


def register_parser(detect: Callable[[openpyxl.Workbook], bool]):
    def deco(fn: Parser) -> Parser:
        _parsers.append((detect, fn))
        return fn
    return deco


def detect_and_parse(content: bytes) -> Statement:
    try:
        wb = openpyxl.load_workbook(io.BytesIO(content), data_only=True)
    except Exception as e:
        raise StatementError(f"Excel faylı oxunmadı: {e}")
    for detect, parse in _parsers:
        if detect(wb):
            st = parse(wb)
            if not st.txs:
                raise StatementError("Çıxarışda əməliyyat tapılmadı.")
            return st
    raise StatementError("Bank formatı tanınmadı (dəstəklənənlər: ABB, Accessbank, AIIB, Bank Respublika).")
