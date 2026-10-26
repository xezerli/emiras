// @vitest-environment node
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { POST as login } from "@/app/api/session/login/route";
import { POST as logout } from "@/app/api/session/logout/route";
import { POST as refresh } from "@/app/api/session/refresh/route";
import { isSameOrigin, readRefreshCookie, refreshCookie } from "@/server/identity";

const HOST = "clinic.dentacore.app";
const req = (path: string, init: { origin?: string | null; body?: unknown; cookie?: string; auth?: string } = {}) => {
  const headers = new Headers({ host: HOST, "content-type": "application/json" });
  if (init.origin !== null) headers.set("origin", init.origin ?? `https://${HOST}`);
  if (init.cookie) headers.set("cookie", init.cookie);
  if (init.auth) headers.set("authorization", init.auth);
  return new Request(`https://${HOST}/api/session/${path}`, { method: "POST", headers, body: init.body === undefined ? undefined : JSON.stringify(init.body) });
};
const upstream = (status: number, body: unknown) => new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });

let fetchMock: ReturnType<typeof vi.fn>;
beforeEach(() => {
  fetchMock = vi.fn();
  vi.stubGlobal("fetch", fetchMock);
  vi.stubEnv("API_ORIGIN", "http://gw");
  vi.stubEnv("DEV_TENANT", "");
});
afterEach(() => {
  vi.unstubAllGlobals();
  vi.unstubAllEnvs();
});

describe("origin check", () => {
  it("rejects missing, foreign and malformed origins", () => {
    expect(isSameOrigin(req("login", { origin: null }))).toBe(false);
    expect(isSameOrigin(req("login", { origin: "https://evil.example" }))).toBe(false);
    expect(isSameOrigin(req("login", { origin: "not a url" }))).toBe(false);
    expect(isSameOrigin(req("login"))).toBe(true);
  });

  it.each([["login"], ["refresh"], ["logout"]])("%s returns 403 for cross-origin and never calls upstream", async (name) => {
    const handler = { login, refresh, logout }[name as "login"];
    const res = await handler(req(name, { origin: "https://evil.example", body: { email: "a@b.c", password: "x" }, cookie: "dc_rt=r" }));
    expect(res.status).toBe(403);
    expect(fetchMock).not.toHaveBeenCalled();
  });
});

describe("login", () => {
  it("puts the refresh token in an httpOnly Strict cookie and never in the body", async () => {
    fetchMock.mockResolvedValue(upstream(200, { accessToken: "AT", refreshToken: "RT", expiresIn: 900, tokenType: "Bearer" }));
    const res = await login(req("login", { body: { email: "a@b.c", password: "pw" } }));
    expect(res.status).toBe(200);
    const body = await res.json();
    expect(body).toEqual({ accessToken: "AT", expiresIn: 900 });
    expect(JSON.stringify(body)).not.toContain("RT");
    const cookie = res.headers.get("set-cookie")!;
    expect(cookie).toContain("dc_rt=RT");
    expect(cookie).toMatch(/HttpOnly/);
    expect(cookie).toMatch(/SameSite=Strict/);
    expect(cookie).toContain("Path=/api/session");
    expect(cookie).toContain("Max-Age=604800");   // 7 gün: serverdəki refresh token ömrü ilə eyni (cookie ondan uzun yaşamamalıdır)
    expect(res.headers.get("cache-control")).toBe("no-store");
  });

  it("forwards the host for tenant resolution, and X-Tenant only when DEV_TENANT is set", async () => {
    fetchMock.mockImplementation(async () => upstream(200, { accessToken: "a", refreshToken: "r", expiresIn: 1 }));
    await login(req("login", { body: { email: "a@b.c", password: "pw" } }));
    let h = new Headers((fetchMock.mock.calls[0]![1] as RequestInit).headers);
    expect(h.get("x-forwarded-host")).toBe(HOST);
    expect(h.has("x-tenant")).toBe(false);

    vi.stubEnv("DEV_TENANT", "demo");
    await login(req("login", { body: { email: "a@b.c", password: "pw" } }));
    h = new Headers((fetchMock.mock.calls[1]![1] as RequestInit).headers);
    expect(h.get("x-tenant")).toBe("demo");
  });

  it("returns the 2FA challenge without any cookie", async () => {
    fetchMock.mockResolvedValue(upstream(202, { challengeId: "c1", methods: ["totp"] }));
    const res = await login(req("login", { body: { email: "a@b.c", password: "pw" } }));
    expect(res.status).toBe(202);
    expect(res.headers.get("set-cookie")).toBeNull();
    expect(await res.json()).toEqual({ challengeId: "c1", methods: ["totp"] });
  });

  it("passes problem+json through unchanged and sets no cookie", async () => {
    fetchMock.mockResolvedValue(upstream(401, { code: "auth.invalid_credentials", title: "x" }));
    const res = await login(req("login", { body: { email: "a@b.c", password: "bad" } }));
    expect(res.status).toBe(401);
    expect((await res.json()).code).toBe("auth.invalid_credentials");
    expect(res.headers.get("set-cookie")).toBeNull();
  });

  it("validates input before calling upstream", async () => {
    const res = await login(req("login", { body: { email: "a@b.c" } }));
    expect(res.status).toBe(400);
    expect(fetchMock).not.toHaveBeenCalled();
  });
});

