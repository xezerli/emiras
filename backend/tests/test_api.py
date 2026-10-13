import os
os.environ["AZAPI_DEMO"] = "1"
os.environ["AZAPI_ETAXES"] = "demo"
from fastapi.testclient import TestClient
from app.main import app

c = TestClient(app)


def test_banks_listed():
    assert {"name": "Demo bank", "url": "demo"} in c.get("/api/1c/banks").json()["banks"]


def test_unknown_bank():
    assert c.post("/api/1c/bank/nope", json={}).status_code == 404


def test_bank_flow_retries_then_data():
    r = c.post("/api/1c/bank/demo", json={"filter": {"date_from": "2026-10-13"}}).json()
    assert r["retry"] and r["code"] and r["pw"]
    r = c.post("/api/1c/bank/demo", json={"pw": r["pw"]}).json()
    assert r["retry"]
    r = c.post("/api/1c/bank/demo", json={"pw": r["pw"], "filter": {"date_from": "2026-10-13"}}).json()
    assert r["success"] and r["data"][0]["c4"].startswith("2026-10-13")


def test_tax_flow():
    r = c.post("/api/tax/list", json={"asan_mob": "0501234567", "asan_id": "123456"}).json()
    assert r["retry"] and r["code"] == "4821"
    pw = r["pw"]
    r = c.post("/api/tax/list", json={"pw": pw}).json()            # phone not approved yet
    assert r["retry"]
    r = c.post("/api/tax/list", json={"pw": pw}).json()            # approved, no cert chosen yet
    assert not r["retry"] and not r["success"] and r["certificates"][0]["tax_id"] == "1234567891"
    r = c.post("/api/tax/list", json={"pw": pw, "tax_id": "1234567891"}).json()
    assert r["success"] and len(r["data"]) == 2
    assert r["data"][0]["c13"] == "MQ0000000001" and r["data"][1]["c44"] == "xidmət"
    assert r["message"] == ""


def test_update_endpoint():
    assert c.post("/api/1c/module/8.2/update", json={"data": ""}).json()["updatable"] is False
