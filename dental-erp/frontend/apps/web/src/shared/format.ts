const money = new Intl.NumberFormat("az-AZ", { style: "currency", currency: "AZN", maximumFractionDigits: 2 });
export const formatMoney = (n: number) => money.format(n);
export const formatShortDay = (iso: string) => new Intl.DateTimeFormat("az-AZ", { day: "2-digit", month: "2-digit" }).format(new Date(`${iso}T00:00:00`));
