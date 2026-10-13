import createClient from "openapi-fetch";
import type { paths } from "./schema";

export type { paths, components, operations } from "./schema";

/** RFC 9457 problem+json; `code` is DentaCore's stable machine-readable error code. */
export class ApiError extends Error {
  readonly status: number;
  readonly code: string;
  readonly title: string;
  readonly extensions: Record<string, unknown>;

  constructor(status: number, code: string, title: string, detail: string | undefined, extensions: Record<string, unknown> = {}) {
    super(detail ?? title);
    this.name = "ApiError";
    this.status = status;
    this.code = code;
    this.title = title;
    this.extensions = extensions;
  }

  static async fromResponse(response: Response): Promise<ApiError> {
    let body: Record<string, unknown> = {};
    try {
      const parsed: unknown = await response.clone().json();
      if (parsed && typeof parsed === "object") body = parsed as Record<string, unknown>;
    } catch {
      // non-JSON body (proxy error page etc.): fall through to defaults
    }
    const { type: _t, title, status: _s, detail, instance: _i, code, ...extensions } = body;
    return new ApiError(
      response.status,
      typeof code === "string" ? code : `http.${response.status}`,
      typeof title === "string" ? title : response.statusText || "Request failed",
      typeof detail === "string" ? detail : undefined,
      extensions,
    );
  }
}

/** Builds an ApiError from an already-parsed openapi-fetch result (the body has been consumed, so `response.clone()` is useless). */
export function toApiError(error: unknown, response: Response): ApiError {
  const e = (error && typeof error === "object" ? error : {}) as Record<string, unknown>;
  const { type: _t, title, status: _s, detail, instance: _i, code, ...extensions } = e;
  return new ApiError(
    response.status,
    typeof code === "string" ? code : `http.${response.status}`,
    typeof title === "string" ? title : response.statusText || "Request failed",
    typeof detail === "string" ? detail : undefined,
    extensions,
  );
}

/** Unwraps an openapi-fetch result: returns data or throws ApiError. */
export function unwrap<T>(result: { data?: T; error?: unknown; response: Response }): T {
  if (result.error !== undefined || !result.response.ok) throw toApiError(result.error, result.response);
  return result.data as T;
}

export interface ApiClientOptions {
  /** Base URL of the API (default `/v1`, same-origin through the ingress/Gateway). */
  baseUrl?: string;
  /** Current access token (kept in memory only). */
  getAccessToken: () => string | undefined;
  /**
   * Obtains a new access token (e.g. via the BFF refresh route). Called at most once concurrently;
   * resolve `undefined` when the session is over.
   */
  refresh: () => Promise<string | undefined>;
  /** Called when refresh fails and the user must sign in again. */
  onSessionExpired?: () => void;
  /** Dev only: sent as `X-Tenant`. Production resolves the tenant from the host name. */
  tenant?: string;
  fetch?: typeof fetch;
}

/** Generates a fresh idempotency key for one logical payment/refund attempt (reuse it on retries!). */
export function newIdempotencyKey(): string {
  return globalThis.crypto.randomUUID();
}

/** If-Match header value from a numeric/string row version. */
export function ifMatch(rowVersion: number | string): { "If-Match": string } {
  return { "If-Match": `"${rowVersion}"` };
}

export function createApiClient(options: ApiClientOptions) {
  const baseFetch = options.fetch ?? ((input: Request) => globalThis.fetch(input));
  let inflight: Promise<string | undefined> | null = null;

  const refreshOnce = (): Promise<string | undefined> => {
    inflight ??= options.refresh().finally(() => {
      inflight = null;
    });
    return inflight;
  };

  const withAuth = (request: Request, token: string | undefined): Request => {
    if (token) request.headers.set("Authorization", `Bearer ${token}`);
    if (options.tenant) request.headers.set("X-Tenant", options.tenant);
    return request;
  };

  const authedFetch = async (input: Request): Promise<Response> => {
    // The body can be consumed only once, so keep a clone for the single retry.
    const retryCopy = input.clone();
    const first = await baseFetch(withAuth(input, options.getAccessToken()));
    if (first.status !== 401) return first;

    const token = await refreshOnce();
    if (!token) {
      options.onSessionExpired?.();
      return first;
    }
    return baseFetch(withAuth(retryCopy, token));
  };

  return createClient<paths>({ baseUrl: options.baseUrl ?? "/v1", fetch: authedFetch });
}

export type ApiClient = ReturnType<typeof createApiClient>;
