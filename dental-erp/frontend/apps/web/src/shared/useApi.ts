"use client";
import { useAuth } from "./AuthProvider";
import { api } from "./session";

/** Klient sessiya bitəndə (refresh alınmadıqda) istifadəçini çıxışa yönləndirir. */
export function useApi() {
  const { signOut } = useAuth();
  return api(() => void signOut());
}

/** Xətanı istifadəçiyə uyğun mətnə çevirir; server mətni (PHI ola bilər) göstərilmir, yalnız sabit `code`. */
export function problemMessage(code: string | undefined, fallback: string, map: Record<string, string> = {}): string {
  const common: Record<string, string> = {
    "http.403": "Bu əməliyyat üçün icazəniz yoxdur.",
    "http.404": "Qeyd tapılmadı.",
    "http.412": "Qeyd başqa istifadəçi tərəfindən dəyişdirilib. Səhifəni yeniləyin.",
    "http.429": "Çox sayda sorğu. Bir az gözləyin.",
  };
  return (code && (map[code] ?? common[code])) || fallback;
}
