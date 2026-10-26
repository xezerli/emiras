"use client";
import { toApiError, unwrap, type components } from "@dentacore/api-client";
import { Alert, Badge, Button, Card, EmptyState, Skeleton, TextField } from "@dentacore/ui";
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState, type FormEvent } from "react";
import { ADMIN_ERRORS, USER_STATUS, nextAssignments, passwordProblem } from "@/features/users/admin";
import { useAuth } from "@/shared/AuthProvider";
import { formatDateTime } from "@/shared/format";
import { problemMessage, useApi } from "@/shared/useApi";

type User = components["schemas"]["User"];
type Role = components["schemas"]["Role"];
type Branch = components["schemas"]["Branch"];

function CreateUser({ roles, branches, onDone }: { roles: Role[]; branches: Branch[]; onDone: () => void }) {
  const client = useApi();
  const [f, setF] = useState({ email: "", fullName: "", password: "", role: "", branch: "" });
  const [error, setError] = useState<string>();
  const set = (k: keyof typeof f) => (e: { target: { value: string } }) => setF((s) => ({ ...s, [k]: e.target.value }));
  const passwordError = f.password ? passwordProblem(f.password) : undefined;
  const create = useMutation({
    mutationFn: async () => {
      const res = await client.POST("/users", {
        body: { email: f.email.trim(), fullName: f.fullName.trim(), password: f.password, roles: [{ role: f.role, branchId: f.branch || null }] },
      });
      if (!res.response.ok) throw toApiError(res.error, res.response);
    },
    onSuccess: onDone,
    onError: (e: { code?: string }) => setError(problemMessage(e.code, "İstifadəçi yaradılmadı.", ADMIN_ERRORS)),
  });
  const valid = f.email.includes("@") && f.fullName.trim() && f.role && f.password && !passwordProblem(f.password);

  return (
    <Card aria-labelledby="new-user-h">
      <h2 id="new-user-h">Yeni istifadəçi</h2>
      <form
        onSubmit={(e: FormEvent) => { e.preventDefault(); setError(undefined); create.mutate(); }}
        style={{ display: "grid", gap: 16, maxWidth: 520 }}
      >
        {error && <Alert tone="error" title="Xəta">{error}</Alert>}
        <TextField label="E-poçt" type="email" required value={f.email} onChange={set("email")} autoComplete="off" />
        <TextField label="Ad Soyad" required value={f.fullName} onChange={set("fullName")} autoComplete="off" />
        <TextField
          label="İlkin parol" type="password" required value={f.password} onChange={set("password")} autoComplete="new-password"
          hint="Ən azı 10 simvol, hərf və rəqəm. Parolu istifadəçiyə təhlükəsiz yolla çatdırın."
          error={passwordError}
        />
        <div>
          <label htmlFor="new-role">Rol</label>{" "}
          <select id="new-role" value={f.role} onChange={set("role")} required>
            <option value="">Seçin</option>
            {roles.map((r) => <option key={r.code} value={r.code}>{r.name}</option>)}
          </select>
        </div>
        <div>
          <label htmlFor="new-branch">Filial</label>{" "}
          <select id="new-branch" value={f.branch} onChange={set("branch")}>
            <option value="">Bütün filiallar</option>
            {branches.map((b) => <option key={b.id} value={b.id}>{b.name}</option>)}
          </select>
        </div>
        <Button type="submit" loading={create.isPending} disabled={!valid}>Yarat</Button>
      </form>
    </Card>
  );
}

