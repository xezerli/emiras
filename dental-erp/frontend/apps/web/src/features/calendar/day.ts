/** Klinikanın yerli günü [00:00, növbəti gün 00:00) ISO anlarına çevrilir (brauzerin vaxt qurşağı = klinika qurşağı). */
export function dayRange(date: string): { from: string; to: string } {
  const [y, m, d] = date.split("-").map(Number) as [number, number, number];
  const start = new Date(y, m - 1, d, 0, 0, 0, 0);
  const end = new Date(y, m - 1, d + 1, 0, 0, 0, 0);
  return { from: start.toISOString(), to: end.toISOString() };
}

export function todayLocal(now = new Date()): string {
  const mm = String(now.getMonth() + 1).padStart(2, "0");
  const dd = String(now.getDate()).padStart(2, "0");
  return `${now.getFullYear()}-${mm}-${dd}`;
}

export const STATUS_LABEL: Record<string, { label: string; tone: "neutral" | "success" | "warning" | "error" }> = {
  booked: { label: "Planlaşdırılıb", tone: "neutral" },
  confirmed: { label: "Təsdiqlənib", tone: "neutral" },
  checked_in: { label: "Gəlib", tone: "warning" },
  in_progress: { label: "Qəbulda", tone: "warning" },
  completed: { label: "Tamamlanıb", tone: "success" },
  cancelled: { label: "Ləğv", tone: "error" },
  no_show: { label: "Gəlmədi", tone: "error" },
  rescheduled: { label: "Təxirə salınıb", tone: "neutral" },
};

/** Statusa görə icazə verilən əməliyyatlar (son qərar serverdədir; bu yalnız lazımsız düymələri gizlədir). */
export function actionsFor(status: string): Array<"check-in" | "no-show" | "cancel"> {
  switch (status) {
    case "booked":
    case "confirmed":
      return ["check-in", "no-show", "cancel"];
    case "checked_in":
      return ["cancel"];
    default:
      return [];
  }
}

export const timeLabel = (iso: string) => new Intl.DateTimeFormat("az-AZ", { hour: "2-digit", minute: "2-digit" }).format(new Date(iso));
