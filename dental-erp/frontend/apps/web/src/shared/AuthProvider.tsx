"use client";
import { unwrap, type components } from "@dentacore/api-client";
import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { useRouter } from "next/navigation";
import { api, logout as doLogout, refreshSession } from "./session";

export type CurrentUser = components["schemas"]["CurrentUser"];

interface AuthState {
  status: "loading" | "authenticated";
  user?: CurrentUser;
  signOut: () => Promise<void>;
}

const Ctx = createContext<AuthState | null>(null);

export function useAuth(): AuthState {
  const v = useContext(Ctx);
  if (!v) throw new Error("useAuth must be used inside <AuthProvider>");
  return v;
}

/** Səhifə yüklənəndə cookie ilə sessiyanı bərpa edir; olmasa /login-ə yönləndirir. */
export function AuthProvider({ children }: { children: ReactNode }) {
  const router = useRouter();
  const [user, setUser] = useState<CurrentUser>();

  const toLogin = useCallback(() => router.replace("/login"), [router]);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      const token = await refreshSession();
      if (!token) return toLogin();
      try {
        const me = unwrap(await api(toLogin).GET("/auth/me"));
        if (!cancelled) setUser(me);
      } catch {
        if (!cancelled) toLogin();
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [toLogin]);

  const signOut = useCallback(async () => {
    await doLogout();
    toLogin();
  }, [toLogin]);

  const value = useMemo<AuthState>(() => ({ status: user ? "authenticated" : "loading", user, signOut }), [user, signOut]);
  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}
