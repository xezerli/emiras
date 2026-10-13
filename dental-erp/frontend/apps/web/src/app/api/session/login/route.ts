import { NextResponse } from "next/server";
import { callIdentity, forbidden, isSameOrigin, passthroughProblem, sessionResponse } from "@/server/identity";

export async function POST(req: Request) {
  if (!isSameOrigin(req)) return forbidden();
  const body = (await req.json().catch(() => null)) as { email?: string; password?: string; deviceId?: string } | null;
  if (!body?.email || !body.password) {
    return NextResponse.json({ title: "Invalid request", status: 400, code: "bff.invalid" }, { status: 400 });
  }
  const upstream = await callIdentity(req, "login", { email: body.email, password: body.password, deviceId: body.deviceId });
  if (upstream.status === 202) {
    // 2FA tələb olunur: challenge brauzerə qaytarılır, token yoxdur
    return NextResponse.json(await upstream.json(), { status: 202, headers: { "cache-control": "no-store" } });
  }
  if (!upstream.ok) return passthroughProblem(upstream);
  return sessionResponse(await upstream.json());
}
