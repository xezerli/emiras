"use client";
import { ApiError, toApiError, unwrap } from "@dentacore/api-client";
import { Alert, Badge, Button, EmptyState, Skeleton, TextField } from "@dentacore/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { actionsFor, dayRange, STATUS_LABEL, timeLabel, todayLocal } from "@/features/calendar/day";
import { useAuth } from "@/shared/AuthProvider";
import { can } from "@/shared/permissions";
import { problemMessage, useApi } from "@/shared/useApi";

const LABEL = { "check-in": "Gəldi", "no-show": "Gəlmədi", cancel: "Ləğv et" } as const;
const ERRORS: Record<string, string> = {
  "appointment.too_early": "Check-in hələ tezdir.",
  "appointment.checkin_window": "Check-in vaxtı keçib və ya hələ başlamayıb.",
  "appointment.invalid_state": "Qəbulun vəziyyəti bu əməliyyata icazə vermir.",
};

export default function CalendarPage() {
  const client = useApi();
  const { user } = useAuth();
  const qc = useQueryClient();
  const [date, setDate] = useState(todayLocal);
  const [error, setError] = useState<string>();
  const canWrite = can(user?.permissions, "appointment:write");

  const list = useQuery({
    queryKey: ["appointments", date],
    queryFn: async () => {
      const { from, to } = dayRange(date);
      return unwrap(await client.GET("/appointments", { params: { query: { from, to } } }));
    },
  });

  const act = useMutation({
    mutationFn: async ({ id, action }: { id: string; action: "check-in" | "no-show" | "cancel" }) => {
      const params = { path: { appointmentId: id } };
      const res =
        action === "check-in" ? await client.POST("/appointments/{appointmentId}/check-in", { params })
        : action === "no-show" ? await client.POST("/appointments/{appointmentId}/no-show", { params })
        : await client.POST("/appointments/{appointmentId}/cancel", { params, body: {} });
      if (!res.response.ok) throw toApiError(res.error, res.response);
    },
    onSuccess: () => {
      setError(undefined);
      return qc.invalidateQueries({ queryKey: ["appointments"] });
    },
    onError: (e) => setError(problemMessage(e instanceof ApiError ? e.code : undefined, "Əməliyyat alınmadı.", ERRORS)),
  });

  const items = [...(list.data ?? [])].sort((a, b) => (a.start ?? "").localeCompare(b.start ?? ""));

  return (
    <>
      <h1>Təqvim</h1>
      <TextField label="Gün" type="date" value={date} onChange={(e) => e.target.value && setDate(e.target.value)} />
      {error && <Alert tone="error" title="Xəta">{error}</Alert>}
      {list.isPending && <div role="status" aria-label="Yüklənir"><Skeleton height={120} /></div>}
      {list.isError && <Alert tone="error" title="Təqvim yüklənmədi" />}
      {list.isSuccess && items.length === 0 && <EmptyState title="Bu gün qəbul yoxdur" />}
      {items.length > 0 && (
        <table className="data-table">
          <caption className="dc-sr-only">Günün qəbulları</caption>
          <thead><tr><th scope="col">Saat</th><th scope="col">Pasiyent</th><th scope="col">Səbəb</th><th scope="col">Vəziyyət</th>{canWrite && <th scope="col">Əməliyyat</th>}</tr></thead>
          <tbody>
            {items.map((a) => {
              const st = STATUS_LABEL[a.status ?? ""] ?? { label: a.status ?? "—", tone: "neutral" as const };
              return (
                <tr key={a.id}>
                  <td>{timeLabel(a.start ?? "")}–{timeLabel(a.end ?? "")}</td>
                  <td>{a.patientName ?? "—"}</td>
                  <td>{a.reason ?? "—"}</td>
                  <td><Badge tone={st.tone}>{st.label}</Badge></td>
                  {canWrite && (
                    <td style={{ display: "flex", gap: 8 }}>
                      {actionsFor(a.status ?? "").map((action) => (
                        <Button key={action} variant={action === "check-in" ? "tonal" : "text"} loading={act.isPending && act.variables?.id === a.id && act.variables.action === action}
                          onClick={() => act.mutate({ id: a.id ?? "", action })} aria-label={`${LABEL[action]}: ${a.patientName ?? ""}`}>
                          {LABEL[action]}
                        </Button>
                      ))}
                    </td>
                  )}
                </tr>
              );
            })}
          </tbody>
        </table>
      )}
    </>
  );
}
