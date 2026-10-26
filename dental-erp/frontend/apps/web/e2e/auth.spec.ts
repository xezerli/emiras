import { expect, test } from "@playwright/test";
import { loginAndWaitForDashboard, loginUi, signInAs } from "./helpers";

test("wrong password shows a generic error and stays on login", async ({ page }) => {
  await loginUi(page, "admin", "wrong-password-123");
  await expect(page.getByRole("alert").filter({ hasText: "yanlışdır" })).toBeVisible();
  await expect(page).toHaveURL(/\/login$/);
  expect(await page.context().cookies()).toEqual([]);
});

test("sign in sets an httpOnly refresh cookie, keeps tokens out of storage, and survives reload", async ({ page }) => {
  await loginAndWaitForDashboard(page);

  const cookies = await page.context().cookies();
  const rt = cookies.find((c) => c.name === "dc_rt");
  expect(rt, "refresh cookie").toBeTruthy();
  expect(rt!.httpOnly).toBe(true);
  expect(rt!.sameSite).toBe("Strict");
  expect(rt!.path).toBe("/api/session");

  const stored = await page.evaluate(() => JSON.stringify({ ...localStorage }) + JSON.stringify({ ...sessionStorage }) + document.cookie);
  expect(stored).not.toMatch(/eyJ/);     // heç bir JWT
  expect(stored).not.toContain("dc_rt");

  await page.reload();
  await expect(page.getByRole("heading", { name: "İdarə paneli", level: 1 })).toBeVisible();
});

test("protected pages redirect anonymous visitors to login", async ({ page }) => {
  await page.goto("/patients");
  await expect(page).toHaveURL(/\/login$/);
});

test("sign out revokes the session: reload stays on login", async ({ page }) => {
  await loginAndWaitForDashboard(page);
  await page.getByRole("button", { name: "Çıxış" }).click();
  await expect(page).toHaveURL(/\/login$/);
  await page.goto("/dashboard");
  await expect(page).toHaveURL(/\/login$/);
});

test("navigation follows the role: doctor and reception land on their first allowed section", async ({ page }) => {
  await loginAndWaitForDashboard(page, "admin");
  const nav = page.getByRole("navigation", { name: "Əsas naviqasiya" });
  for (const name of ["İdarə paneli", "Pasiyentlər", "Təqvim", "Maliyyə"]) await expect(nav.getByRole("link", { name })).toBeVisible();

  await signInAs(page, "doctor");
  await expect(page).toHaveURL(/\/patients$/);
  await expect(page.getByRole("link", { name: "İdarə paneli" })).toHaveCount(0);   // doctor: report:read yoxdur
  await expect(page.getByRole("link", { name: "Maliyyə" })).toBeVisible();         // invoice:read (own) var

  await signInAs(page, "reception");
  await expect(page).toHaveURL(/\/patients$/);
  await expect(page.getByRole("link", { name: "İdarə paneli" })).toHaveCount(0);
});
