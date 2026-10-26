#!/usr/bin/env node
// docs/guides/img/*.png ekran görüntülərini REAL stack-dən çəkir (yalnız saxta ad/telefonlarla).
//   infra/dev/run-stack.sh start (TƏMİZ baza: STACK_DB=dentacore_docs)  →  pnpm e2e:build  →  node scripts/capture-docs.mjs
// Tələb: Playwright Chromium (PLAYWRIGHT_CHROMIUM=/opt/pw-browsers/chromium) və Next.js `next start -p 3100` (bu skript özü qaldırır).
import { spawn } from "node:child_process";
import { mkdirSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { chromium } from "@playwright/test";

const API = process.env.E2E_API_ORIGIN ?? "http://127.0.0.1:5100";
const TENANT = process.env.E2E_TENANT ?? "demo";
const PASSWORD = process.env.E2E_PASSWORD ?? "Passw0rd!e2e";
const PORT = Number(process.env.E2E_PORT ?? 3100);
const OUT = fileURLToPath(new URL("../../../../docs/guides/img/", import.meta.url));
mkdirSync(OUT, { recursive: true });

async function call(token, method, path, body, headers = {}) {
  const r = await fetch(`${API}/v1${path}`, {
    method,
    headers: { "x-tenant": TENANT, "content-type": "application/json", ...(token ? { authorization: `Bearer ${token}` } : {}), ...headers },
    body: body ? JSON.stringify(body) : undefined,
  });
  const text = await r.text();
  return { status: r.status, json: text ? JSON.parse(text) : undefined };
}
const login = async (role) => (await call(undefined, "POST", "/auth/login", { email: `${role}@${TENANT}.test`, password: PASSWORD })).json.accessToken;

// ---------------- saxta məlumat ----------------
const admin = await login("admin");
const doctor = await login("doctor");
const me = (await call(admin, "GET", "/auth/me")).json;
const doctorId = (await call(doctor, "GET", "/auth/me")).json.id;
const branch = me.branches[0];
const people = [
  ["Nigar", "Quliyeva", "F", "1990-05-01"], ["Rəşad", "Məmmədov", "M", "1985-11-12"], ["Aysel", "Həsənova", "F", "1998-03-22"],
  ["Elçin", "Əliyev", "M", "1979-07-30"], ["Günel", "Hüseynova", "F", "2001-01-15"], ["Tural", "İsmayılov", "M", "1992-09-09"],
];
const ids = [];
for (const [i, [first, last, gender, birthDate]] of people.entries()) {
  const r = await call(admin, "POST", "/patients", { branchId: branch, firstName: first, lastName: last, gender, birthDate, phone: `+99455000${String(1000 + i)}` });
  ids.push(r.json.id);
}
// ağır allergiya (kartda qırmızı xəbərdarlıq)
await call(admin, "POST", `/patients/${ids[0]}/allergies`, { substance: "Penisilin", reaction: "Anafilaksiya", severity: "anaphylaxis" });
// bu günün qəbulları (müxtəlif vəziyyətlər)
const slots = (await call(admin, "GET", `/appointments/availability?providerId=${doctorId}&date=${new Date().toISOString().slice(0, 10)}&durationMin=30`)).json ?? [];
const booked = [];
for (const [n, patient] of [ids[0], ids[1], ids[2]].entries()) {
  const s = slots[n * 2];
  if (!s) continue;
  const r = await call(admin, "POST", "/appointments", { branchId: branch, patientId: patient, providerId: doctorId, start: s.start, end: s.end, reason: ["Yoxlama", "Plomb", "Diş təmizliyi"][n] });
  if (r.status === 201) booked.push(r.json.id);
}
if (booked[0]) await call(admin, "POST", `/appointments/${booked[0]}/check-in`);
// kassa smeni və fakturalar
await call(admin, "POST", "/cash-shifts/open", { branchId: branch, openingCash: 100 });
const services = (await call(admin, "GET", "/services")).json;
const filling = services.find((s) => s.code === "FILLING").id;
const consult = services.find((s) => s.code === "CONSULT").id;
let shot = {};
for (const [k, [patient, svc, pay]] of Object.entries({ paid: [ids[1], filling, 80], partial: [ids[0], filling, 30], open: [ids[2], consult, 0] })) {
  const inv = (await call(admin, "POST", "/invoices", { kind: "invoice", patientId: patient, branchId: branch, items: [{ serviceId: svc, quantity: 1 }] })).json;
  await call(admin, "POST", `/invoices/${inv.id}/issue`);
  if (pay) await call(admin, "POST", `/invoices/${inv.id}/payments`, { method: "cash", amount: pay }, { "idempotency-key": crypto.randomUUID() });
  shot[k] = inv.id;
}

// ---------------- Next.js + brauzer ----------------
const server = spawn("pnpm", ["exec", "next", "start", "-p", String(PORT), "-H", "127.0.0.1"], {
  env: { ...process.env, API_ORIGIN: API, DEV_TENANT: TENANT }, stdio: "ignore",
});
for (let i = 0; i < 60; i++) {
  try { if ((await fetch(`http://127.0.0.1:${PORT}/login`)).ok) break; } catch { /* hələ qalxmayıb */ }
  await new Promise((r) => setTimeout(r, 500));
}
const browser = await chromium.launch({ executablePath: process.env.PLAYWRIGHT_CHROMIUM });
const base = `http://127.0.0.1:${PORT}`;

async function newPage(theme) {
  const ctx = await browser.newContext({ viewport: { width: 1280, height: 800 }, locale: "az-AZ", colorScheme: theme });
  await ctx.addInitScript((t) => localStorage.setItem("dc-theme", t), theme);
  return ctx.newPage();
}
async function signIn(page, role = "admin") {
  await page.goto(`${base}/login`);
  await page.getByLabel("E-poçt").fill(`${role}@${TENANT}.test`);
  await page.getByLabel("Parol").fill(PASSWORD);
  await page.getByRole("button", { name: "Daxil ol" }).click();
  await page.getByRole("navigation", { name: "Əsas naviqasiya" }).waitFor();
}
const snap = async (page, name) => { await page.waitForTimeout(600); await page.screenshot({ path: `${OUT}${name}.png` }); console.log("çəkildi:", name); };

try {
  let page = await newPage("light");
  await page.goto(`${base}/login`);
  await page.getByLabel("E-poçt").fill(`admin@${TENANT}.test`);
  await snap(page, "01-login");

  await signIn(page);
  await page.getByRole("heading", { name: "İdarə paneli", level: 1 }).waitFor();
  await snap(page, "02-dashboard");

  await page.goto(`${base}/patients`);
  await page.getByLabel("Axtar").fill("Qul");
  await page.getByRole("link", { name: /Quliyeva/ }).waitFor();
  await snap(page, "03-patients-search");

  await page.goto(`${base}/patients/${ids[0]}`);
  await page.getByRole("heading", { level: 1 }).waitFor();
  await snap(page, "04-patient-card");
  await page.getByRole("button", { name: "Məlumatları göstər" }).click();
  await page.getByText("+99455000").first().waitFor();
  await snap(page, "05-patient-reveal");

  await page.goto(`${base}/patients/new`);
  await page.getByLabel("Ad", { exact: true }).fill("Aysel");
  await page.getByLabel("Soyad").fill("Quliyeva");
  await page.getByLabel("Telefon").fill("+994550001000");
  await page.getByRole("button", { name: "Saxla" }).click();
  await page.getByRole("alert").filter({ hasText: "Oxşar pasiyent" }).waitFor();
  await snap(page, "06-duplicate-warning");

  await page.goto(`${base}/calendar`);
  await page.getByRole("row").filter({ hasText: "Quliyeva" }).first().waitFor().catch(() => undefined);
  await snap(page, "07-calendar");

  await page.goto(`${base}/billing`);
  await page.getByRole("link", { name: /INV-/ }).first().waitFor();
  await snap(page, "08-billing");

  await page.goto(`${base}/billing/${shot.partial}`);
  await page.getByRole("button", { name: "Qəbul et" }).waitFor();
  await snap(page, "09-invoice-payment");

  const dark = await newPage("dark");
  await signIn(dark);
  await dark.getByRole("heading", { name: "İdarə paneli", level: 1 }).waitFor();
  await snap(dark, "10-dashboard-dark");

  const reception = await newPage("light");
  await signIn(reception, "reception");
  await snap(reception, "11-reception-role");
} finally {
  await browser.close();
  server.kill();
}
