"use client";
import { ApiError } from "@dentacore/api-client";
import { Alert, Button, Card, TextField } from "@dentacore/ui";
import { useRouter } from "next/navigation";
import { useState, type FormEvent } from "react";
import { login } from "@/shared/session";

const MESSAGES: Record<string, string> = {
  "auth.invalid_credentials": "E-poçt və ya parol yanlışdır.",
  "auth.account_locked": "Hesab müvəqqəti bloklanıb. Bir qədər sonra yenidən cəhd edin.",
  "auth.account_disabled": "Hesab deaktiv edilib. Klinika rəhbərliyi ilə əlaqə saxlayın.",
  "auth.ip_not_allowed": "Bu şəbəkədən giriş icazəli deyil.",
};

export function loginErrorMessage(e: unknown): string {
  if (e instanceof ApiError) {
    if (e.status === 429) return "Çox sayda cəhd. Bir az gözləyib yenidən cəhd edin.";
    return MESSAGES[e.code] ?? "Giriş mümkün olmadı. Bir qədər sonra yenidən cəhd edin.";
  }
  return "Serverə qoşulmaq mümkün olmadı.";
}

export default function LoginPage() {
  const router = useRouter();
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [challenge, setChallenge] = useState(false);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(undefined);
    try {
      const r = await login(email, password);
      if (r.kind === "challenge") setChallenge(true);
      else router.replace("/dashboard");
    } catch (err) {
      setError(loginErrorMessage(err));
    } finally {
      setBusy(false);
    }
  }

  return (
    <main className="login">
      <Card className="login__card" aria-labelledby="login-title">
        <h1 id="login-title">DentaCore</h1>
        {challenge ? (
          <Alert tone="info" title="İkimərhələli doğrulama">
            Hesabınız üçün ikinci mərhələ tələb olunur. Bu ekran növbəti buraxılışda əlavə olunacaq.
          </Alert>
        ) : (
          <form onSubmit={submit} noValidate style={{ display: "grid", gap: "var(--space-4)" }}>
            {error && <Alert tone="error" title="Daxil olmaq alınmadı">{error}</Alert>}
            <TextField label="E-poçt" type="email" autoComplete="username" required value={email} onChange={(e) => setEmail(e.target.value)} />
            <TextField label="Parol" type="password" autoComplete="current-password" required value={password} onChange={(e) => setPassword(e.target.value)} />
            <Button type="submit" loading={busy} disabled={!email || !password}>Daxil ol</Button>
          </form>
        )}
      </Card>
    </main>
  );
}
