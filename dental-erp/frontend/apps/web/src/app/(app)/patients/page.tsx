"use client";
import { Alert, Button, EmptyState, Skeleton, TextField } from "@dentacore/ui";
import { useInfiniteQuery } from "@tanstack/react-query";
import Link from "next/link";
import { useEffect, useState } from "react";
import { findPatients } from "@/features/patients/search";
import { useAuth } from "@/shared/AuthProvider";
import { can } from "@/shared/permissions";
import { useApi } from "@/shared/useApi";

function useDebounced<T>(value: T, ms: number): T {
  const [v, setV] = useState(value);
  useEffect(() => {
    const t = setTimeout(() => setV(value), ms);
    return () => clearTimeout(t);
  }, [value, ms]);
  return v;
}

export default function PatientsPage() {
  const client = useApi();
  const { user } = useAuth();
  const [text, setText] = useState("");
  const q = useDebounced(text, 300);
  const query = useInfiniteQuery({
    queryKey: ["patients", "list", q],
    initialPageParam: undefined as string | undefined,
    queryFn: ({ pageParam }) => findPatients(client, q, pageParam),
    getNextPageParam: (last) => last.nextCursor ?? undefined,
  });
  const rows = query.data?.pages.flatMap((p) => p.rows) ?? [];

  return (
    <>
      <div style={{ display: "flex", justifyContent: "space-between", alignItems: "end", gap: 16, flexWrap: "wrap" }}>
        <h1>Pasiyentlər</h1>
        {can(user?.permissions, "patient:write") && <Link className="dc-btn dc-btn--filled" href="/patients/new">Yeni pasiyent</Link>}
      </div>
      <TextField label="Axtar" hint="Ad, soyad və ya kart nömrəsi" type="search" value={text} onChange={(e) => setText(e.target.value)} autoComplete="off" />
      <div aria-live="polite" className="dc-sr-only">{query.isSuccess ? `${rows.length} nəticə` : ""}</div>
      {query.isPending && <div role="status" aria-label="Yüklənir" style={{ display: "grid", gap: 8 }}><Skeleton height={40} /><Skeleton height={40} /><Skeleton height={40} /></div>}
      {query.isError && <Alert tone="error" title="Siyahı yüklənmədi">Bir qədər sonra yenidən cəhd edin.</Alert>}
      {query.isSuccess && rows.length === 0 && <EmptyState title="Pasiyent tapılmadı">{q ? "Yazılışı yoxlayın və ya yeni pasiyent əlavə edin." : "Hələ pasiyent yoxdur."}</EmptyState>}
      {rows.length > 0 && (
        <table className="data-table">
          <caption className="dc-sr-only">Pasiyentlər</caption>
          <thead><tr><th scope="col">№</th><th scope="col">Ad</th><th scope="col">Telefon</th></tr></thead>
          <tbody>
            {rows.map((r) => (
              <tr key={r.id}>
                <td>{r.chartNo}</td>
                <td><Link href={`/patients/${r.id}`}>{r.name}</Link></td>
                <td>{r.phoneMasked ?? "—"}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {query.hasNextPage && <Button variant="tonal" loading={query.isFetchingNextPage} onClick={() => void query.fetchNextPage()}>Daha çox</Button>}
    </>
  );
}
