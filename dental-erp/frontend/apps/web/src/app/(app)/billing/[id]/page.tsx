"use client";
import { ApiError, unwrap } from "@dentacore/api-client";
import { Alert, Badge, Button, Card, SelectField, Skeleton, TextField } from "@dentacore/ui";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useParams } from "next/navigation";
import { useRef, useState, type FormEvent } from "react";
import { METHODS, PAY_ERRORS, PaymentAttempt, parseAmount, recordPayment, type PayMethod } from "@/features/billing/pay";
import { INVOICE_STATUS, PAYABLE } from "@/features/billing/status";
import { useAuth } from "@/shared/AuthProvider";
import { formatMoney } from "@/shared/format";
import { can } from "@/shared/permissions";
import { problemMessage, useApi } from "@/shared/useApi";

export default function InvoicePage() {
  const { id } = useParams<{ id: string }>();
  const client = useApi();
  const { user } = useAuth();
  const qc = useQueryClient();
  const attempt = useRef(new PaymentAttempt());
  const [method, setMethod] = useState<PayMethod>("cash");
  const [amount, setAmount] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [done, setDone] = useState(false);

  const inv = useQuery({
    queryKey: ["invoices", id],
    queryFn: async () => unwrap(await client.GET("/invoices/{invoiceId}", { params: { path: { invoiceId: id } } })),
  });

  if (inv.isPending) return <div role="status" aria-label="Yüklənir"><Skeleton height={160} /></div>;
  if (inv.isError) return <Alert tone="error" title="Faktura açılmadı" />;
  const i = inv.data;
  const st = INVOICE_STATUS[i.status ?? ""] ?? { label: i.status ?? "—", tone: "neutral" as const };
  const balance = i.balance ?? 0;
  const canPay = can(user?.permissions, "payment:write") && PAYABLE.has(i.status ?? "");
  const parsed = parseAmount(amount || String(balance));

  async function pay(e: FormEvent) {
    e.preventDefault();
    if (parsed === null) return setError("Məbləğ düzgün deyil.");
    if (parsed > balance) return setError(PAY_ERRORS["billing.overpayment"]);
    setBusy(true);
    setError(undefined);
    setDone(false);
    try {
      await recordPayment(client, id, method, parsed, attempt.current.idempotencyKey);
      attempt.current.reset();   // yalnız uğurdan sonra yeni açar
      setAmount("");
      setDone(true);
      await Promise.all([qc.invalidateQueries({ queryKey: ["invoices"] }), qc.invalidateQueries({ queryKey: ["dashboard"] })]);
    } catch (err) {
      setError(problemMessage(err instanceof ApiError ? err.code : undefined, "Ödəniş qeydə alınmadı. Eyni əməliyyatı təkrar göndərmək təhlükəsizdir.", PAY_ERRORS));
    } finally {
      setBusy(false);
    }
  }

  return (
    <>
      <h1>Faktura {i.number} <Badge tone={st.tone}>{st.label}</Badge></h1>
      <Card aria-labelledby="items-h">
        <h2 id="items-h">Xidmətlər</h2>
        <table className="data-table">
          <caption className="dc-sr-only">Faktura sətirləri</caption>
          <thead><tr><th scope="col">Təsvir</th><th scope="col" className="num">Say</th><th scope="col" className="num">Məbləğ</th></tr></thead>
          <tbody>{(i.items ?? []).map((x) => <tr key={x.id}><td>{x.description}</td><td className="num">{x.quantity}</td><td className="num">{formatMoney(x.lineTotal ?? 0)}</td></tr>)}</tbody>
          <tfoot>
            <tr><th scope="row" colSpan={2}>Cəmi</th><td className="num">{formatMoney(i.total ?? 0)}</td></tr>
            <tr><th scope="row" colSpan={2}>Ödənilib</th><td className="num">{formatMoney(i.paidTotal ?? 0)}</td></tr>
            <tr><th scope="row" colSpan={2}>Qalıq</th><td className="num"><strong>{formatMoney(balance)}</strong></td></tr>
          </tfoot>
        </table>
      </Card>
      {(i.payments?.length ?? 0) > 0 && (
        <Card aria-labelledby="pay-h">
          <h2 id="pay-h">Ödənişlər</h2>
          <ul>{i.payments!.map((p) => <li key={p.id}>{p.kind === "refund" ? "Qaytarma" : "Ödəniş"}: {formatMoney(p.amount ?? 0)} ({p.method})</li>)}</ul>
        </Card>
      )}
      {canPay && (
        <Card aria-labelledby="new-h">
          <h2 id="new-h">Ödəniş qəbul et</h2>
          <form onSubmit={pay} style={{ display: "grid", gap: 16, maxWidth: 360 }}>
            {error && <Alert tone="error" title="Ödəniş alınmadı">{error}</Alert>}
            {done && <Alert tone="success" title="Ödəniş qeydə alındı" />}
            <SelectField label="Üsul" value={method} onChange={(e) => setMethod(e.target.value as PayMethod)}>
              {METHODS.map((m) => <option key={m.value} value={m.value}>{m.label}</option>)}
            </SelectField>
            <TextField label="Məbləğ (AZN)" inputMode="decimal" value={amount} placeholder={String(balance)} onChange={(e) => setAmount(e.target.value)} />
            <Button type="submit" loading={busy} disabled={parsed === null}>Qəbul et</Button>
          </form>
        </Card>
      )}
    </>
  );
}
