"""Request/response shapes. They mirror exactly what the 1C processing (Az-api v8.2.epf)
sends and reads, so the existing client keeps working with only the server address changed."""
from typing import Any, Optional
from pydantic import BaseModel, Field


class Certificate(BaseModel):
    tax_id: str
    tax_name: str


class ApiResult(BaseModel):
    """Response of every data endpoint. 1C reads all of these keys unconditionally,
    so none of them may be null/missing."""
    retry: bool = False          # True -> client waits 5s and repeats the same request
    code: str = ""               # ASAN verification code shown to the user (compare on the phone)
    pw: str = ""                 # opaque session token / password, echoed back by the client
    certificates: list[Certificate] = Field(default_factory=list)
    success: bool = False
    data: list[dict[str, Any]] = Field(default_factory=list)
    message: str = ""


class InvoiceFilter(BaseModel):
    creationDateFrom: str = ""   # dd-MM-yyyy HH:mm
    creationDateTo: str = ""
    senderTin: Optional[str] = ""
    receivererTin: Optional[str] = ""  # (sic) spelling used by the 1C client
    maxCount: int = 10000
    box: str = "inbox"           # inbox | outbox | draft


class TaxRequest(BaseModel):
    asan_mob: str = ""
    asan_id: str = ""
    tax_id: Optional[str] = None
    code: str = ""
    pw: str = ""
    filter: InvoiceFilter = InvoiceFilter()


class BankFilter(BaseModel):
    date_from: str = ""          # yyyy-MM-dd
    date_to: str = ""


class BankRequest(BaseModel):
    cust_id: Optional[str] = ""
    asan_mob: str = ""
    asan_id: str = ""
    tax_id: Optional[str] = None
    code: str = ""
    pw: str = ""
    filter: BankFilter = BankFilter()


class UpdateRequest(BaseModel):
    data: str = ""               # base64 of the current .epf
