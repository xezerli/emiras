"use client";
import { useEffect, useState } from "react";
import { Button } from "./components";
import { applyTheme, readStoredTheme, resolveInitialTheme, storeTheme, type Theme } from "./theme";

export function ThemeToggle() {
  const [theme, setTheme] = useState<Theme>("light");

  useEffect(() => {
    const t = resolveInitialTheme(globalThis.matchMedia?.("(prefers-color-scheme: dark)").matches ?? false);
    setTheme(t);
    applyTheme(t);
  }, []);

  const toggle = () => {
    const next: Theme = theme === "dark" ? "light" : "dark";
    setTheme(next);
    applyTheme(next);
    storeTheme(next);
  };

  return (
    <Button variant="text" onClick={toggle} aria-pressed={theme === "dark"}>
      {theme === "dark" ? "Açıq rejim" : "Tünd rejim"}
    </Button>
  );
}

export { readStoredTheme };
