"use client";
import { unwrap } from "@dentacore/api-client";
import { Alert, Badge, Button, Card, Skeleton } from "@dentacore/ui";
import { useQuery } from "@tanstack/react-query";
import { useParams } from "next/navigation";
import { useEffect, useState } from "react";
import { useAuth } from "@/shared/AuthProvider";
import { can } from "@/shared/permissions";
import { useApi } from "@/shared/useApi";

const REVEAL_MS = 30_000;

export default function PatientPage() {
  const { id } = useParams<{ id: string }>();
  const client = useApi();
  const { user } = useAuth();
  const [reveal, setReveal] = useState(false);

  // Maska açıq göstərmə vaxtla məhdudlaşır: ekranda uzun qalmasın (hər açılış serverdə audit olunur)
  useEffect(() => {
    if (!reveal) return;
    const t = setTimeout(() => setReveal(false), REVEAL_MS);
    return () => clearTimeout(t);
  }, [reveal]);

  const patient = useQuery({
    queryKey: ["patients", id, reveal],
    queryFn: async () => unwrap(await client.GET("/patients/{patientId}", { params: { path: { patientId: id }, query: { reveal } } })),
  });
  const canClinical = can(user?.permissions, "clinical:read");
  const profile = useQuery({
    queryKey: ["patients", id, "medical-profile"],
    enabled: canClinical,
    queryFn: async () => unwrap(await client.GET("/patients/{patientId}/medical-profile", { params: { path: { patientId: id } } })),
  });

  if (patient.isPending) return <div role="status" aria-label="Yüklənir"><Skeleton height={160} /></div>;
  if (patient.isError) return <Alert tone="error" title="Pasiyent açılmadı">Qeyd tapılmadı və ya icazəniz yoxdur.</Alert>;
  const p = patient.data;
  const allergies = profile.data?.allergies ?? [];

  return (
    <>
      <h1>{p.lastName} {p.firstName} {p.fatherName ?? ""}</h1>
      {(p.hasSevereAllergy || allergies.some((a) => a.severity === "severe" || a.severity === "anaphylaxis")) && (
        <Alert tone="error" title="Ağır allergiya">
          {allergies.filter((a) => a.severity === "severe" || a.severity === "anaphylaxis").map((a) => a.substance).join(", ") || "Allergiya qeydlərinə baxın."}
        </Alert>
      )}
      <Card aria-labelledby="info-h">
        <h2 id="info-h">Məlumatlar <Badge tone={p.status === "active" ? "success" : "neutral"}>{p.status === "active" ? "Aktiv" : (p.status ?? "—")}</Badge></h2>
        <dl style={{ display: "grid", gridTemplateColumns: "max-content 1fr", gap: "8px 24px" }}>
          <dt>Kart №</dt><dd>{p.chartNo}</dd>
          <dt>Doğum tarixi</dt><dd>{p.birthDate ?? "—"}</dd>
          <dt>Telefon</dt><dd>{p.phone ?? "—"}</dd>
          <dt>E-poçt</dt><dd>{p.email ?? "—"}</dd>
          <dt>FİN</dt><dd>{p.nationalId ?? "—"}</dd>
          <dt>Gəlmədi</dt><dd>{p.noShowCount ?? 0}</dd>
        </dl>
        {can(user?.permissions, "patient:read_sensitive") && (
          <Button variant="outlined" onClick={() => setReveal((r) => !r)} aria-pressed={reveal}>
            {reveal ? "Maskala" : "Məlumatları göstər"}
          </Button>
        )}
        {reveal && <p className="kpi__label">Açıq məlumatlar 30 saniyədən sonra gizlənir. Baxış auditə yazılır.</p>}
      </Card>
      {canClinical && (
        <Card aria-labelledby="med-h">
          <h2 id="med-h">Tibbi profil</h2>
          {profile.isError ? <Alert tone="warning" title="Tibbi profil yüklənmədi" /> : (
            <ul>
              {allergies.length === 0 ? <li>Allergiya qeydi yoxdur</li> : allergies.map((a) => <li key={a.id ?? a.substance}>{a.substance}{a.reaction ? ` — ${a.reaction}` : ""} <Badge tone={a.severity === "mild" ? "neutral" : "warning"}>{a.severity ?? "—"}</Badge></li>)}
            </ul>
          )}
        </Card>
      )}
    </>
  );
}
