import { formatMoney, formatShortDay } from "./format";

export interface Point {
  day: string;
  amount: number;
}

/** Əlçatan sütun qrafiki: SVG role=img + qısa təsvir, eyni məlumat cədvəl şəklində də verilir (WCAG 1.1.1, 1.4.1). */
export function RevenueChart({ data }: { data: Point[] }) {
  const w = 640;
  const h = 200;
  const pad = 24;
  const max = Math.max(1, ...data.map((d) => d.amount));
  const bw = data.length ? (w - pad * 2) / data.length : 0;
  const total = data.reduce((s, d) => s + d.amount, 0);

  return (
    <>
      <svg className="chart" viewBox={`0 0 ${w} ${h}`} role="img" aria-label={`Son ${data.length} gün gəlir qrafiki, cəmi ${formatMoney(total)}`}>
        {data.map((d, i) => {
          const bh = Math.round(((h - pad * 2) * d.amount) / max);
          return (
            <g key={d.day}>
              <rect className="chart__bar" x={pad + i * bw + 4} y={h - pad - bh} width={Math.max(bw - 8, 1)} height={bh} rx={4} />
              <text className="chart__axis" x={pad + i * bw + bw / 2} y={h - 6} textAnchor="middle">{formatShortDay(d.day)}</text>
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
