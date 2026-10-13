"use client";
import { HubConnectionBuilder, HubConnectionState, LogLevel } from "@microsoft/signalr";
import { useQueryClient, type QueryClient } from "@tanstack/react-query";
import { useEffect } from "react";
import { tokenStore } from "./session";

/** Server bildirişi məzmun daşımır (ad/məbləğ yox) - yalnız "nə dəyişdi"; klient müvafiq sorğu açarlarını etibarsız edir. */
export interface Notification {
  event: string;
  entityId?: string | null;
  patientId?: string | null;
  branchId?: string | null;
}

export function queryKeysFor(event: string): string[][] {
  if (event.startsWith("scheduling.")) return [["dashboard"], ["appointments"]];
  if (event.startsWith("billing.")) return [["dashboard"], ["invoices"]];
  if (event.startsWith("clinical.")) return [["dashboard"], ["visits"]];
  return [];
}

export function invalidateFor(qc: QueryClient, n: Notification): void {
  for (const key of queryKeysFor(n.event)) void qc.invalidateQueries({ queryKey: key });
}

export function useRealtime(enabled: boolean): void {
  const qc = useQueryClient();
  useEffect(() => {
    if (!enabled) return;
    const conn = new HubConnectionBuilder()
      // Token hər (yenidən) qoşulmada təzə götürülür; ?access_token yalnız /hubs üçün server tərəfində qəbul olunur
      .withUrl("/hubs/notifications", { accessTokenFactory: () => tokenStore.get() ?? "" })
      .withAutomaticReconnect([0, 2000, 5000, 15000, 30000])
      .configureLogging(LogLevel.Warning)
      .build();
    conn.on("notification", (n: Notification) => invalidateFor(qc, n));
    // Qoşulma uğursuz olsa tətbiq işləməyə davam edir (sorğular polling/refetch ilə yenilənir)
    conn.start().catch(() => undefined);
    return () => {
      if (conn.state !== HubConnectionState.Disconnected) void conn.stop();
    };
  }, [enabled, qc]);
}
