#!/usr/bin/env node
// Yüngül yük/davamlılıq testi (asılılıq yoxdur, Node ≥ 20). Gateway-ə qarşı sabit paralellik ilə ssenarilər işlədir,
// gecikmə persentilləri və xəta nisbətini ölçür; hədd aşılarsa çıxış kodu 1.
//   node tests/load/load.mjs [--base http://127.0.0.1:5100] [--tenant demo] [--seconds 10] [--concurrency 16]
// Sirr: E2E_PASSWORD (default Passw0rd!e2e). Bu test TƏK maşında dev stack üçündür: mütləq rəqəmlər istehsal ölçüsü deyil,
// reqressiyanı (əvvəlki işə salmaya nisbətən) tutmaq üçündür. Hədlər --p95 / --max-errors ilə dəyişir.
import { performance } from "node:perf_hooks";

const arg = (name, def) => {
  const i = process.argv.indexOf(`--${name}`);
  return i > -1 ? process.argv[i + 1] : def;
};
const BASE = arg("base", "http://127.0.0.1:5100");
const TENANT = arg("tenant", "demo");
const SECONDS = Number(arg("seconds", 10));
const CONCURRENCY = Number(arg("concurrency", 16));
const P95_MS = Number(arg("p95", 750));
const MAX_ERR = Number(arg("max-errors", 0.01));
const PASSWORD = process.env.E2E_PASSWORD ?? "Passw0rd!e2e";

async function token() {
  const r = await fetch(`${BASE}/v1/auth/login`, {
    method: "POST",
    headers: { "content-type": "application/json", "x-tenant": TENANT },
    body: JSON.stringify({ email: `admin@${TENANT}.test`, password: PASSWORD }),
  });
  if (!r.ok) throw new Error(`login failed: ${r.status}`);
  return (await r.json()).accessToken;
}

const percentile = (sorted, p) => (sorted.length ? sorted[Math.min(sorted.length - 1, Math.ceil((p / 100) * sorted.length) - 1)] : NaN);

async function run(name, makeRequest, ok = (s) => s >= 200 && s < 300) {
  const deadline = performance.now() + SECONDS * 1000;
  const latencies = [];
  const statuses = new Map();
  let errors = 0;
  const worker = async (id) => {
    for (let n = 0; performance.now() < deadline; n++) {
      const t0 = performance.now();
      try {
        const res = await makeRequest(id, n);
        await res.arrayBuffer();
        statuses.set(res.status, (statuses.get(res.status) ?? 0) + 1);
        if (!ok(res.status)) errors++;
      } catch {
        errors++;
        statuses.set("network", (statuses.get("network") ?? 0) + 1);
      }
      latencies.push(performance.now() - t0);
    }
  };
  await Promise.all(Array.from({ length: CONCURRENCY }, (_, i) => worker(i)));
  latencies.sort((a, b) => a - b);
  return {
    name,
    requests: latencies.length,
    rps: +(latencies.length / SECONDS).toFixed(1),
    p50: +percentile(latencies, 50).toFixed(1),
    p95: +percentile(latencies, 95).toFixed(1),
    p99: +percentile(latencies, 99).toFixed(1),
    errorRate: +(errors / Math.max(1, latencies.length)).toFixed(4),
    statuses: Object.fromEntries(statuses),
  };
}

const access = await token();
const h = { authorization: `Bearer ${access}`, "x-tenant": TENANT };
const me = await (await fetch(`${BASE}/v1/auth/me`, { headers: h })).json();
const branch = me.branches[0];
const today = new Date();
const from = new Date(today.getFullYear(), today.getMonth(), today.getDate()).toISOString();
const to = new Date(today.getFullYear(), today.getMonth(), today.getDate() + 1).toISOString();

const results = [];
results.push(await run("GET dashboard/summary (keşli)", () => fetch(`${BASE}/v1/dashboard/summary`, { headers: h })));
results.push(await run("GET patients (siyahı)", () => fetch(`${BASE}/v1/patients?limit=25`, { headers: h })));
results.push(await run("GET patients?q= (axtarış)", (_, n) => fetch(`${BASE}/v1/patients?q=${encodeURIComponent("Te" + (n % 7))}`, { headers: h })));
results.push(await run("GET appointments (gün)", () => fetch(`${BASE}/v1/appointments?from=${from}&to=${to}`, { headers: h })));
results.push(await run("GET invoices", () => fetch(`${BASE}/v1/invoices?limit=25`, { headers: h })));
results.push(
  await run("POST patients (yazma, PII şifrələmə + audit + outbox)", (id, n) =>
    fetch(`${BASE}/v1/patients`, {
      method: "POST",
      headers: { ...h, "content-type": "application/json", "idempotency-key": crypto.randomUUID() },
      body: JSON.stringify({ branchId: branch, firstName: "Yuk", lastName: `Test${id}x${n}${Date.now().toString(36)}`, phone: `+99455${String(Math.floor(Math.random() * 1e7)).padStart(7, "0")}` }),
    }),
  ),
);
// Autentifikasiya olmadan: rədd edilməlidir və ucuz olmalıdır
results.push(await run("GET patients tokensiz (401 gözlənilir)", () => fetch(`${BASE}/v1/patients`, { headers: { "x-tenant": TENANT } }), (s) => s === 401));

console.log(`\nGateway=${BASE} tenant=${TENANT} paralellik=${CONCURRENCY} müddət=${SECONDS}s/ssenari\n`);
console.table(results.map(({ statuses, ...r }) => r));
for (const r of results) if (Object.keys(r.statuses).some((k) => !/^(2\d\d|401)$/.test(k))) console.log(`diqqət: ${r.name} statusları`, r.statuses);

const failed = results.filter((r) => r.p95 > P95_MS || r.errorRate > MAX_ERR);
if (failed.length) {
  console.error(`\nHƏDD AŞILDI (p95 ≤ ${P95_MS} ms, xəta ≤ ${MAX_ERR * 100}%):`, failed.map((r) => r.name));
  process.exit(1);
}
console.log("\nBütün ssenarilər həddlər daxilindədir.");
