import AxeBuilder from "@axe-core/playwright";
import { expect, test } from "@playwright/test";
import { loginAndWaitForDashboard } from "./helpers";

const pages = [
  ["login", "/login"],
  ["dashboard", "/dashboard"],
  ["patients", "/patients"],
  ["new patient", "/patients/new"],
  ["calendar", "/calendar"],
  ["billing", "/billing"],
  ["users", "/users"],
  ["roles", "/roles"],
] as const;

for (const theme of ["light", "dark"] as const) {
  test(`WCAG 2.2 AA (axe, real contrast) in ${theme} theme`, async ({ page }) => {
    await page.addInitScript((t) => localStorage.setItem("dc-theme", t), theme);
    for (const [name, path] of pages) {
      if (path === "/login") {
        await page.goto(path);
      } else {
        if (!page.url().includes("/dashboard") && !page.url().includes("/patients") && !page.url().includes("/calendar") && !page.url().includes("/billing") && !page.url().includes("/users") && !page.url().includes("/roles")) await loginAndWaitForDashboard(page);
        await page.goto(path);
        await expect(page.getByRole("navigation", { name: "Əsas naviqasiya" })).toBeVisible();
      }
      await expect(page.locator("html")).toHaveAttribute("data-theme", theme);
      const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"]).analyze();
      expect(results.violations.map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(" ")).join(", ")}`), `${name} (${theme})`).toEqual([]);
    }
  });
}
