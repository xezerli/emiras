/** Allergiya ağırlığı: server enum-u → istifadəçiyə göstərilən ad və ton (xam ingiliscə dəyər göstərilmir). */
export const SEVERITY: Record<string, { label: string; tone: "neutral" | "warning" | "error" }> = {
  mild: { label: "Yüngül", tone: "neutral" },
  moderate: { label: "Orta", tone: "warning" },
  severe: { label: "Ağır", tone: "error" },
  anaphylaxis: { label: "Anafilaksiya", tone: "error" },
};
