import { NextResponse } from "next/server";

export const REFRESH_COOKIE = "dc_rt";
const API_ORIGIN = () => process.env.API_ORIGIN ?? "http://localhost:8080";

/** Yalnız eyni mənşəli sorğular: BFF cookie ilə işlədiyi üçün CSRF-ə qarşı SameSite=Strict-ə əlavə qat. */
export function isSameOrigin(req: Request): boolean {
  const origin = req.headers.get("origin");
  if (!origin) return false; // fetch POST həmişə Origin göndərir; yoxdursa şübhəlidir
  const host = req.headers.get("x-forwarded-host") ?? req.headers.get("host");
  try {
    return new URL(origin).host === host;
  } catch {
    return false;
  }
}

/**
 * Kirayəçi (tenant) tanıma: produksiyada Host başlığı (`clinic.dentacore.app`) Gateway-ə ötürülür;
 * yalnız dev-də `DEV_TENANT` ilə `X-Tenant` göndərilir.
 */
export function upstreamHeaders(req: Request): Headers {
  const h = new Headers({ "content-type": "application/json", accept: "application/json" });
  const devTenant = process.env.DEV_TENANT;
  if (devTenant) h.set("x-tenant", devTenant);
  const host = req.headers.get("x-forwarded-host") ?? req.headers.get("host");
  if (host) h.set("x-forwarded-host", host);
  return h;
}

export function refreshCookie(value: string, maxAgeSeconds: number): string {
  const secure = process.env.NODE_ENV === "production" ? "; Secure" : "";
  return `${REFRESH_COOKIE}=${encodeURIComponent(value)}; Path=/api/session; HttpOnly; SameSite=Strict; Max-Age=${maxAgeSeconds}${secure}`;
}

export function clearRefreshCookie(): string {
  return refreshCookie("", 0);
}

export function readRefreshCookie(req: Request): string | undefined {
  const raw = req.headers.get("cookie") ?? "";
  for (const part of raw.split(";")) {
    const [k, ...v] = part.trim().split("=");
    if (k === REFRESH_COOKIE) return decodeURIComponent(v.join("="));
  }
  return undefined;
}

export const forbidden = () =>
  NextResponse.json({ title: "Forbidden", status: 403, code: "bff.origin" }, { status: 403, headers: { "cache-control": "no-store" } });

export async function callIdentity(req: Request, path: string, body: unknown): Promise<Response> {
  return fetch(`${API_ORIGIN()}/v1/auth/${path}`, {
    method: "POST",
    headers: upstreamHeaders(req),
    body: JSON.stringify(body),
    cache: "no-store",
  });
}

interface TokenPayload {
  accessToken: string;
  refreshToken: string;
  expiresIn: number;
}

// Serverdəki refresh token ömrü 7 gündür (AuthPolicy.RefreshTokenLifetime): cookie ondan uzun yaşamamalıdır
const REFRESH_COOKIE_TTL = 60 * 60 * 24 * 7;

/** Token cavabını brauzerə uyğunlaşdırır: refresh token yalnız httpOnly cookie-yə yazılır, JSON-da qaytarılmır. */
export function sessionResponse(tokens: TokenPayload): NextResponse {
  const res = NextResponse.json({ accessToken: tokens.accessToken, expiresIn: tokens.expiresIn }, { headers: { "cache-control": "no-store" } });
  res.headers.append("set-cookie", refreshCookie(tokens.refreshToken, REFRESH_COOKIE_TTL));
  return res;
}

export async function passthroughProblem(upstream: Response, clearCookie = false): Promise<NextResponse> {
  const text = await upstream.text();
  const res = new NextResponse(text, {
    status: upstream.status,
    headers: { "content-type": upstream.headers.get("content-type") ?? "application/problem+json", "cache-control": "no-store" },
  });
  if (clearCookie) res.headers.append("set-cookie", clearRefreshCookie());
  return res;
}
