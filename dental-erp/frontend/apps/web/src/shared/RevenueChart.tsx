import { formatMoney, formatShortDay } from "./format";

/** x oxunda ən çox ~6 etiket: 30 gün üçün hər gün yazılsa üst-üstə düşür. Birinci və sonuncu həmişə göstərilir. */
export function labelIndexes(count: number, max = 6): Set<number> {
  if (count <= max) return new Set(Array.from({ length: count }, (_, i) => i));
  const step = Math.ceil((count - 1) / (max - 1));
  const shown = new Set<number>();
  for (let i = 0; i < count; i += step) shown.add(i);
  shown.add(count - 1);
  // sonuncu ilə əvvəlki etiket çox yaxındırsa, əvvəlkini at
  const last = count - 1;
  const prev = [...shown].filter((i) => i < last).at(-1);
  if (prev !== undefined && last - prev < step / 2) shown.delete(prev);
  return shown;
}

export interface Point {
  day: string;
  amount: number;
}

/** Əlçatan sütun qrafiki: SVG role=img + qısa təsvir, eyni məlumat cədvəl şəklində də verilir (WCAG 1.1.1, 1.4.1). */
export function RevenueChart({ data }: { data: Point[] }) {
  // viewBox kartın real enine yaxındır (~320px): daha geniş olsa SVG kiçilir və etiketlər oxunmaz olur
  const w = 320;
  const h = 180;
  const pad = 22;
  const max = Math.max(1, ...data.map((d) => d.amount));
  const bw = data.length ? (w - pad * 2) / data.length : 0;
  const total = data.reduce((s, d) => s + d.amount, 0);
  const labels = labelIndexes(data.length);

  return (
    <>
      <svg className="chart" viewBox={`0 0 ${w} ${h}`} role="img" aria-label={`Son ${data.length} gün gəlir qrafiki, cəmi ${formatMoney(total)}`}>
        {data.map((d, i) => {
          const bh = Math.round(((h - pad * 2) * d.amount) / max);
          return (
            <g key={d.day}>
              <rect className="chart__bar" x={pad + i * bw + 4} y={h - pad - bh} width={Math.max(bw - 8, 1)} height={bh} rx={4} />
              {labels.has(i) && <text className="chart__axis" x={pad + i * bw + bw / 2} y={h - 5} textAnchor="middle">{formatShortDay(d.day)}</text>}
            </g>
          );
        })}
      </svg>
      <details>
        <summary>Cədvəl kimi göstər</summary>
        <table className="data-table">
          <caption className="dc-sr-only">Günlük gəlir</caption>
          <thead><tr><th scope="col">Gün</th><th scope="col" className="num">Gəlir</th></tr></thead>
          <tbody>
            {data.map((d) => (
              <tr key={d.day}><td>{formatShortDay(d.day)}</td><td className="num">{formatMoney(d.amount)}</td></tr>
            ))}
          </tbody>
        </table>
      </details>
    </>
  );
}
