import { expect, type APIRequestContext, type Page } from "@playwright/test";

export const TENANT = process.env.E2E_TENANT ?? "demo";
export const PASSWORD = process.env.E2E_PASSWORD ?? "Passw0rd!e2e";
export const GATEWAY = process.env.E2E_API_ORIGIN ?? "http://127.0.0.1:5100";

export type Role = "admin" | "reception" | "doctor";

export async function loginUi(page: Page, role: Role = "admin", password = PASSWORD) {
  await page.goto("/login");
  await page.getByLabel("E-poçt").fill(`${role}@${TENANT}.test`);
  await page.getByLabel("Parol").fill(password);
  await page.getByRole("button", { name: "Daxil ol" }).click();
}

export async function loginAndWaitForDashboard(page: Page, role: Role = "admin") {
  await loginUi(page, role);
  await expect(page).toHaveURL(/\/dashboard$/);
  await expect(page.getByRole("heading", { name: "İdarə paneli", level: 1 })).toBeVisible();
}

/** Birbaşa Gateway-ə (UI-dan keçmədən) tokenli sorğu konteksti: test məlumatı hazırlamaq üçün. */
export async function apiAs(request: APIRequestContext, role: Role = "admin") {
  const res = await request.post(`${GATEWAY}/v1/auth/login`, {
    headers: { "X-Tenant": TENANT },
    data: { email: `${role}@${TENANT}.test`, password: PASSWORD },
  });
  expect(res.ok(), `login ${role}`).toBeTruthy();
  const { accessToken } = (await res.json()) as { accessToken: string };
  const headers = { "X-Tenant": TENANT, Authorization: `Bearer ${accessToken}` };
  const call = async <T>(method: "get" | "post", path: string, data?: unknown, extra: Record<string, string> = {}): Promise<T> => {
    const r = await request[method](`${GATEWAY}/v1${path}`, { headers: { ...headers, ...extra }, data });
    expect(r.ok(), `${method} ${path} -> ${r.status()} ${await r.text()}`).toBeTruthy();
    return (r.status() === 204 ? undefined : await r.json()) as T;
  };
  return { call, headers };
}

export const unique = (prefix: string) => `${prefix}${Date.now().toString(36)}${Math.floor(Math.random() * 1e4)}`;

/** 9 rəqəmli unikal telefon: dublikat yoxlaması testlər arasında qarışmasın. */
export const uniquePhone = () => `+99450${String(Date.now()).slice(-7)}`;

/** İstənilən rolla daxil olur; idarə paneli olmayan rollar ilk əlçatan bölməyə düşür. */
export async function signInAs(page: Page, role: Role) {
  await page.context().clearCookies();
  await loginUi(page, role);
  await expect(page.getByRole("navigation", { name: "Əsas naviqasiya" })).toBeVisible();
}