describe("refresh", () => {
  it("401s without a cookie and does not call upstream", async () => {
    const res = await refresh(req("refresh"));
    expect(res.status).toBe(401);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("rotates the cookie on success", async () => {
    fetchMock.mockResolvedValue(upstream(200, { accessToken: "AT2", refreshToken: "RT2", expiresIn: 900 }));
    const res = await refresh(req("refresh", { cookie: "other=1; dc_rt=RT1" }));
    expect(JSON.parse((fetchMock.mock.calls[0]![1] as RequestInit).body as string)).toEqual({ refreshToken: "RT1" });
    expect(res.headers.get("set-cookie")).toContain("dc_rt=RT2");
  });

  it("clears the cookie when the refresh token is rejected", async () => {
    fetchMock.mockResolvedValue(upstream(401, { code: "auth.invalid_refresh_token" }));
    const res = await refresh(req("refresh", { cookie: "dc_rt=stale" }));
    expect(res.status).toBe(401);
    expect(res.headers.get("set-cookie")).toMatch(/dc_rt=;.*Max-Age=0/);
  });

  it("keeps the cookie on a transient upstream failure", async () => {
    fetchMock.mockResolvedValue(upstream(503, { title: "down" }));
    const res = await refresh(req("refresh", { cookie: "dc_rt=ok" }));
    expect(res.status).toBe(503);
    expect(res.headers.get("set-cookie")).toBeNull();
  });
});

describe("logout", () => {
  it("revokes upstream and clears the cookie", async () => {
    fetchMock.mockResolvedValue(new Response(null, { status: 204 }));
    const res = await logout(req("logout", { cookie: "dc_rt=RT", auth: "Bearer AT" }));
    expect(res.status).toBe(204);
    expect(res.headers.get("set-cookie")).toMatch(/Max-Age=0/);
    const [url, init] = fetchMock.mock.calls[0]! as [string, RequestInit];
    expect(url).toBe("http://gw/v1/auth/logout");
    expect(new Headers(init.headers).get("authorization")).toBe("Bearer AT");
  });

  it("clears the cookie even if upstream is unreachable", async () => {
    fetchMock.mockRejectedValue(new Error("down"));
    const res = await logout(req("logout", { cookie: "dc_rt=RT", auth: "Bearer AT" }));
    expect(res.status).toBe(204);
    expect(res.headers.get("set-cookie")).toMatch(/Max-Age=0/);
  });
});

describe("cookie helpers", () => {
  it("round-trips values with special characters", () => {
    const c = refreshCookie("a=b/c+d", 10);
    const value = c.split(";")[0]!;
    expect(readRefreshCookie(new Request("http://x", { headers: { cookie: value } }))).toBe("a=b/c+d");
  });

  it("marks the cookie Secure in production only", () => {
    vi.stubEnv("NODE_ENV", "production");
    expect(refreshCookie("x", 1)).toContain("Secure");
    vi.stubEnv("NODE_ENV", "development");
    expect(refreshCookie("x", 1)).not.toContain("Secure");
  });
});
