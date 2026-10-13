import { fileURLToPath } from "node:url";
import type { NextConfig } from "next";

/** Gateway/ingress ünvanı. Produksiyada ingress `/v1` və `/hubs` yollarını özü yönləndirir, rewrites yalnız dev/self-host üçündür. */
const apiOrigin = process.env.API_ORIGIN ?? "http://localhost:8080";

const securityHeaders = [
  { key: "X-Content-Type-Options", value: "nosniff" },
  { key: "X-Frame-Options", value: "DENY" },
  { key: "Referrer-Policy", value: "strict-origin-when-cross-origin" },
  { key: "Permissions-Policy", value: "camera=(), microphone=(), geolocation=()" },
];

// Konteyner build-i: NEXT_OUTPUT=standalone → minimal `server.js` + yalnız lazım olan node_modules (monorepo kökü izlənir)
const standalone = process.env.NEXT_OUTPUT === "standalone";

const config: NextConfig = {
  ...(standalone ? { output: "standalone" as const, outputFileTracingRoot: fileURLToPath(new URL("../../", import.meta.url)) } : {}),
  reactStrictMode: true,
  poweredByHeader: false,
  transpilePackages: ["@dentacore/ui", "@dentacore/api-client"],
  async headers() {
    return [{ source: "/:path*", headers: securityHeaders }];
  },
  async rewrites() {
    if (process.env.DISABLE_API_REWRITE === "1") return [];
    return [
      { source: "/v1/:path*", destination: `${apiOrigin}/v1/:path*` },
      { source: "/hubs/:path*", destination: `${apiOrigin}/hubs/:path*` },
    ];
  },
};

export default config;
