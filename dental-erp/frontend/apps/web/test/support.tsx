import { createApiClient } from "@dentacore/api-client";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render } from "@testing-library/react";
import type { ReactElement } from "react";
import { vi } from "vitest";

export const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });

export type Handler = (req: Request) => Response | Promise<Response>;

/** Gerçək createApiClient + saxta fetch: səhifələr eyni kodla işləyir, yalnız şəbəkə əvəz olunur. */
export function fakeApi(handler: Handler) {
  const calls: Request[] = [];
  const client = createApiClient({
    baseUrl: "http://x/v1",
    getAccessToken: () => "t",
    refresh: async () => undefined,
    fetch: (async (r: Request) => {
      calls.push(r.clone());
      return handler(r);
    }) as unknown as typeof fetch,
  });
  return { client, calls };
}

export const path = (r: Request) => new URL(r.url).pathname.replace("/v1", "");

export function renderWithQuery(ui: ReactElement) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(<QueryClientProvider client={qc}>{ui}</QueryClientProvider>);
}

export function mockAuth(permissions: string[], branches: string[] = ["b1"]) {
  return {
    useAuth: () => ({
      status: "authenticated" as const,
      user: { id: "u", fullName: "Test", roles: [], branches, permissions: permissions.map((code) => ({ code, scope: "tenant" as const })) },
      signOut: vi.fn(),
    }),
  };
}
