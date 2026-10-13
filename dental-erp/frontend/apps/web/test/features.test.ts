import { createApiClient } from "@dentacore/api-client";
import { describe, expect, it, vi } from "vitest";
import { PaymentAttempt, parseAmount, recordPayment } from "@/features/billing/pay";
import { actionsFor, dayRange, todayLocal } from "@/features/calendar/day";
import { registerPatient } from "@/features/patients/register";
import { findPatients } from "@/features/patients/search";

const json = (status: number, body: unknown) => new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });

function client(handler: (req: Request) => Response | Promise<Response>) {
  const calls: Request[] = [];
  const c = createApiClient({
    baseUrl: "http://x/v1",
    getAccessToken: () => "t",
    refresh: async () => undefined,
    fetch: (async (r: Request) => { calls.push(r.clone()); return handler(r); }) as unknown as typeof fetch,
  });
  return { c, calls };
}

describe("findPatients", () => {
  const hit = { patientId: "p1", chartNo: 7, firstName: "Əli", lastName: "Məmmədov", fatherName: "Rza" };

  it("uses Elasticsearch for 2+ characters", async () => {
    const { c, calls } = client(() => json(200, [hit]));
    const r = await findPatients(c, " Məm ");
    expect(new URL(calls[0]!.url).pathname).toBe("/v1/search/patients");
    expect(new URL(calls[0]!.url).searchParams.get("q")).toBe("Məm");
    expect(r.rows).toEqual([{ id: "p1", chartNo: 7, name: "Məmmədov Əli Rza" }]);
    expect(r.nextCursor).toBeNull();
  });

  it.each([503, 403])("falls back to SQL search on %i", async (status) => {
    const { c, calls } = client((req) =>
      new URL(req.url).pathname.endsWith("/search/patients")
        ? json(status, { code: "search.unavailable" })
        : json(200, { items: [{ id: "p2", chartNo: 9, firstName: "A", lastName: "B", phoneMasked: "+994•••67" }], nextCursor: "n1" }));
    const r = await findPatients(c, "bb");
    expect(calls.map((x) => new URL(x.url).pathname)).toEqual(["/v1/search/patients", "/v1/patients"]);
    expect(r.rows[0]).toMatchObject({ id: "p2", name: "B A", phoneMasked: "+994•••67" });
    expect(r.nextCursor).toBe("n1");
  });

  it("does not hide other errors behind the fallback", async () => {
    const { c, calls } = client(() => json(500, { code: "boom" }));
    await expect(findPatients(c, "bb")).rejects.toMatchObject({ status: 500 });
    expect(calls).toHaveLength(1);
  });

  it("empty or 1-char text lists via SQL; paging with a cursor never hits ES", async () => {
    const { c, calls } = client(() => json(200, { items: [], nextCursor: null }));
    await findPatients(c, "a");
    await findPatients(c, "abc", "cur");
    expect(calls.map((x) => new URL(x.url).pathname)).toEqual(["/v1/patients", "/v1/patients"]);
    expect(new URL(calls[1]!.url).searchParams.get("cursor")).toBe("cur");
  });
});

describe("registerPatient", () => {
  const body = { branchId: "b", firstName: "A", lastName: "B" };

  it("sends the idempotency key and confirmDuplicate flag", async () => {
    const { c, calls } = client(() => json(201, { id: "new" }));
    expect(await registerPatient(c, body, "key-1", true)).toEqual({ kind: "created", id: "new" });
    expect(calls[0]!.headers.get("idempotency-key")).toBe("key-1");
    expect(new URL(calls[0]!.url).searchParams.get("confirmDuplicate")).toBe("true");
  });

  it("treats 409 patient.duplicate as a decision, not an error", async () => {
    const { c } = client(() => json(409, { code: "patient.duplicate" }));
    expect(await registerPatient(c, body, "k", false)).toEqual({ kind: "duplicate" });
  });

  it("other 409s are real errors", async () => {
    const { c } = client(() => json(409, { code: "something.else" }));
    await expect(registerPatient(c, body, "k", false)).rejects.toMatchObject({ code: "something.else" });
  });

  it("validation failures become a user message", async () => {
    const { c } = client(() => json(422, { code: "patient.invalid_phone" }));
    expect(await registerPatient(c, body, "k", false)).toMatchObject({ kind: "invalid" });
  });
});

describe("payments", () => {
  it("keeps the same key until reset (safe retries) and then changes it", () => {
    const a = new PaymentAttempt();
    const k = a.idempotencyKey;
    expect(a.idempotencyKey).toBe(k);
    a.reset();
    expect(a.idempotencyKey).not.toBe(k);
  });

  it("parses amounts strictly", () => {
    expect(parseAmount("12,50")).toBe(12.5);
    expect(parseAmount(" 7 ")).toBe(7);
    for (const bad of ["", "0", "-5", "abc", "1.234", "NaN", "Infinity"]) expect(parseAmount(bad), bad).toBeNull();
  });

  it("posts method, amount and the key; surfaces ApiError with code", async () => {
    const { c, calls } = client(() => json(422, { code: "billing.shift_required" }));
    await expect(recordPayment(c, "inv1", "cash", 5, "K")).rejects.toMatchObject({ code: "billing.shift_required", status: 422 });
    expect(calls[0]!.headers.get("idempotency-key")).toBe("K");
    expect(await calls[0]!.json()).toEqual({ method: "cash", amount: 5 });
  });
});

describe("calendar helpers", () => {
  it("day range spans exactly the local day (DST-safe: built from local midnight)", () => {
    const { from, to } = dayRange("2026-10-13");
    expect(new Date(from).getTime()).toBe(new Date(2026, 9, 13).getTime());
    expect(new Date(to).getTime()).toBe(new Date(2026, 9, 14).getTime());
  });

  it("todayLocal pads and uses local fields", () => {
    expect(todayLocal(new Date(2026, 0, 5))).toBe("2026-01-05");
  });

  it("only offers sensible actions per status", () => {
    expect(actionsFor("booked")).toEqual(["check-in", "no-show", "cancel"]);
    expect(actionsFor("checked_in")).toEqual(["cancel"]);
    for (const s of ["completed", "cancelled", "no_show", "in_progress"]) expect(actionsFor(s)).toEqual([]);
  });
});

void vi;
