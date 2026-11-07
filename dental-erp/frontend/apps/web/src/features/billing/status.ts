export const INVOICE_STATUS: Record<string, { label: string; tone: "neutral" | "success" | "warning" | "error" }> = {
  draft: { label: "Qaralama", tone: "neutral" },
  issued: { label: "Verilib", tone: "warning" },
  partially_paid: { label: "Qismən ödənilib", tone: "warning" },
  paid: { label: "Ödənilib", tone: "success" },
  void: { label: "Ləğv", tone: "error" },
  refunded: { label: "Qaytarılıb", tone: "error" },
};

/** Ödəniş qəbul edilə bilən statuslar (server yenə yoxlayır). */
export const PAYABLE = new Set(["issued", "partially_paid"]);
