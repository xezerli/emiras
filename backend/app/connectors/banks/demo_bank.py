"""Reference implementation for new banks. Enabled only with AZAPI_DEMO=1.
Copy this file, rename, set name/slug and replace the body of `fetch`."""
import os

from app import sessions
from app.connectors.base import BankConnector
from app.connectors.registry import register_bank
from app.schemas import ApiResult, BankRequest

if os.getenv("AZAPI_DEMO") == "1":

    @register_bank
    class DemoBank(BankConnector):
        name = "Demo bank"
        slug = "demo"

        async def fetch(self, req: BankRequest) -> ApiResult:
            s = sessions.get(req.pw) if req.pw else None
            if s is None:                       # step 1: start approval
                s = sessions.create()
                s.state["polls"] = 0
                return ApiResult(retry=True, code="7305", pw=s.token)
            s.state["polls"] += 1
            if s.state["polls"] < 2:            # step 2: still waiting for the phone
                return ApiResult(retry=True, code="7305", pw=s.token)
            sessions.drop(s.token)              # step 3: done
            return ApiResult(success=True, pw="", data=[{
                "c1": "Demo bank", "c2": "AZ00DEMO00000000000000000000",
                "c3": "AZN", "c4": f"{req.filter.date_from}T09:00:00.000",
                "c5": "REF-1", "c6": "1234567891", "c7": "Demo MMC",
                "c8": "AZ00XXXX00000000000000000001",
                "c9": 150.0, "c10": 0.0, "c11": "Ödəniş", "c12": 1000.0, "c13": 1150.0,
            }])
