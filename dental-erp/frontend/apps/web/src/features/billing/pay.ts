import { ApiError, toApiError, newIdempotencyKey, type ApiClient } from "@dentacore/api-client";

export const METHODS = [
  { value: "cash", label: "Nağd" },
  { value: "card", label: "Kart" },
  { value: "pos", label: "POS terminal" },
  { value: "transfer", label: "Köçürmə" },
] as const;

export type PayMethod = (typeof METHODS)[number]["value"];

/**
 * Bir ödəniş cəhdi = bir Idempotency-Key. Şəbəkə kəsilib təkrar basılanda EYNİ açar göndərilir (iki dəfə çıxılmasın);
 * yalnız uğurdan sonra yeni açar verilir.
 */
export class PaymentAttempt {
  private key = newIdempotencyKey();

  get idempotencyKey(): string {
    return this.key;
  }

  reset(): void {
    this.key = newIdempotencyKey();
  }
}

/** "12,50" və "12.50" qəbul edilir; mənfi/sıfır/NaN və 2-dən çox onluq rəqəm rədd olunur. */
export function parseAmount(input: string): number | null {
  const n = Number(input.trim().replace(",", "."));
  if (!Number.isFinite(n) || n <= 0) return null;
  return Math.round(n * 100) === Number((n * 100).toFixed(6)) ? n : null;
}

export const PAY_ERRORS: Record<string, string> = {
  "billing.shift_required": "Nağd ödəniş üçün əvvəlcə kassa smenini açın.",
  "billing.shift_closed": "Smen bağlanıb. Yenisini açın.",
  "billing.shift_branch_mismatch": "Açıq smeniniz başqa filialdadır.",
  "billing.overpayment": "Məbləğ qalıq borcdan çoxdur.",
  "billing.method_not_supported": "Bu ödəniş üsulu hələ dəstəklənmir.",
};

export async function recordPayment(client: ApiClient, invoiceId: string, method: PayMethod, amount: number, key: string): Promise<void> {
  const res = await client.POST("/invoices/{invoiceId}/payments", {
    params: { path: { invoiceId }, header: { "Idempotency-Key": key } },
    body: { method, amount },
  });
  if (!res.response.ok) throw toApiError(res.error, res.response);
}
