import { expect, test } from "@playwright/test";
import { apiAs, GATEWAY, loginAndWaitForDashboard, unique } from "./helpers";

test("an appointment appears on the day view and check-in changes its status", async ({ page, request }) => {
  const admin = await apiAs(request);
  const me = await admin.call<{ branches: string[] }>("get", "/auth/me");
  const doctor = await (await apiAs(request, "doctor")).call<{ id: string }>("get", "/auth/me");
  const last = unique("Cal");
  const patient = await admin.call<{ id: string }>("post", "/patients", { branchId: me.branches[0], firstName: "Rəşad", lastName: last }, { "Idempotency-Key": unique("k") });

  // Doktor e2e bazasında təkrar işə salmalarla paylaşılır: üst-üstə düşən slot (409) olarsa başqa slot seçilir.
  // Check-in pəncərəsi başlanğıcdan 2 saat əvvəldir, ona görə başlanğıc 15..105 dəqiqə sonradır.
  let start = new Date();
  let booked = false;
  for (let attempt = 0; attempt < 12 && !booked; attempt++) {
    start = new Date(Date.now() + (15 + Math.floor(Math.random() * 18) * 5) * 60_000);
    start.setSeconds(0, 0);
    const end = new Date(start.getTime() + 5 * 60_000);
    const res = await request.post(`${GATEWAY}/v1/appointments`, {
      headers: admin.headers,
      data: { branchId: me.branches[0], patientId: patient.id, providerId: doctor.id, start: start.toISOString(), end: end.toISOString(), reason: "Yoxlama" },
    });
    if (res.status() !== 409) expect(res.ok(), await res.text()).toBeTruthy();
    booked = res.ok();
  }
  expect(booked, "free slot").toBe(true);

  await loginAndWaitForDashboard(page);
  await page.getByRole("link", { name: "Təqvim" }).click();
  const localDay = `${start.getFullYear()}-${String(start.getMonth() + 1).padStart(2, "0")}-${String(start.getDate()).padStart(2, "0")}`;
  await expect(page.getByRole("heading", { name: "Təqvim", level: 1 })).toBeVisible();
  await page.getByLabel("Gün", { exact: true }).fill(localDay);

  const row = page.getByRole("row").filter({ hasText: last });
  await expect(row).toBeVisible();
  await expect(row.getByText("Planlaşdırılıb")).toBeVisible();

  await row.getByRole("button", { name: /Gəldi/ }).click();
  await expect(row.getByText("Gəlib")).toBeVisible();
  await expect(row.getByRole("button", { name: /Gəldi/ })).toHaveCount(0);   // yalnız ləğv qalır
});
