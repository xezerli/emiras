import { expect, test } from "@playwright/test";
import { apiAs, loginAndWaitForDashboard, unique } from "./helpers";

test("an appointment appears on the day view and check-in changes its status", async ({ page, request }) => {
  const admin = await apiAs(request);
  const me = await admin.call<{ branches: string[] }>("get", "/auth/me");
  const doctor = await (await apiAs(request, "doctor")).call<{ id: string }>("get", "/auth/me");
  const last = unique("Cal");
  const patient = await admin.call<{ id: string }>("post", "/patients", { branchId: me.branches[0], firstName: "Rəşad", lastName: last }, { "Idempotency-Key": unique("k") });

  const start = new Date(Date.now() + 40 * 60_000);
  start.setSeconds(0, 0);
  const end = new Date(start.getTime() + 30 * 60_000);
  await admin.call("post", "/appointments", { branchId: me.branches[0], patientId: patient.id, providerId: doctor.id, start: start.toISOString(), end: end.toISOString(), reason: "Yoxlama" });

  await loginAndWaitForDashboard(page);
  await page.getByRole("link", { name: "Təqvim" }).click();
  const localDay = `${start.getFullYear()}-${String(start.getMonth() + 1).padStart(2, "0")}-${String(start.getDate()).padStart(2, "0")}`;
  await page.getByLabel("Gün").fill(localDay);

  const row = page.getByRole("row").filter({ hasText: last });
  await expect(row).toBeVisible();
  await expect(row.getByText("Planlaşdırılıb")).toBeVisible();

  await row.getByRole("button", { name: /Gəldi/ }).click();
  await expect(row.getByText("Gəlib")).toBeVisible();
  await expect(row.getByRole("button", { name: /Gəldi/ })).toHaveCount(0);   // yalnız ləğv qalır
});
