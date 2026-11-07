"use client";
import { toApiError, unwrap, type components } from "@dentacore/api-client";
import { Alert, Badge, Button, Card, SelectField, Skeleton } from "@dentacore/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { ADMIN_ERRORS, HAS_LIMIT, SCOPES, toDraft, toGrants } from "@/features/users/admin";
import { problemMessage, useApi } from "@/shared/useApi";

type Role = components["schemas"]["Role"];
type Grant = components["schemas"]["RoleGrant"];
type Info = { code: string; description?: string | null };

/** `key` rolun saxlanmış vəziyyətinə bağlıdır: server dəyişəndə redaktor təzə qaralama ilə başlayır. */
function RoleEditor({ role, permissions, onSaved }: { role: Role; permissions: Info[]; onSaved: () => void }) {
  const client = useApi();
  const qc = useQueryClient();
  const [draft, setDraft] = useState(() => toDraft(role));
  const [error, setError] = useState<string>();
  const editable = role.editable;

  const save = useMutation({
    mutationFn: async () => {
      const res = await client.PUT("/roles/{roleCode}/permissions", { params: { path: { roleCode: role.code } }, body: { permissions: toGrants(draft) } });
      if (!res.response.ok) throw toApiError(res.error, res.response);
    },
    onSuccess: async () => {
      onSaved();
      await qc.invalidateQueries({ queryKey: ["roles"] });
    },
    onError: (e: { code?: string }) => setError(problemMessage(e.code, "İcazələr saxlanmadı.", ADMIN_ERRORS)),
  });

  const badLimit = Object.entries(draft).some(([p, v]) => p === HAS_LIMIT && v.maxAmount.trim() !== "" && !(Number(v.maxAmount) >= 0));
  const granted = (code: string) => (editable ? code in draft : role.permissions.some((g) => g.permission === code));
  const scopeOf = (code: string): Grant["scope"] | undefined => (editable ? draft[code]?.scope : role.permissions.find((g) => g.permission === code)?.scope);

  return (
    <>
      {!editable && <Alert tone="info" title="Bu rol dəyişdirilə bilməz">İdarəçi rolları həmişə tam icazəlidir ki, sistemə girişi itirmək mümkün olmasın.</Alert>}
      {error && <Alert tone="error" title="Xəta">{error}</Alert>}
      {editable && role.permissions.length === 0 && Object.keys(draft).length === 0 && <div><Badge tone="warning">Bu rolun heç bir icazəsi yoxdur</Badge></div>}
      <table className="data-table">
        <caption className="dc-sr-only">Rolun icazələri</caption>
        <thead><tr><th scope="col">İcazə</th><th scope="col">Verilib</th><th scope="col">Əhatə</th><th scope="col">Limit</th></tr></thead>
        <tbody>
          {permissions.map((p) => {
            const d = draft[p.code];
            const scope = scopeOf(p.code);
            return (
              <tr key={p.code}>
                <th scope="row">{p.description ?? p.code}<div style={{ opacity: 0.7, fontWeight: 400 }}>{p.code}</div></th>
                <td>
                  <input
                    type="checkbox" aria-label={`${p.code} icazəsi`} checked={granted(p.code)} disabled={!editable}
                    onChange={(e) => setDraft((s) => {
                      const next = { ...s };
                      if (e.target.checked) next[p.code] = { scope: "branch", maxAmount: "" }; else delete next[p.code];
                      return next;
                    })}
                  />
                </td>
                <td>
                  {scope && (
                    <select
                      aria-label={`${p.code} əhatəsi`} value={scope} disabled={!editable}
                      onChange={(e) => d && setDraft((s) => ({ ...s, [p.code]: { ...d, scope: e.target.value as Grant["scope"] } }))}
                    >
                      {SCOPES.map((s) => <option key={s.value} value={s.value}>{s.label}</option>)}
                    </select>
                  )}
                </td>
                <td>
                  {d && p.code === HAS_LIMIT && (
                    <input
                      inputMode="decimal" aria-label="Geri qaytarma limiti" placeholder="Limitsiz" value={d.maxAmount}
                      onChange={(e) => setDraft((s) => ({ ...s, [p.code]: { ...d, maxAmount: e.target.value } }))}
                      style={{ width: 96 }}
                    />
                  )}
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
      {editable && <div><Button loading={save.isPending} disabled={badLimit} onClick={() => { setError(undefined); save.mutate(); }}>Saxla</Button></div>}
    </>
  );
}

export default function RolesPage() {
  const client = useApi();
  const [selected, setSelected] = useState("");
  // Saxlayandan sonra redaktor təzə server vəziyyəti ilə yenidən qurulur, ona görə bildiriş burada saxlanır
  const [saved, setSaved] = useState(false);
  const data = useQuery({ queryKey: ["roles"], queryFn: async () => unwrap(await client.GET("/roles")) });
  const roles = data.data?.roles ?? [];
  const role = roles.find((r) => r.code === selected) ?? roles[0];
  // user:manage rola verilə bilməz, ona görə siyahıda göstərilmir
  const permissions = (data.data?.permissions ?? []).filter((p) => p.code !== "user:manage");

  return (
    <>
      <h1>Rollar və icazələr</h1>
      {data.isPending && <div role="status" aria-label="Yüklənir"><Skeleton height={120} /></div>}
      {data.isError && <Alert tone="error" title="Rollar yüklənmədi" />}
      {role && (
        <Card aria-labelledby="role-h">
          <h2 id="role-h">İcazələr</h2>
          <div style={{ display: "grid", gap: 16, maxWidth: 640 }}>
            <SelectField label="Rol" value={role.code} onChange={(e) => { setSaved(false); setSelected(e.target.value); }}>
              {roles.map((r) => <option key={r.code} value={r.code}>{r.name}</option>)}
            </SelectField>
            {saved && <Alert tone="success" title="İcazələr saxlandı. Mövcud girişlər ən çox 10 dəqiqə köhnə icazə ilə qala bilər." />}
            <RoleEditor key={`${role.code}:${JSON.stringify(role.permissions)}`} role={role} permissions={permissions} onSaved={() => setSaved(true)} />
          </div>
        </Card>
      )}
    </>
  );
}
