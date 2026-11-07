import { ApiError, toApiError, newIdempotencyKey, type ApiClient, type components } from "@dentacore/api-client";

export type PatientCreate = components["schemas"]["PatientCreate"];

export type RegisterOutcome =
  | { kind: "created"; id: string }
  | { kind: "duplicate" }
  | { kind: "invalid"; message: string };

/**
 * Qeydiyyat. 409 `patient.duplicate` xəta deyil, istifadəçi qərarıdır (ailə üzvləri eyni telefonu paylaşa bilər):
 * UI soruşur, təsdiqdən sonra `confirmDuplicate=true` ilə eyni açarla təkrar göndərilir.
 */
export async function registerPatient(client: ApiClient, body: PatientCreate, key: string, confirmDuplicate: boolean): Promise<RegisterOutcome> {
  const res = await client.POST("/patients", {
    params: { query: { confirmDuplicate }, header: { "Idempotency-Key": key } },
    body,
  });
  if (res.response.ok && res.data?.id) return { kind: "created", id: res.data.id };
  const err = res.error as { code?: string; detail?: string } | undefined;
  if (res.response.status === 409 && err?.code === "patient.duplicate") return { kind: "duplicate" };
  if (res.response.status === 400 || res.response.status === 422) return { kind: "invalid", message: "Daxil edilmiş məlumatları yoxlayın." };
  throw toApiError(res.error, res.response);
}

export { newIdempotencyKey };
