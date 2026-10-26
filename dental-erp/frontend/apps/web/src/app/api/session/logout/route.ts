import { NextResponse } from "next/server";
import { clearRefreshCookie, forbidden, isSameOrigin, readRefreshCookie, upstreamHeaders } from "@/server/identity";

export async function POST(req: Request) {
  if (!isSameOrigin(req)) return forbidden();
  const refreshToken = readRefreshCookie(req);
  const auth = req.headers.get("authorization");
  if (refreshToken && auth) {
    const headers = upstreamHeaders(req);
    headers.set("authorization", auth);
    // Upstream uğursuz olsa belə yerli cookie silinir: istifadəçi çıxış edə bilməli qalır
    await fetch(`${process.env.API_ORIGIN ?? "http://localhost:8080"}/v1/auth/logout`, {
      method: "POST",
      headers,
      body: JSON.stringify({ refreshToken }),
      cache: "no-store",
    }).catch(() => undefined);
  }
  const res = new NextResponse(null, { status: 204, headers: { "cache-control": "no-store" } });
  res.headers.append("set-cookie", clearRefreshCookie());
  return res;
}
