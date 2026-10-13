import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import axe from "axe-core";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { Alert, Badge, Button, EmptyState, SelectField, Spinner, TextField, ThemeToggle, resolveInitialTheme, storeTheme } from "../src";

async function violations(container: HTMLElement) {
  const r = await axe.run(container, { rules: { "color-contrast": { enabled: false } } }); // jsdom CSS yoxdur
  return r.violations.map((v) => v.id);
}

beforeEach(() => {
  localStorage.clear();
  document.documentElement.removeAttribute("data-theme");
});

describe("Button", () => {
  it("is disabled and busy while loading, and does not fire clicks", async () => {
    const onClick = vi.fn();
    render(<Button loading onClick={onClick}>Saxla</Button>);
    const b = screen.getByRole("button", { name: "Saxla" });
    expect(b).toBeDisabled();
    expect(b).toHaveAttribute("aria-busy", "true");
    await userEvent.click(b);
    expect(onClick).not.toHaveBeenCalled();
  });

  it("defaults to type=button so it never submits a form by accident", () => {
    render(<Button>OK</Button>);
    expect(screen.getByRole("button")).toHaveAttribute("type", "button");
  });
});

describe("TextField", () => {
  it("links label, hint and error for assistive tech", () => {
    render(<TextField label="E-poçt" hint="Klinika e-poçtu" error="Yanlış format" />);
    const input = screen.getByLabelText("E-poçt");
    expect(input).toHaveAttribute("aria-invalid", "true");
    const described = input.getAttribute("aria-describedby")!.split(" ");
    expect(described).toHaveLength(2);
    expect(document.getElementById(described[1]!)).toHaveTextContent("Yanlış format");
    expect(screen.getByRole("alert")).toHaveTextContent("Yanlış format");
  });

  it("has no aria-invalid/describedby when valid and without hint", () => {
    render(<TextField label="Ad" />);
    const input = screen.getByLabelText("Ad");
    expect(input).not.toHaveAttribute("aria-invalid");
    expect(input).not.toHaveAttribute("aria-describedby");
  });

  it("two fields never share ids", () => {
    render(<><TextField label="A" /><TextField label="B" /></>);
    expect(screen.getByLabelText("A").id).not.toBe(screen.getByLabelText("B").id);
  });
});

describe("feedback components", () => {
  it("error alerts are assertive (role=alert) while info is polite (role=status)", () => {
    const { rerender } = render(<Alert tone="error" title="Xəta" />);
    expect(screen.getByRole("alert")).toBeInTheDocument();
    rerender(<Alert tone="info" title="Məlumat" />);
    expect(screen.getByRole("status")).toBeInTheDocument();
  });

  it("badge conveys state with a symbol, not only colour", () => {
    render(<Badge tone="error">Borclu</Badge>);
    expect(screen.getByText("✕")).toBeInTheDocument();
  });

  it("spinner exposes a status label", () => {
    render(<Spinner label="Yüklənir" />);
    expect(screen.getByRole("status")).toHaveTextContent("Yüklənir");
  });
});

describe("theme", () => {
  it("stored choice wins over the OS preference", () => {
    storeTheme("light");
    expect(resolveInitialTheme(true)).toBe("light");
  });

  it("falls back to the OS preference and survives a throwing localStorage", () => {
    expect(resolveInitialTheme(true)).toBe("dark");
    const spy = vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => {
      throw new Error("blocked");
    });
    expect(resolveInitialTheme(false)).toBe("light");
    spy.mockRestore();
  });

  it("toggle switches data-theme, aria-pressed and persists", async () => {
    render(<ThemeToggle />);
    const b = screen.getByRole("button");
    expect(b).toHaveAttribute("aria-pressed", "false");
    await userEvent.click(b);
    expect(document.documentElement.dataset.theme).toBe("dark");
    expect(b).toHaveAttribute("aria-pressed", "true");
    expect(localStorage.getItem("dc-theme")).toBe("dark");
  });
});

describe("accessibility (axe)", () => {
  it("a composed form has no violations", async () => {
    const { container } = render(
      <main>
        <h1>Giriş</h1>
        <form>
          <TextField label="E-poçt" type="email" error="Məcburidir" />
          <SelectField label="Filial"><option>Mərkəz</option></SelectField>
          <Button type="submit">Daxil ol</Button>
          <Alert tone="error" title="Xəta" />
          <EmptyState title="Boşdur" />
        </form>
      </main>,
    );
    expect(await violations(container)).toEqual([]);
  });
});
