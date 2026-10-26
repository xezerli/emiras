"use client";
import { unwrap } from "@dentacore/api-client";
import { Alert, Badge, Card, EmptyState, Skeleton } from "@dentacore/ui";
import { useQuery } from "@tanstack/react-query";
import { useRouter } from "next/navigation";
import { useEffect } from "react";
import { useAuth } from "@/shared/AuthProvider";
import { formatMoney } from "@/shared/format";
import { can, visibleNav } from "@/shared/permissions";
import { RevenueChart } from "@/shared/RevenueChart";
import { api } from "@/shared/session";

const ROOM: Record<string, { label: string; tone: "success" | "warning" | "neutral" }> = {
  free: { label: "Boş", tone: "success" },
  occupied: { label: "Məşğul", tone: "warning" },
  cleaning: { label: "Təmizlik", tone: "neutral" },
};

function Kpi({ label, value }: { label: string; value: string | number }) {
  return (
    <Card>
      <div className="kpi__label">{label}</div>
      <div className="kpi__value">{value}</div>
    </Card>
  );
}

export default function DashboardPage() {
  const { user, signOut } = useAuth();
  const router = useRouter();
  const allowed = can(user?.permissions, "report:read");
  // İdarə panelinə icazəsi olmayan rol (qəbul, həkim) ilk əlçatan bölməyə yönləndirilir
  const fallback = visibleNav(user?.permissions)[0]?.href;
  useEffect(() => {
    if (!allowed && fallback) router.replace(fallback);
  }, [allowed, fallback, router]);
  const q = useQuery({
    queryKey: ["dashboard", "summary"],
    enabled: allowed,
    queryFn: async () => unwrap(await api(() => void signOut()).GET("/dashboard/summary")),
  });

  if (!allowed) return <Alert tone="warning" title="İcazə yoxdur">İdarə panelinə baxmaq üçün icazəniz yoxdur.</Alert>;
  if (q.isPending) return <div role="status" aria-label="Yüklənir" style={{ display: "grid", gap: 12 }}><Skeleton height={80} /><Skeleton height={220} /></div>;
  if (q.isError) return <Alert tone="error" title="İdarə paneli yüklənmədi">Bir qədər sonra yenidən cəhd edin.</Alert>;

  const s = q.data;
  const a = s.appointmentsToday;
  return (
    <>
      <h1>İdarə paneli</h1>
      <div className="kpis">
        <Kpi label="Bu gün qəbullar" value={a ? `${a.completed ?? 0} / ${a.total ?? 0}` : "—"} />
        <Kpi label="Gözləyən" value={a?.waiting ?? 0} />
        <Kpi label="Gəlmədi" value={a?.noShow ?? 0} />
        <Kpi label="Bu günkü gəlir" value={formatMoney(s.revenueToday ?? 0)} />
        <Kpi label="Aylıq gəlir" value={formatMoney(s.revenueMonth ?? 0)} />
        <Kpi label="Borc" value={formatMoney(s.outstandingDebt ?? 0)} />
        <Kpi label="Yeni pasiyentlər" value={s.newPatients ?? 0} />
      </div>
      <div className="cols">
        <Card aria-labelledby="rev-h">
          <h2 id="rev-h">Gəlir</h2>
          {s.revenueSeries?.length ? <RevenueChart data={s.revenueSeries.map((p) => ({ day: p.day ?? "", amount: p.amount ?? 0 }))} /> : <EmptyState title="Məlumat yoxdur" level={3} />}
        </Card>
        <Card aria-labelledby="rooms-h">
          <h2 id="rooms-h">Otaqlar</h2>
          {s.rooms?.length ? (
            <ul style={{ listStyle: "none", padding: 0, display: "grid", gap: 8 }}>
              {s.rooms.map((r) => {
                const st = ROOM[r.state ?? "free"] ?? ROOM.free!;
                return <li key={r.roomId} style={{ display: "flex", justifyContent: "space-between" }}>{r.name}<Badge tone={st.tone}>{st.label}</Badge></li>;
              })}
            </ul>
          ) : <EmptyState title="Otaq yoxdur" level={3} />}
        </Card>
        <Card aria-labelledby="svc-h">
          <h2 id="svc-h">Ən çox görülən xidmətlər</h2>
          {s.topServices?.length ? (
            <table className="data-table">
              <thead><tr><th scope="col">Xidmət</th><th scope="col" className="num">Say</th><th scope="col" className="num">Gəlir</th></tr></thead>
              <tbody>{s.topServices.map((t) => <tr key={t.name}><td>{t.name}</td><td className="num">{t.count}</td><td className="num">{formatMoney(t.revenue ?? 0)}</td></tr>)}</tbody>
            </table>
          ) : <EmptyState title="Məlumat yoxdur" level={3} />}
        </Card>
      </div>
    </>
  );
}
