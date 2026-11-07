import { ApiError, unwrap, type ApiClient } from "@dentacore/api-client";

export interface PatientRow {
  id: string;
  chartNo: number;
  name: string;
  phoneMasked?: string | null;
}

const MIN_SEARCH = 2;

/**
 * Axtarış: Elasticsearch (hərf səhvinə dözümlü, prefiks). Mühərrik 503 verərsə (və ya axtarış icazəsi scope səbəbilə 403)
 * adi PostgreSQL axtarışına keçilir: istifadəçi fərqi görmür, yalnız dəqiqlik azalır.
 * Mətn yoxdursa və ya çox qısadırsa adi siyahı (kursor ilə) göstərilir.
 */
export async function findPatients(client: ApiClient, text: string, cursor?: string): Promise<{ rows: PatientRow[]; nextCursor: string | null }> {
  const q = text.trim();
  if (q.length >= MIN_SEARCH && !cursor) {
    try {
      const hits = unwrap(await client.GET("/search/patients", { params: { query: { q, limit: 25 } } }));
      return {
        rows: hits.map((h) => ({ id: h.patientId ?? "", chartNo: h.chartNo ?? 0, name: [h.lastName, h.firstName, h.fatherName].filter(Boolean).join(" ") })),
        nextCursor: null,
      };
    } catch (e) {
      if (!(e instanceof ApiError) || (e.status !== 503 && e.status !== 403)) throw e;
    }
  }
  const page = unwrap(await client.GET("/patients", { params: { query: { q: q || undefined, cursor, limit: 25 } } }));
  return {
    rows: (page.items ?? []).map((p) => ({ id: p.id ?? "", chartNo: p.chartNo ?? 0, name: `${p.lastName ?? ""} ${p.firstName ?? ""}`.trim(), phoneMasked: p.phoneMasked })),
    nextCursor: page.nextCursor ?? null,
  };
}
