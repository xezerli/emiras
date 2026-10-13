import { expect, test } from "@playwright/test";
import { apiAs, loginAndWaitForDashboard, unique } from "./helpers";

test("open a shift, take a partial cash payment and then the remainder by card", async ({ page, request }) => {
  const admin = await apiAs(request);
  const me = await admin.call<{ branches: string[] }>("get", "/auth/me");
  const services = await admin.call<Array<{ id: string; code: string }>>("get", "/services");
  const filling = services.find((s) => s.code === "FILLING")!;
  const last = unique("Bill");
  const patient = await admin.call<{ id: string }>("post", "/patients", { branchId: me.branches[0], firstName: "Tural", lastName: last }, { "Idempotency-Key": unique("k") });
  const draft = await admin.call<{ id: string; number: string }>("post", "/invoices", {
    kind: "invoice", patientId: patient.id, branchId: me.branches[0], items: [{ serviceId: filling.id, quantity: 1 }],
  });
  await admin.call("post", `/invoices/${draft.id}/issue`);

  await loginAndWaitForDashboard(page);
  await page.getByRole("link", { name: "Maliyyə" }).click();

  // Smen yoxdursa nağd ödəniş düzgün səbəblə rədd olunur, smen açıldıqdan sonra keçir
  const shiftCard = page.getByRole("region", { name: "Kassa smeni" });
  await expect(shiftCard.getByText(/Açıq|Bağlı/)).toBeVisible();   // yüklənməni gözlə
  if (await shiftCard.getByRole("button", { name: "Smeni aç" }).isVisible()) {
    await page.goto(`/billing/${draft.id}`);
    await page.getByLabel("Məbləğ (AZN)").fill("10");
    await page.getByRole("button", { name: "Qəbul et" }).click();
    await expect(page.getByRole("alert").filter({ hasText: "kassa smenini açın" })).toBeVisible();
    await page.goto("/billing");
    await shiftCard.getByRole("button", { name: "Smeni aç" }).click();
  }
  await expect(shiftCard.getByText("Açıq")).toBeVisible();

  await page.getByRole("link", { name: draft.number }).click();
  await expect(page.getByRole("heading", { level: 1, name: new RegExp(draft.number) })).toBeVisible();
  await expect(page.getByText("Verilib")).toBeVisible();

  await page.getByLabel("Məbləğ (AZN)").fill("30");
  await page.getByRole("button", { name: "Qəbul et" }).click();
  await expect(page.getByText("Ödəniş qeydə alındı")).toBeVisible();
  await expect(page.getByText("Qismən ödənilib")).toBeVisible();
  await expect(page.getByRole("row", { name: /Qalıq/ })).toContainText("50");

  await page.getByLabel("Üsul").selectOption("card");
  await page.getByLabel("Məbləğ (AZN)").fill("50");
  await page.getByRole("button", { name: "Qəbul et" }).click();
  await expect(page.getByText("Ödənilib", { exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Qəbul et" })).toHaveCount(0);   // tam ödənilib: forma yox
});

test("overpayment is stopped before it reaches the server", async ({ page, request }) => {
  const admin = await apiAs(request);
  const me = await admin.call<{ branches: string[] }>("get", "/auth/me");
  const services = await admin.call<Array<{ id: string; code: string }>>("get", "/services");
  const patient = await admin.call<{ id: string }>("post", "/patients", { branchId: me.branches[0], firstName: "Fərid", lastName: unique("Over") }, { "Idempotency-Key": unique("k") });
  const inv = await admin.call<{ id: string }>("post", "/invoices", { kind: "invoice", patientId: patient.id, branchId: me.branches[0], items: [{ serviceId: services.find((s) => s.code === "CONSULT")!.id, quantity: 1 }] });
  await admin.call("post", `/invoices/${inv.id}/issue`);

  await loginAndWaitForDashboard(page);
  await page.goto(`/billing/${inv.id}`);
  let posted = 0;
  page.on("request", (r) => { if (r.method() === "POST" && r.url().includes("/payments")) posted++; });
  await page.getByLabel("Məbləğ (AZN)").fill("999");
  await page.getByRole("button", { name: "Qəbul et" }).click();
  await expect(page.getByRole("alert").filter({ hasText: "qalıq borcdan çoxdur" })).toBeVisible();
  expect(posted).toBe(0);
});
