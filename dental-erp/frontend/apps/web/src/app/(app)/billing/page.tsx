"use client";
import { ApiError, toApiError, unwrap } from "@dentacore/api-client";
import { Alert, Badge, Button, Card, EmptyState, SelectField, Skeleton } from "@dentacore/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import Link from "next/link";
import { useState } from "react";
import { INVOICE_STATUS } from "@/features/billing/status";
import { useAuth } from "@/shared/AuthProvider";
import { formatMoney } from "@/shared/format";
import { can } from "@/shared/permissions";
import { useApi } from "@/shared/useApi";

function ShiftCard() {
  const client = useApi();
  const { user } = useAuth();
  const qc = useQueryClient();
  const allowed = can(user?.permissions, "payment:write");
  const shift = useQuery({
    queryKey: ["shift", "current"],
    enabled: allowed,
    retry: false,
    queryFn: async () => {
      const res = await client.GET("/cash-shifts/current");
      if (res.response.status === 404) return null;
      return unwrap(res);
    },
  });
  const open = useMutation({
    mutationFn: async () => {
      const branchId = user?.branches?.[0];
      if (!branchId) throw new Error("no-branch");
      const res = await client.POST("/cash-shifts/open", { body: { branchId, openingCash: 0 } });
      if (!res.response.ok) throw toApiError(res.error, res.response);
    },
    onSuccess: () => qc.invalidateQueries({ queryKey: ["shift"] }),
  });
  if (!allowed) return null;
  return (
    <Card aria-labelledby="shift-h">
      <h2 id="shift-h">Kassa smeni</h2>
      {shift.isPending ? <Skeleton height={24} /> : shift.data ? (
        <p><Badge tone="success">Açıq</Badge> Açılış nağdı: {formatMoney(shift.data.openingCash ?? 0)}</p>
      ) : (
        <>
          <p><Badge tone="neutral">Bağlı</Badge> Nağd ödəniş üçün smen açın.</p>
          <Button variant="tonal" loading={open.isPending} onClick={() => open.mutate()}>Smeni aç</Button>
          {open.isError && <Alert tone="error" title="Smen açılmadı" />}
        </>
      )}
    </Card>
  );
}

export default function BillingPage() {
  const client = useApi();
  const [status, setStatus] = useState("");
  const q = useQuery({
    queryKey: ["invoices", "list", status],
    queryFn: async () => unwrap(await client.GET("/invoices", { params: { query: { status: (status || undefined) as never, limit: 25 } } })),
  });
  const items = q.data?.items ?? [];

  return (
    <>
      <h1>Maliyyə</h1>
      <ShiftCard />
      <SelectField label="Status" value={status} onChange={(e) => setStatus(e.target.value)}>
        <option value="">Hamısı</option>
        {Object.entries(INVOICE_STATUS).map(([v, s]) => <option key={v} value={v}>{s.label}</option>)}
      </SelectField>
      {q.isPending && <div role="status" aria-label="Yüklənir"><Skeleton height={120} /></div>}
      {q.isError && <Alert tone="error" title="Fakturalar yüklənmədi" />}
      {q.isSuccess && items.length === 0 && <EmptyState title="Faktura yoxdur" />}
      {items.length > 0 && (
        <table className="data-table">
          <caption className="dc-sr-only">Fakturalar</caption>
          <thead><tr><th scope="col">№</th><th scope="col">Status</th><th scope="col" className="num">Cəmi</th><th scope="col" className="num">Qalıq</th></tr></thead>
          <tbody>
            {items.map((i) => {
              const st = INVOICE_STATUS[i.status ?? ""] ?? { label: i.status ?? "—", tone: "neutral" as const };
              return (
                <tr key={i.id}>
                  <td><Link href={`/billing/${i.id}`}>{i.number}</Link></td>
                  <td><Badge tone={st.tone}>{st.label}</Badge></td>
                  <td className="num">{formatMoney(i.total ?? 0)}</td>
                  <td className="num">{formatMoney(i.balance ?? 0)}</td>
                </tr>
              );
            })}
          </tbody>
        </table>
      )}
    </>
  );
}
