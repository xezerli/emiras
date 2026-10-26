import { expect, test } from "@playwright/test";
import { TENANT, loginAndWaitForDashboard, signInAs, unique } from "./helpers";

test("admin creates a user from the UI, the user signs in with the role's permissions, then admin disables the account", async ({ page }) => {
  const email = `${unique("staff")}@${TENANT}.test`;
  const name = `Test ${unique("İşçi")}`;
  const password = "Str0ng-Passw0rd-e2e";

  await loginAndWaitForDashboard(page, "admin");
  await page.getByRole("navigation", { name: "Əsas naviqasiya" }).getByRole("link", { name: "İstifadəçilər" }).click();
  await expect(page.getByRole("heading", { name: "İstifadəçilər", level: 1 })).toBeVisible();

  await page.getByRole("button", { name: "Yeni istifadəçi" }).click();
  await page.getByLabel("E-poçt").fill(email);
  await page.getByLabel("Ad Soyad").fill(name);
  await page.getByLabel("İlkin parol").fill("zəif");
  await expect(page.getByRole("button", { name: "Yarat" })).toBeDisabled();
  await page.getByLabel("İlkin parol").fill(password);
  await page.getByLabel("Rol", { exact: true }).selectOption({ label: "Kassir" });
  await page.getByRole("button", { name: "Yarat" }).click();
  await expect(page.getByText("İstifadəçi yaradıldı.")).toBeVisible();
  const row = page.getByRole("row", { name: new RegExp(name) });
  await expect(row).toBeVisible();
  await expect(row.getByText("Kassir")).toBeVisible();
  await expect(row.getByText(email)).toHaveCount(0);   // e-poçt maskalanır

  // Yeni istifadəçi: kassir yalnız Maliyyə bölməsini görür, idarəetmə yoxdur
  await page.context().clearCookies();
  await page.goto("/login");
  await page.getByLabel("E-poçt").fill(email);
  await page.getByLabel("Parol").fill(password);
  await page.getByRole("button", { name: "Daxil ol" }).click();
  const nav = page.getByRole("navigation", { name: "Əsas naviqasiya" });
  await expect(nav.getByRole("link", { name: "Maliyyə" })).toBeVisible();
  await expect(nav.getByRole("link", { name: "İstifadəçilər" })).toHaveCount(0);
  await page.goto("/users");
  await expect(page.getByText("İstifadəçilər yüklənmədi")).toBeVisible();   // server 403 verir; menyu yalnız rahatlıqdır

  // İdarəçi hesabı söndürür: yeni giriş mümkün deyil
  await signInAs(page, "admin");
  await page.goto("/users");
  await page.getByRole("button", { name: `Söndür: ${name}` }).click();
  await expect(page.getByRole("row", { name: new RegExp(name) }).getByText("Söndürülüb")).toBeVisible();
  await page.context().clearCookies();
  await page.goto("/login");
  await page.getByLabel("E-poçt").fill(email);
  await page.getByLabel("Parol").fill(password);
  await page.getByRole("button", { name: "Daxil ol" }).click();
  await expect(page).toHaveURL(/\/login$/);
  await expect(page.locator(".dc-alert--error")).toBeVisible();
});

test("admin edits a role's permissions, they persist, and administrator roles stay read-only", async ({ page }) => {
  await loginAndWaitForDashboard(page, "admin");
  await page.goto("/roles");
  await page.getByLabel("Rol", { exact: true }).selectOption({ label: "Gigiyenist" });
  const box = page.getByLabel("patient:read icazəsi");
  await expect(box).toBeEnabled();
  if (!(await box.isChecked())) await box.check();
  await page.getByRole("button", { name: "Saxla" }).click();
  await expect(page.getByText(/İcazələr saxlandı/)).toBeVisible();
  await page.reload();
  await page.getByLabel("Rol", { exact: true }).selectOption({ label: "Gigiyenist" });
  await expect(page.getByLabel("patient:read icazəsi")).toBeChecked();

  // Qorunan rol dəyişdirilə bilmir
  await page.getByLabel("Rol", { exact: true }).selectOption({ label: "Klinik Administrator" });
  await expect(page.getByText("Bu rol dəyişdirilə bilməz")).toBeVisible();
  await expect(page.getByRole("button", { name: "Saxla" })).toHaveCount(0);
});
