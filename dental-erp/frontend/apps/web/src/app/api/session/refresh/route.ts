import { NextResponse } from "next/server";
import { callIdentity, forbidden, isSameOrigin, passthroughProblem, readRefreshCookie, sessionResponse } from "@/server/identity";

export async function POST(req: Request) {
  if (!isSameOrigin(req)) return forbidden();
  const refreshToken = readRefreshCookie(req);
  if (!refreshToken) {
    return NextResponse.json({ title: "No session", status: 401, code: "bff.no_session" }, { status: 401, headers: { "cache-control": "no-store" } });
  }
  const upstream = await callIdentity(req, "refresh", { refreshToken });
  // Etibarsız/istifadə olunmuş refresh token: cookie silinir ki, brauzer təkrar-təkrar cəhd etməsin
  if (upstream.status === 401 || upstream.status === 400) return passthroughProblem(upstream, true);
  if (!upstream.ok) return passthroughProblem(upstream);
  return sessionResponse(await upstream.json());
}
