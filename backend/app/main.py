import os
from fastapi import FastAPI, HTTPException

from app import sessions
from app.connectors import registry
from app.etaxes import DemoETaxes, ETaxesClient, LiveETaxes, to_rows
from app.schemas import ApiResult, BankRequest, TaxRequest, UpdateRequest

app = FastAPI(title="AZ-API")
registry.load_all()


def etaxes() -> ETaxesClient:
    return DemoETaxes() if os.getenv("AZAPI_ETAXES", "demo") == "demo" else LiveETaxes()


_client_singleton: ETaxesClient | None = None


def client() -> ETaxesClient:
    global _client_singleton
    if _client_singleton is None:
        _client_singleton = etaxes()
    return _client_singleton


@app.get("/api/1c/banks")
async def banks():
    return {"banks": [{"name": b.name, "url": b.slug} for b in registry.all_banks()]}


@app.post("/api/1c/bank/{slug}", response_model=ApiResult)
async def bank(slug: str, req: BankRequest):
    b = registry.get_bank(slug)
    if b is None:
        raise HTTPException(404, "Bank tapılmadı")
    try:
        return await b.fetch(req)
    except Exception as e:  # surface to the 1C user as a message instead of an HTTP 500
        return ApiResult(message=f"{b.name}: {e}")


@app.post("/api/1c/module/8.2/update")
async def update(_: UpdateRequest):
    return {"updatable": False, "question": "", "header": "", "warning": "", "data": ""}


@app.post("/api/tax/list", response_model=ApiResult)
async def tax_list(req: TaxRequest):
    c = client()
    try:
        s = sessions.get(req.pw) if req.pw else None
        if s is None:                                   # 1) start ASAN approval
            s = sessions.create()
            s.state["auth"], code = await c.start_auth(req.asan_mob, req.asan_id)
            s.state["code"], s.state["ok"] = code, False
            return ApiResult(retry=True, code=code, pw=s.token)
        if not s.state["ok"]:                           # 2) waiting for the phone
            if not await c.poll_auth(s.state["auth"]):
                return ApiResult(retry=True, code=s.state["code"], pw=s.token)
            s.state["ok"] = True
        certs = await c.certificates(s.state["auth"])   # 3) authenticated
        res = ApiResult(pw=s.token, code=s.state["code"], certificates=certs)
        if not req.tax_id:
            res.message = "Təşkilatı seçib yenidən yükləyin."
            return res
        inv = await c.list_invoices(s.state["auth"], req.tax_id, req.filter.model_dump())
        res.data, res.success = to_rows(inv), True
        return res
    except Exception as e:
        return ApiResult(message=f"e-taxes: {e}")
