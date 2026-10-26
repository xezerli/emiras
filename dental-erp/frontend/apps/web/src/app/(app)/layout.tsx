"use client";
import { Skeleton, ThemeToggle, Button } from "@dentacore/ui";
import Link from "next/link";
import { usePathname } from "next/navigation";
import type { ReactNode } from "react";
import { AuthProvider, useAuth } from "@/shared/AuthProvider";
import { visibleNav } from "@/shared/permissions";
import { useRealtime } from "@/shared/realtime";

function Shell({ children }: { children: ReactNode }) {
  const { status, user, signOut } = useAuth();
  const path = usePathname();
  useRealtime(status === "authenticated");

  if (status === "loading") {
    return (
      <div role="status" aria-label="Yüklənir" style={{ padding: 32, display: "grid", gap: 12 }}>
        <Skeleton width="30%" height={28} />
        <Skeleton height={120} />
      </div>
    );
  }

  return (
    <div className="shell">
      <a className="skip-link" href="#main">Əsas məzmuna keç</a>
      <nav className="shell__nav" aria-label="Əsas naviqasiya">
        <div className="shell__brand">DentaCore</div>
        {visibleNav(user?.permissions).map((n) => (
          <Link key={n.href} href={n.href} className="shell__link" aria-current={path.startsWith(n.href) ? "page" : undefined}>
            {n.label}
          </Link>
        ))}
      </nav>
      <div className="shell__main">
        <header className="shell__top">
          <span>{user?.fullName}</span>
          <span style={{ display: "flex", gap: 8 }}>
            <ThemeToggle />
            <Button variant="outlined" onClick={() => void signOut()}>Çıxış</Button>
          </span>
        </header>
        <main id="main" className="shell__content" tabIndex={-1}>{children}</main>
      </div>
    </div>
  );
}

export default function AppLayout({ children }: { children: ReactNode }) {
  return (
    <AuthProvider>
      <Shell>{children}</Shell>
    </AuthProvider>
  );
}
