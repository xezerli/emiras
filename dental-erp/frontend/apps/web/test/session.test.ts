import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { login, logout, refreshSession, tokenStore } from "@/shared/session";

const json = (status: number, body: unknown) => new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
let fetchMock: ReturnType<typeof vi.fn>;

beforeEach(() => {
  fetchMock = vi.fn();
  vi.stubGlobal("fetch", fetchMock);
  tokenStore.set(undefined);
});
afterEach(() => vi.unstubAllGlobals());

describe("browser session client", () => {
  it("login stores only the access token and calls the same-origin BFF with credentials", async () => {
    fetchMock.mockResolvedValue(json(200, { accessToken: "AT", expiresIn: 600 }));
    expect(await login("a@b.az", "pw")).toEqual({ kind: "ok" });
    const [url, init] = fetchMock.mock.calls[0]! as [string, RequestInit];
    expect(url).toBe("/api/session/login");
    expect(init.credentials).toBe("same-origin");
    expect(tokenStore.get()).toBe("AT");
    expect(tokenStore.expiresAt()).toBeGreaterThan(Date.now());
  });

  it("login surfaces a 2FA challenge without storing a token", async () => {
    fetchMock.mockResolvedValue(json(202, { challengeId: "c1", methods: ["totp"] }));
    expect(await login("a@b.az", "pw")).toEqual({ kind: "challenge", challengeId: "c1", methods: ["totp"] });
    expect(tokenStore.get()).toBeUndefined();
  });

  it("login failure throws ApiError with the server code", async () => {
    fetchMock.mockResolvedValue(json(401, { code: "auth.invalid_credentials", title: "x" }));
    await expect(login("a@b.az", "bad")).rejects.toMatchObject({ status: 401, code: "auth.invalid_credentials" });
    expect(tokenStore.get()).toBeUndefined();
  });

  it("refresh returns the new token, or clears the old one when the session is gone", async () => {
    fetchMock.mockResolvedValueOnce(json(200, { accessToken: "AT2", expiresIn: 60 }));
    expect(await refreshSession()).toBe("AT2");
    expect(tokenStore.get()).toBe("AT2");

    fetchMock.mockResolvedValueOnce(json(401, { code: "bff.no_session" }));
    expect(await refreshSession()).toBeUndefined();
    expect(tokenStore.get()).toBeUndefined();
  });

  it("logout forwards the bearer token and clears memory even if the BFF is unreachable", async () => {
    tokenStore.set("AT", 60);
    fetchMock.mockRejectedValue(new Error("offline"));
    await logout();
    const init = fetchMock.mock.calls[0]![1] as RequestInit;
    expect((init.headers as Record<string, string>).authorization).toBe("Bearer AT");
    expect(tokenStore.get()).toBeUndefined();
  });
});