function RolesEditor({ target, roles, onClose }: { target: User; roles: Role[]; onClose: () => void }) {
  const client = useApi();
  const qc = useQueryClient();
  const [selected, setSelected] = useState<string[]>(target.roles.map((r) => r.role));
  const [error, setError] = useState<string>();
  const save = useMutation({
    mutationFn: async () => {
      const res = await client.PUT("/users/{userId}/roles", {
        params: { path: { userId: target.id } },
        body: { roles: nextAssignments(target.roles, selected) },
      });
      if (!res.response.ok) throw toApiError(res.error, res.response);
    },
    onSuccess: async () => { await qc.invalidateQueries({ queryKey: ["users"] }); onClose(); },
    onError: (e: { code?: string }) => setError(problemMessage(e.code, "Rollar dəyişmədi.", ADMIN_ERRORS)),
  });
  const toggle = (code: string) => setSelected((s) => (s.includes(code) ? s.filter((c) => c !== code) : [...s, code]));

  return (
    <Card aria-labelledby="roles-edit-h">
      <h2 id="roles-edit-h">Rollar: {target.fullName}</h2>
      {error && <Alert tone="error" title="Xəta">{error}</Alert>}
      <fieldset style={{ border: 0, padding: 0, display: "grid", gap: 8 }}>
        <legend className="dc-sr-only">Rollar</legend>
        {roles.map((r) => (
          <label key={r.code} style={{ display: "flex", gap: 8, alignItems: "center" }}>
            <input type="checkbox" checked={selected.includes(r.code)} onChange={() => toggle(r.code)} />
            {r.name}
          </label>
        ))}
      </fieldset>
      <p>Saxlayanda istifadəçinin sessiyaları bağlanır; yeni icazələr növbəti girişdən sonra qüvvəyə minir.</p>
      <div style={{ display: "flex", gap: 8 }}>
        <Button loading={save.isPending} disabled={selected.length === 0} onClick={() => save.mutate()}>Saxla</Button>
        <Button variant="outlined" onClick={onClose}>Ləğv et</Button>
      </div>
    </Card>
  );
}

function ResetPassword({ target, onClose }: { target: User; onClose: () => void }) {
  const client = useApi();
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string>();
  const reset = useMutation({
    mutationFn: async () => {
      const res = await client.POST("/users/{userId}/reset-password", { params: { path: { userId: target.id } }, body: { newPassword: password } });
      if (!res.response.ok) throw toApiError(res.error, res.response);
    },
    onSuccess: onClose,
    onError: (e: { code?: string }) => setError(problemMessage(e.code, "Parol dəyişmədi.", ADMIN_ERRORS)),
  });
  const problem = password ? passwordProblem(password) : undefined;

  return (
    <Card aria-labelledby="reset-h">
      <h2 id="reset-h">Parolu sıfırla: {target.fullName}</h2>
      {error && <Alert tone="error" title="Xəta">{error}</Alert>}
      <div style={{ display: "grid", gap: 16, maxWidth: 520 }}>
        <TextField label="Yeni parol" type="password" value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="new-password" error={problem} hint="Ən azı 10 simvol, hərf və rəqəm. İstifadəçinin bütün sessiyaları bağlanır." />
        <div style={{ display: "flex", gap: 8 }}>
          <Button loading={reset.isPending} disabled={!password || !!problem} onClick={() => reset.mutate()}>Sıfırla</Button>
          <Button variant="outlined" onClick={onClose}>Ləğv et</Button>
        </div>
      </div>
    </Card>
  );
}

