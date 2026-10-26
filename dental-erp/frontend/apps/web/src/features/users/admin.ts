import type { components } from "@dentacore/api-client";

type RoleAssignment = components["schemas"]["RoleAssignment"];
type Role = components["schemas"]["Role"];
type Grant = components["schemas"]["RoleGrant"];
export type Draft = Record<string, { scope: Grant["scope"]; maxAmount: string }>;

/** Pul limiti yalnız geri qaytarma icazəsində mənalıdır (serverdə `role_permissions.max_amount`). */
export const HAS_LIMIT = "invoice:refund";

export const toDraft = (role: Role): Draft =>
  Object.fromEntries(role.permissions.map((g) => [g.permission, { scope: g.scope, maxAmount: g.maxAmount == null ? "" : String(g.maxAmount) }]));

export function toGrants(draft: Draft): Grant[] {
  return Object.entries(draft).map(([permission, v]) => ({
    permission,
    scope: v.scope,
    maxAmount: permission === HAS_LIMIT && v.maxAmount.trim() !== "" ? Number(v.maxAmount) : null,
  }));
}

export const USER_STATUS: Record<string, { label: string; tone: "success" | "warning" | "error" | "neutral" }> = {
  active: { label: "Aktiv", tone: "success" },
  locked: { label: "Bloklanıb", tone: "warning" },
  disabled: { label: "Söndürülüb", tone: "error" },
};

export const SCOPES = [
  { value: "own", label: "Yalnız özünün" },
  { value: "branch", label: "Filial" },
  { value: "tenant", label: "Bütün klinika" },
] as const;

/** Serverdəki qaydanın güzgüsü (≥10 simvol, hərf və rəqəm). Yalnız tez geri bildirim üçündür; qərar serverindir. */
export function passwordProblem(password: string): string | undefined {
  if (password.length < 10) return "Parol ən azı 10 simvol olmalıdır.";
  if (!/\p{L}/u.test(password) || !/\d/.test(password)) return "Parolda hərf və rəqəm olmalıdır.";
  return undefined;
}

/** Rolları seçilmiş koda görə yeniləyir: qalan rolların filialı saxlanır, yeni rollar bütün filiallara təyin olunur. */
export function nextAssignments(current: readonly RoleAssignment[], selected: readonly string[]): RoleAssignment[] {
  return selected.map((role) => current.find((a) => a.role === role) ?? { role, branchId: null });
}

export const ADMIN_ERRORS: Record<string, string> = {
  "user.email_exists": "Bu e-poçt ilə istifadəçi artıq var.",
  "user.self_change": "Öz hesabınızı bu yolla dəyişə bilməzsiniz.",
  "user.unknown_role": "Rol tapılmadı.",
  "user.unknown_branch": "Filial tapılmadı və ya aktiv deyil.",
  "user.not_found": "İstifadəçi tapılmadı.",
  "role.protected": "İdarəçi rolları dəyişdirilə bilməz.",
  "role.permission_not_assignable": "Bu icazə rola verilə bilməz.",
  "role.unknown_permission": "Naməlum icazə.",
  "validation.failed": "Daxil edilən məlumat düzgün deyil.",
};
