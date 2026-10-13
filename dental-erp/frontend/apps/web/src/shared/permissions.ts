export type Scope = "own" | "branch" | "tenant";
export interface Permission {
  code?: string;
  scope?: Scope;
  maxAmount?: number | null;
}

/** Nav və düymələrin görünməsi üçündür; real icazə yoxlaması həmişə serverdədir (403). */
export function can(permissions: readonly Permission[] | undefined, code: string): boolean {
  return permissions?.some((p) => p.code === code) ?? false;
}

export interface NavItem {
  href: string;
  label: string;
  permission: string;
}

export const NAV: readonly NavItem[] = [
  { href: "/dashboard", label: "İdarə paneli", permission: "report:read" },
  { href: "/patients", label: "Pasiyentlər", permission: "patient:read" },
  { href: "/calendar", label: "Təqvim", permission: "appointment:read" },
  { href: "/billing", label: "Maliyyə", permission: "invoice:read" },
];

export function visibleNav(permissions: readonly Permission[] | undefined): NavItem[] {
  return NAV.filter((n) => can(permissions, n.permission));
}
