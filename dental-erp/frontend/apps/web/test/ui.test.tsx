import { render, screen } from "@testing-library/react";
import { QueryClient } from "@tanstack/react-query";
import axe from "axe-core";
import { describe, expect, it, vi } from "vitest";
import { ApiError } from "@dentacore/api-client";
import { loginErrorMessage } from "@/app/login/page";
import { can, visibleNav } from "@/shared/permissions";
import { devTenantHeaders, invalidateFor, queryKeysFor } from "@/shared/realtime";
import { labelIndexes, RevenueChart } from "@/shared/RevenueChart";
import { tokenStore } from "@/shared/session";

vi.mock("next/navigation", () => ({ useRouter: () => ({ replace: vi.fn() }), usePathname: () => "/" }));

describe("permissions", () => {
  const perms = [{ code: "patient:read", scope: "branch" as const }, { code: "invoice:read", scope: "own" as const }];

  it("shows only navigation the user may open", () => {
    expect(visibleNav(perms).map((n) => n.href)).toEqual(["/patients", "/billing"]);
    expect(visibleNav([])).toEqual([]);
    expect(visibleNav(undefined)).toEqual([]);
  });

  it("can() is exact-match, not prefix", () => {
    expect(can(perms, "patient:read")).toBe(true);
    expect(can(perms, "patient:read_sensitive")).toBe(false);
    expect(can(perms, "patient")).toBe(false);
  });
});

describe("realtime invalidation", () => {
  it("maps events to query keys", () => {
    expect(queryKeysFor("scheduling.appointment-booked")).toEqual([["dashboard"], ["appointments"]]);
    expect(queryKeysFor("billing.payment-received")).toEqual([["dashboard"], ["invoices"]]);
    expect(queryKeysFor("unknown.thing")).toEqual([]);
  });

  it("invalidates exactly the mapped queries", () => {
    const qc = new QueryClient();
    const spy = vi.spyOn(qc, "invalidateQueries");
    invalidateFor(qc, { event: "billing.invoice-issued" });
    expect(spy.mock.calls.map((c) => c[0]?.queryKey)).toEqual([["dashboard"], ["invoices"]]);
    spy.mockClear();
    invalidateFor(qc, { event: "other" });
    expect(spy).not.toHaveBeenCalled();
  });
});

describe("realtime tenant header", () => {
  it("sends X-Tenant only in dev", () => {
    expect(devTenantHeaders("demo")).toEqual({ "X-Tenant": "demo" });
    expect(devTenantHeaders(undefined)).toEqual({});
    expect(devTenantHeaders("")).toEqual({});
  });
});

describe("token store", () => {
  it("lives in memory and notifies subscribers", () => {
    const seen = vi.fn();
    const off = tokenStore.subscribe(seen);
    tokenStore.set("abc", 60);
    expect(tokenStore.get()).toBe("abc");
    expect(localStorage.length).toBe(0);
    expect(sessionStorage.length).toBe(0);
    tokenStore.set(undefined);
    expect(tokenStore.get()).toBeUndefined();
    expect(seen).toHaveBeenCalledTimes(2);
    off();
  });
});

describe("login error messages", () => {
  it("maps known codes and never leaks server details", () => {
    expect(loginErrorMessage(new ApiError(401, "auth.invalid_credentials", "x", "SQL boom"))).toBe("E-poçt və ya parol yanlışdır.");
    expect(loginErrorMessage(new ApiError(429, "http.429", "x", undefined))).toMatch(/Çox sayda/);
    expect(loginErrorMessage(new ApiError(500, "weird", "x", "stack trace"))).not.toContain("stack");
    expect(loginErrorMessage(new TypeError("fetch failed"))).toMatch(/qoşulmaq/);
  });
});

describe("RevenueChart", () => {
  const data = [{ day: "2026-10-01", amount: 100 }, { day: "2026-10-02", amount: 0 }, { day: "2026-10-03", amount: 250 }];

  it("describes the chart and offers the same data as a table", () => {
    render(<RevenueChart data={data} />);
    expect(screen.getByRole("img", { name: /3 gün.*cəmi/ })).toBeInTheDocument();
    expect(document.querySelectorAll("tbody tr")).toHaveLength(3);
  });

  it("handles empty and all-zero series without NaN", () => {
    const { container, rerender } = render(<RevenueChart data={[]} />);
    expect(container.innerHTML).not.toContain("NaN");
    rerender(<RevenueChart data={[{ day: "2026-10-01", amount: 0 }]} />);
    expect(container.innerHTML).not.toContain("NaN");
  });

  it("30 days show at most 6 axis labels, always including the first and last day", () => {
    const days = Array.from({ length: 30 }, (_, i) => ({ day: `2026-09-${String(i + 1).padStart(2, "0")}`, amount: i }));
    const { container } = render(<RevenueChart data={days} />);
    expect(container.querySelectorAll("svg text").length).toBeLessThanOrEqual(7);
    expect(container.querySelectorAll("svg text").length).toBeGreaterThanOrEqual(4);
    const shown = [...labelIndexes(30)];
    expect(shown[0]).toBe(0);
    expect(shown.at(-1)).toBe(29);
    // bütün sütunlar çəkilir, yalnız etiketlər seyrəldilir
    expect(container.querySelectorAll("svg rect").length).toBe(30);
  });

  it("labelIndexes keeps small series complete", () => {
    expect([...labelIndexes(3)]).toEqual([0, 1, 2]);
    expect(labelIndexes(0).size).toBe(0);
    expect(labelIndexes(6).size).toBe(6);
  });

  it("has no axe violations", async () => {
    const { container } = render(<main><h1>X</h1><RevenueChart data={data} /></main>);
    const r = await axe.run(container, { rules: { "color-contrast": { enabled: false } } });
    expect(r.violations.map((v) => v.id)).toEqual([]);
  });
});
