import { expect, test } from "@playwright/test";
import { apiAs, loginAndWaitForDashboard, signInAs, unique, uniquePhone } from "./helpers";

test("register, find (search falls back to SQL when Elasticsearch is down), open and reveal masked data", async ({ page }) => {
  const last = unique("Test");
  const phone = uniquePhone();
  await loginAndWaitForDashboard(page);

  await page.getByRole("link", { name: "Pasiyentlər" }).click();
  await page.getByRole("link", { name: "Yeni pasiyent" }).click();
  await page.getByLabel("Ad", { exact: true }).fill("Elnur");
  await page.getByLabel("Soyad").fill(last);
  await page.getByLabel("Telefon").fill(phone);
  await page.getByRole("button", { name: "Saxla" }).click();

  await expect(page.getByRole("heading", { level: 1, name: new RegExp(last) })).toBeVisible();
  // telefon default maskalıdır
  await expect(page.getByText(phone)).toHaveCount(0);

  // axtarış
  await page.getByRole("link", { name: "Pasiyentlər" }).click();
  await page.getByLabel("Axtar").fill(last);
  await expect(page.getByRole("link", { name: new RegExp(last) })).toBeVisible();

  // maskasız göstər (admin patient:read_sensitive-ə malikdir)
  await page.getByRole("link", { name: new RegExp(last) }).click();
  await page.getByRole("button", { name: "Məlumatları göstər" }).click();
  await expect(page.getByText(phone)).toBeVisible();
  await page.getByRole("button", { name: "Maskala" }).click();
  await expect(page.getByText(phone)).toHaveCount(0);
});

test("same phone triggers the duplicate warning; confirming creates the family member", async ({ page }) => {
  const phone = uniquePhone();
  const lastA = unique("Dup");
  await loginAndWaitForDashboard(page);

  const create = async (first: string, last: string) => {
    await page.goto("/patients/new");
    await page.getByLabel("Ad", { exact: true }).fill(first);
    await page.getByLabel("Soyad").fill(last);
    await page.getByLabel("Telefon").fill(phone);
    await page.getByRole("button", { name: "Saxla" }).click();
  };

  await create("Anar", lastA);
  await expect(page.getByRole("heading", { level: 1, name: new RegExp(lastA) })).toBeVisible();

  await create("Aysel", lastA);
  const warning = page.getByRole("alert").filter({ hasText: "Oxşar pasiyent" });
  await expect(warning).toBeVisible();
  await warning.getByRole("button", { name: "Yenə də yarat" }).click();
  await expect(page.getByRole("heading", { level: 1, name: /Aysel/ })).toBeVisible();
});

test("reception sees masked data first; reveal is allowed by its branch-scoped patient:read_sensitive", async ({ page, request }) => {
  const admin = await apiAs(request);
  const me = await admin.call<{ branches: string[] }>("get", "/auth/me");
  const last = unique("Mask");
  const phone = uniquePhone();
  const p = await admin.call<{ id: string }>("post", "/patients", { branchId: me.branches[0], firstName: "Gülnar", lastName: last, phone }, { "Idempotency-Key": unique("k") });

  await signInAs(page, "reception");
  await page.goto(`/patients/${p.id}`);
  await expect(page.getByRole("heading", { level: 1, name: new RegExp(last) })).toBeVisible();
  await expect(page.getByText(phone)).toHaveCount(0);
  await page.getByRole("button", { name: "Məlumatları göstər" }).click();
  await expect(page.getByText(phone)).toBeVisible();
});
