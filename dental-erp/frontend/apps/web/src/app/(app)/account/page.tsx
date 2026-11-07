"use client";
import { toApiError } from "@dentacore/api-client";
import { Alert, Button, Card, TextField } from "@dentacore/ui";
import { useMutation } from "@tanstack/react-query";
import { useState, type FormEvent } from "react";
import { ACCOUNT_ERRORS, newPasswordProblem } from "@/features/users/admin";
import { useAuth } from "@/shared/AuthProvider";
import { problemMessage, useApi } from "@/shared/useApi";

export default function AccountPage() {
  const client = useApi();
  const { user, signOut } = useAuth();
  const [f, setF] = useState({ current: "", next: "", repeat: "" });
  const [error, setError] = useState<string>();
  const set = (k: keyof typeof f) => (e: { target: { value: string } }) => setF((s) => ({ ...s, [k]: e.target.value }));
  const problem = f.next ? newPasswordProblem(f.next, f.current) : undefined;
  const mismatch = f.repeat !== "" && f.repeat !== f.next ? "Parollar eyni deyil." : undefined;

  const change = useMutation({
    mutationFn: async () => {
      const res = await client.POST("/auth/change-password", { body: { currentPassword: f.current, newPassword: f.next } });
      if (!res.response.ok) throw toApiError(res.error, res.response);
    },
    onError: (e: { code?: string }) => setError(problemMessage(e.code, "Parol dəyişmədi.", ACCOUNT_ERRORS)),
  });
  const valid = f.current && f.next && !problem && f.next === f.repeat;

  return (
    <>
      <h1>Hesabım</h1>
      <Card aria-labelledby="pw-h">
        <h2 id="pw-h">Parolu dəyiş</h2>
        <p>{user?.fullName}</p>
        {change.isSuccess ? (
          <Alert tone="success" title="Parol dəyişdi">
            Təhlükəsizlik üçün bütün sessiyalar bağlandı. Yeni parolla yenidən daxil olun.
            <div style={{ marginTop: 8 }}><Button onClick={() => void signOut()}>Daxil ol</Button></div>
          </Alert>
        ) : (
          <form onSubmit={(e: FormEvent) => { e.preventDefault(); setError(undefined); change.mutate(); }} style={{ display: "grid", gap: 16, maxWidth: 520 }}>
            {error && <Alert tone="error" title="Xəta">{error}</Alert>}
            <TextField label="Cari parol" type="password" required value={f.current} onChange={set("current")} autoComplete="current-password" />
            <TextField label="Yeni parol" type="password" required value={f.next} onChange={set("next")} autoComplete="new-password" hint="Ən azı 10 simvol, hərf və rəqəm." error={problem} />
            <TextField label="Yeni parolu təkrar edin" type="password" required value={f.repeat} onChange={set("repeat")} autoComplete="new-password" error={mismatch} />
            <Button type="submit" loading={change.isPending} disabled={!valid}>Parolu dəyiş</Button>
          </form>
        )}
      </Card>
    </>
  );
}
