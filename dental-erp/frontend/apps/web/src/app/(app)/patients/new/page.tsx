"use client";
import { newIdempotencyKey } from "@dentacore/api-client";
import { Alert, Button, Card, SelectField, TextField } from "@dentacore/ui";
import { useQueryClient } from "@tanstack/react-query";
import { useRouter } from "next/navigation";
import { useRef, useState, type FormEvent } from "react";
import { registerPatient } from "@/features/patients/register";
import { useAuth } from "@/shared/AuthProvider";
import { useApi } from "@/shared/useApi";

export default function NewPatientPage() {
  const client = useApi();
  const { user } = useAuth();
  const router = useRouter();
  const qc = useQueryClient();
  const key = useRef(newIdempotencyKey());
  const [f, setF] = useState({ firstName: "", lastName: "", fatherName: "", birthDate: "", gender: "", phone: "" });
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [duplicate, setDuplicate] = useState(false);
  const set = (k: keyof typeof f) => (e: { target: { value: string } }) => setF((s) => ({ ...s, [k]: e.target.value }));
  const branchId = user?.branches?.[0];

  async function submit(confirm: boolean) {
    if (!branchId) return setError("Sizə filial təyin olunmayıb.");
    setBusy(true);
    setError(undefined);
    try {
      const out = await registerPatient(
        client,
        {
          branchId, firstName: f.firstName.trim(), lastName: f.lastName.trim(),
          fatherName: f.fatherName.trim() || undefined, birthDate: f.birthDate || undefined,
          gender: (f.gender || undefined) as "M" | "F" | "O" | undefined, phone: f.phone.replace(/[\s()-]/g, "") || undefined,
        },
        key.current,
        confirm,
      );
      if (out.kind === "created") {
        await qc.invalidateQueries({ queryKey: ["patients"] });
        router.push(`/patients/${out.id}`);
      } else if (out.kind === "duplicate") setDuplicate(true);
      else setError(out.message);
    } catch {
      setError("Pasiyent yaradılmadı. Bir qədər sonra yenidən cəhd edin.");
    } finally {
      setBusy(false);
    }
  }

  const onSubmit = (e: FormEvent) => {
    e.preventDefault();
    setDuplicate(false);
    void submit(false);
  };

  return (
    <>
      <h1>Yeni pasiyent</h1>
      <Card>
        <form onSubmit={onSubmit} style={{ display: "grid", gap: 16, maxWidth: 520 }}>
          {error && <Alert tone="error" title="Xəta">{error}</Alert>}
          {duplicate && (
            <Alert tone="warning" title="Oxşar pasiyent mövcuddur">
              Bu telefon və ya FİN ilə pasiyent artıq var. Ailə üzvüdürsə yenə də yarada bilərsiniz.
              <div style={{ marginTop: 8 }}><Button variant="tonal" loading={busy} onClick={() => void submit(true)}>Yenə də yarat</Button></div>
            </Alert>
          )}
          <TextField label="Ad" required value={f.firstName} onChange={set("firstName")} autoComplete="off" />
          <TextField label="Soyad" required value={f.lastName} onChange={set("lastName")} autoComplete="off" />
          <TextField label="Ata adı" value={f.fatherName} onChange={set("fatherName")} autoComplete="off" />
          <TextField label="Doğum tarixi" type="date" value={f.birthDate} onChange={set("birthDate")} />
          <SelectField label="Cins" value={f.gender} onChange={set("gender")}>
            <option value="">—</option><option value="M">Kişi</option><option value="F">Qadın</option><option value="O">Digər</option>
          </SelectField>
          <TextField label="Telefon" type="tel" hint="Məsələn +994501234567" value={f.phone} onChange={set("phone")} autoComplete="off" />
          <Button type="submit" loading={busy} disabled={!f.firstName.trim() || !f.lastName.trim()}>Saxla</Button>
        </form>
      </Card>
    </>
  );
}
