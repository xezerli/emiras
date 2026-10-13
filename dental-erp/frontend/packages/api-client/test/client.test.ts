import { describe, expect, it, vi } from "vitest";
import { ApiError, createApiClient, ifMatch, newIdempotencyKey, toApiError, unwrap } from "../src";

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });

function setup(responses: Response[], over: Partial<Parameters<typeof createApiClient>[0]> = {}) {
  const calls: Request[] = [];
  const fetchMock = vi.fn(async (req: Request) => {
    calls.push(req);
    const r = responses.shift();
    if (!r) throw new Error("unexpected request");
    return r;
  });
  let token: string | undefined = "t1";
  const refresh = vi.fn(async () => {
    token = "t2";
    return token;
  });
  const onSessionExpired = vi.fn();
  const client = createApiClient({
    baseUrl: "http://api.test/v1",
    getAccessToken: () => token,
    refresh,
    onSessionExpired,
    fetch: fetchMock as unknown as typeof fetch,
    ...over,
  });
  return { client, calls, refresh, onSessionExpired };
}

describe("api client", () => {
  it("sends the bearer token and the dev tenant header", async () => {
    const { client, calls } = setup([json(200, { id: 1 })], { tenant: "demo" });
    await client.GET("/auth/me");
    expect(calls[0]!.headers.get("authorization")).toBe("Bearer t1");
    expect(calls[0]!.headers.get("x-tenant")).toBe("demo");
  });

  it("omits X-Tenant when no tenant is configured (production)", async () => {
    const { client, calls } = setup([json(200, {})]);
    await client.GET("/auth/me");
    expect(calls[0]!.headers.has("x-tenant")).toBe(false);
  });

  it("refreshes once on 401 and retries with the new token", async () => {
    const { client, calls, refresh } = setup([json(401, { code: "auth.expired" }), json(200, { ok: true })]);
    const res = await client.GET("/auth/me");
    expect(res.response.status).toBe(200);
    expect(refresh).toHaveBeenCalledTimes(1);
    expect(calls[1]!.headers.get("authorization")).toBe("Bearer t2");
  });

  it("shares a single refresh between concurrent 401s", async () => {
    const { client, refresh } = setup([
      json(401, {}), json(401, {}), json(200, {}), json(200, {}),
    ]);
    await Promise.all([client.GET("/auth/me"), client.GET("/auth/me")]);
    expect(refresh).toHaveBeenCalledTimes(1);
  });

  it("re-sends the request body on retry", async () => {
    const { client, calls } = setup([json(401, {}), json(200, {})]);
    await client.POST("/auth/logout", { body: { refreshToken: "r" } });
    expect(await calls[0]!.text()).toContain("refreshToken");
    expect(await calls[1]!.text()).toContain("refreshToken");
  });

  it("does not retry twice and signals session expiry when refresh yields nothing", async () => {
    const { client, onSessionExpired, calls } = setup([json(401, {})], {
      refresh: async () => undefined,
    });
    const res = await client.GET("/auth/me");
    expect(res.response.status).toBe(401);
    expect(onSessionExpired).toHaveBeenCalledTimes(1);
    expect(calls).toHaveLength(1);
  });

  it("a second 401 after refresh is returned, not looped", async () => {
    const { client, calls } = setup([json(401, {}), json(401, {})]);
    const res = await client.GET("/auth/me");
    expect(res.response.status).toBe(401);
    expect(calls).toHaveLength(2);
  });
});

describe("errors and helpers", () => {
  it("unwrap throws ApiError with code, title and extensions", () => {
    const run = () =>
      unwrap({
        error: { type: "x", title: "Duplicate", status: 409, code: "patient.duplicate", detail: "same phone", candidates: [1] },
        response: new Response(null, { status: 409 }),
      });
    expect(run).toThrowError(ApiError);
    try {
      run();
    } catch (e) {
      const err = e as ApiError;
      expect(err.status).toBe(409);
      expect(err.code).toBe("patient.duplicate");
      expect(err.message).toBe("same phone");
      expect(err.extensions).toEqual({ candidates: [1] });
    }
  });

  it("unwrap returns data on success", () => {
    expect(unwrap({ data: { a: 1 }, response: new Response(null, { status: 200 }) })).toEqual({ a: 1 });
  });

  it("fromResponse tolerates non-JSON bodies", async () => {
    const err = await ApiError.fromResponse(new Response("<html>bad gateway</html>", { status: 502, statusText: "Bad Gateway" }));
    expect(err.code).toBe("http.502");
    expect(err.title).toBe("Bad Gateway");
  });

  it("idempotency keys are unique and If-Match is quoted", () => {
    expect(newIdempotencyKey()).not.toBe(newIdempotencyKey());
    expect(ifMatch(7)).toEqual({ "If-Match": '"7"' });
  });
});

describe("toApiError", () => {
  it("builds from a parsed body without touching the consumed response", () => {
    const e = toApiError({ code: "x.y", title: "T", detail: "D", extra: 1 }, new Response(null, { status: 422 }));
    expect([e.status, e.code, e.title, e.message, e.extensions]).toEqual([422, "x.y", "T", "D", { extra: 1 }]);
  });
  it("tolerates a missing/undefined body", () => {
    expect(toApiError(undefined, new Response(null, { status: 500, statusText: "Err" })).code).toBe("http.500");
  });
});