export default function UsersPage() {
  const client = useApi();
  const { user: me } = useAuth();
  const qc = useQueryClient();
  const [creating, setCreating] = useState(false);
  const [editing, setEditing] = useState<{ kind: "roles" | "password"; user: User }>();
  const [notice, setNotice] = useState<string>();
  const [error, setError] = useState<string>();

  const roles = useQuery({ queryKey: ["roles"], queryFn: async () => unwrap(await client.GET("/roles")) });
  const branches = useQuery({ queryKey: ["branches"], queryFn: async () => unwrap(await client.GET("/branches")) });
  const users = useInfiniteQuery({
    queryKey: ["users"],
    initialPageParam: undefined as string | undefined,
    queryFn: async ({ pageParam }) => unwrap(await client.GET("/users", { params: { query: { limit: 50, cursor: pageParam } } })),
    getNextPageParam: (last) => last.nextCursor ?? undefined,
  });
  const toggle = useMutation({
    mutationFn: async (u: User) => {
      const res = await client.POST(u.status === "disabled" || u.status === "locked" ? "/users/{userId}/enable" : "/users/{userId}/disable", { params: { path: { userId: u.id } } });
      if (!res.response.ok) throw toApiError(res.error, res.response);
    },
    onSuccess: () => qc.invalidateQueries({ queryKey: ["users"] }),
    onError: (e: { code?: string }) => setError(problemMessage(e.code, "Əməliyyat alınmadı.", ADMIN_ERRORS)),
  });

  const items = users.data?.pages.flatMap((p) => p.items) ?? [];
  const roleList = roles.data?.roles ?? [];
  const roleName = (code: string) => roleList.find((r) => r.code === code)?.name ?? code;
  const branchName = (id: string) => branches.data?.find((b) => b.id === id)?.name ?? "filial";
  // Filial məhdudiyyəti olan rol "Rol (Filial)" kimi göstərilir; məhdudiyyətsiz rol bütün filialları əhatə edir
  const roleLabel = (r: { role: string; branchId?: string | null }) => (r.branchId ? `${roleName(r.role)} (${branchName(r.branchId)})` : roleName(r.role));

  return (
    <>
      <h1>İstifadəçilər</h1>
      {notice && <Alert tone="success" title={notice} />}
      {error && <Alert tone="error" title="Xəta">{error}</Alert>}
      {!creating && roleList.length > 0 && <div><Button onClick={() => { setNotice(undefined); setCreating(true); }}>Yeni istifadəçi</Button></div>}
      {creating && (
        <CreateUser
          roles={roleList}
          branches={branches.data ?? []}
          onDone={() => { setCreating(false); setNotice("İstifadəçi yaradıldı."); void qc.invalidateQueries({ queryKey: ["users"] }); }}
        />
      )}
      {editing?.kind === "roles" && <RolesEditor target={editing.user} roles={roleList} onClose={() => setEditing(undefined)} />}
      {editing?.kind === "password" && <ResetPassword target={editing.user} onClose={() => { setEditing(undefined); setNotice("Parol dəyişdi, sessiyalar bağlandı."); }} />}

      {users.isPending && <div role="status" aria-label="Yüklənir"><Skeleton height={120} /></div>}
      {users.isError && <Alert tone="error" title="İstifadəçilər yüklənmədi" />}
      {users.isSuccess && items.length === 0 && <EmptyState title="İstifadəçi yoxdur" />}
      {items.length > 0 && (
        <table className="data-table">
          <caption className="dc-sr-only">İstifadəçilər</caption>
          <thead>
            <tr><th scope="col">Ad</th><th scope="col">E-poçt</th><th scope="col">Rollar</th><th scope="col">Status</th><th scope="col">Son giriş</th><th scope="col">Əməliyyatlar</th></tr>
          </thead>
          <tbody>
            {items.map((u) => {
              const st = USER_STATUS[u.status] ?? { label: u.status, tone: "neutral" as const };
              const self = u.id === me?.id;
              return (
                <tr key={u.id}>
                  <th scope="row">{u.fullName}</th>
                  <td>{u.email}</td>
                  <td>{u.roles.map(roleLabel).join(", ")}</td>
                  <td><Badge tone={st.tone}>{st.label}</Badge></td>
                  <td>{u.lastLoginAt ? formatDateTime(u.lastLoginAt) : "—"}</td>
                  <td>
                    <span style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
                      <Button variant="outlined" disabled={self} aria-label={`Rolları dəyiş: ${u.fullName}`} onClick={() => setEditing({ kind: "roles", user: u })}>Rollar</Button>
                      <Button variant="outlined" aria-label={`Parolu sıfırla: ${u.fullName}`} onClick={() => setEditing({ kind: "password", user: u })}>Parol</Button>
                      <Button
                        variant="outlined"
                        disabled={self && u.status === "active"}
                        loading={toggle.isPending && toggle.variables?.id === u.id}
                        aria-label={`${u.status === "active" ? "Söndür" : "Aç"}: ${u.fullName}`}
                        onClick={() => { setError(undefined); toggle.mutate(u); }}
                      >
                        {u.status === "active" ? "Söndür" : "Aç"}
                      </Button>
                    </span>
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      )}
      {users.hasNextPage && <div><Button variant="outlined" loading={users.isFetchingNextPage} onClick={() => void users.fetchNextPage()}>Daha çox</Button></div>}
    </>
  );
}
