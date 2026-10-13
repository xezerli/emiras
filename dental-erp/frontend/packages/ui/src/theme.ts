export type Theme = "light" | "dark";
export const THEME_KEY = "dc-theme";

/** localStorage private pəncərədə/bloklanmış halda atır: həmişə try/catch. */
export function readStoredTheme(): Theme | null {
  try {
    const v = globalThis.localStorage?.getItem(THEME_KEY);
    return v === "light" || v === "dark" ? v : null;
  } catch {
    return null;
  }
}

export function storeTheme(theme: Theme): void {
  try {
    globalThis.localStorage?.setItem(THEME_KEY, theme);
  } catch {
    // seçim yalnız bu sessiyada qüvvədədir
  }
}

export function applyTheme(theme: Theme, root: HTMLElement = document.documentElement): void {
  root.dataset.theme = theme;
}

/** Saxlanmış seçim yoxdursa OS üstünlüyü. */
export function resolveInitialTheme(prefersDark: boolean): Theme {
  return readStoredTheme() ?? (prefersDark ? "dark" : "light");
}
