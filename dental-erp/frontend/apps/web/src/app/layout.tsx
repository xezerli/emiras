import "@dentacore/ui/src/tokens.css";
import "@dentacore/ui/src/components.css";
import "./shell.css";
import type { Metadata } from "next";
import type { ReactNode } from "react";
import { Providers } from "./providers";

export const metadata: Metadata = { title: { default: "DentaCore", template: "%s · DentaCore" }, description: "Diş klinikası idarəetmə sistemi" };

/** Tema ilk boyamadan əvvəl qoyulur (yanıb-sönmə olmasın); localStorage bloklanıbsa OS seçiminə düşür. */
const themeInit = `try{var t=localStorage.getItem("dc-theme");if(t!=="light"&&t!=="dark"){t=matchMedia("(prefers-color-scheme: dark)").matches?"dark":"light"}document.documentElement.dataset.theme=t}catch(e){}`;

export default function RootLayout({ children }: { children: ReactNode }) {
  return (
    <html lang="az" suppressHydrationWarning>
      <head>
        <script dangerouslySetInnerHTML={{ __html: themeInit }} />
      </head>
      <body className="dc-body">
        <Providers>{children}</Providers>
      </body>
    </html>
  );
}
