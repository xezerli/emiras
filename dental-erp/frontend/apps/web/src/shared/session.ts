import { ApiError, createApiClient, type ApiClient } from "@dentacore/api-client";

/** Access token YALNIZ yaddaşdadır (XSS-də localStorage-dan oğurlanmasın); refresh token httpOnly cookie-dədir. */
let accessToken: string | undefined;
let expiresAt = 0;
const listeners = new Set<() => void>();

export const tokenStore = {
  get: () => accessToken,
  set(token: string | undefined, expiresInSeconds = 0) {
    accessToken = token;
    expiresAt = token ? Date.now() + expiresInSeconds * 1000 : 0;
    listeners.forEach((l) => l());
  },
  expiresAt: () => expiresAt,
  subscribe(l: () => void) {
    listeners.add(l);
    return () => void listeners.delete(l);
  },
};

interface SessionPayload {
  accessToken: string;
  expiresIn: number;
}

async function bff(path: string, init?: RequestInit): Promise<Response> {
  return fetch(`/api/session/${path}`, { method: "POST", credentials: "same-origin", headers: { "content-type": "application/json" }, ...init });
}

/** BFF vasitəsilə cookie-dəki refresh token ilə yeni access token alır; sessiya yoxdursa undefined. */
export async function refreshSession(): Promise<string | undefined> {
  const res = await bff("refresh");
  if (!res.ok) {
    tokenStore.set(undefined);
    return undefined;
  }
  const data = (await res.json()) as SessionPayload;
  tokenStore.set(data.accessToken, data.expiresIn);
  return data.accessToken;
}

export type LoginResult = { kind: "ok" } | { kind: "challenge"; challengeId: string; methods: string[] };

export async function login(email: string, password: string): Promise<LoginResult> {
  const res = await bff("login", { body: JSON.stringify({ email, password }) });
  if (res.status === 202) {
    const c = (await res.json()) as { challengeId: string; methods: string[] };
    return { kind: "challenge", ...c };
  }
  if (!res.ok) throw await ApiError.fromResponse(res);
  const data = (await res.json()) as SessionPayload;
  tokenStore.set(data.accessToken, data.expiresIn);
  return { kind: "ok" };
}

export async function logout(): Promise<void> {
  const token = tokenStore.get();
  await bff("logout", { headers: { "content-type": "application/json", ...(token ? { authorization: `Bearer ${token}` } : {}) } }).catch(() => undefined);
  tokenStore.set(undefined);
}

let client: ApiClient | undefined;

export function api(onSessionExpired: () => void): ApiClient {
  client ??= createApiClient({
    baseUrl: "/v1",
    getAccessToken: tokenStore.get,
    refresh: refreshSession,
    onSessionExpired,
    tenant: process.env.NEXT_PUBLIC_DEV_TENANT,
  });
  return client;
}
