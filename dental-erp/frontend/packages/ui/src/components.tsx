import { useId, type ButtonHTMLAttributes, type HTMLAttributes, type InputHTMLAttributes, type ReactNode, type SelectHTMLAttributes } from "react";

const cx = (...parts: Array<string | false | undefined>) => parts.filter(Boolean).join(" ");

export function Spinner({ label = "Yüklənir" }: { label?: string }) {
  return (
    <span role="status">
      <span className="dc-spinner" aria-hidden="true" />
      <span className="dc-sr-only">{label}</span>
    </span>
  );
}

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: "filled" | "tonal" | "outlined" | "text" | "danger";
  loading?: boolean;
}

export function Button({ variant = "filled", loading = false, disabled, className, children, type = "button", ...rest }: ButtonProps) {
  return (
    <button
      {...rest}
      type={type}
      className={cx("dc-btn", `dc-btn--${variant}`, className)}
      disabled={disabled || loading}
      aria-busy={loading || undefined}
    >
      {loading && <span className="dc-spinner" aria-hidden="true" />}
      {children}
    </button>
  );
}

export function Card({ className, ...rest }: HTMLAttributes<HTMLElement>) {
  return <section {...rest} className={cx("dc-card", className)} />;
}

export interface FieldProps {
  label: string;
  hint?: string;
  error?: string;
  children: (a: { id: string; "aria-describedby": string | undefined; "aria-invalid": true | undefined }) => ReactNode;
}

/** Label, hint və xətanı idarə olunan id-lərlə bağlayır (ekran oxuyucu: aria-describedby/aria-invalid). */
export function Field({ label, hint, error, children }: FieldProps) {
  const id = useId();
  const hintId = `${id}-hint`;
  const errId = `${id}-err`;
  const describedBy = [hint && hintId, error && errId].filter(Boolean).join(" ") || undefined;
  return (
    <div className="dc-field">
      <label className="dc-field__label" htmlFor={id}>{label}</label>
      {children({ id, "aria-describedby": describedBy, "aria-invalid": error ? true : undefined })}
      {hint && <span id={hintId} className="dc-field__hint">{hint}</span>}
      {error && <span id={errId} className="dc-field__error" role="alert">{error}</span>}
    </div>
  );
}

export function TextField({ label, hint, error, ...input }: Omit<InputHTMLAttributes<HTMLInputElement>, "id"> & Pick<FieldProps, "label" | "hint" | "error">) {
  return (
    <Field label={label} hint={hint} error={error}>
      {(a) => <input {...input} {...a} className={cx("dc-input", input.className)} />}
    </Field>
  );
}

export function SelectField({ label, hint, error, children, ...select }: Omit<SelectHTMLAttributes<HTMLSelectElement>, "id"> & Pick<FieldProps, "label" | "hint" | "error">) {
  return (
    <Field label={label} hint={hint} error={error}>
      {(a) => <select {...select} {...a} className={cx("dc-select", select.className)}>{children}</select>}
    </Field>
  );
}

export type Tone = "neutral" | "success" | "warning" | "error";
const toneIcon: Record<Tone, string> = { neutral: "•", success: "✓", warning: "!", error: "✕" };

/** Vəziyyət yalnız rənglə deyil, simvol + mətnlə də ötürülür (WCAG 1.4.1). */
export function Badge({ tone = "neutral", children }: { tone?: Tone; children: ReactNode }) {
  return (
    <span className={cx("dc-badge", `dc-badge--${tone}`)}>
      <span aria-hidden="true">{toneIcon[tone]}</span>
      {children}
    </span>
  );
}

export function Alert({ tone = "info", title, children }: { tone?: "error" | "warning" | "success" | "info"; title?: string; children?: ReactNode }) {
  // error/warning dərhal elan olunur (role=alert), qalanları nəzakətlə (role=status)
  const role = tone === "error" || tone === "warning" ? "alert" : "status";
  return (
    <div role={role} className={cx("dc-alert", `dc-alert--${tone}`)}>
      <div>
        {title && <strong>{title}</strong>}
        {children && <div>{children}</div>}
      </div>
    </div>
  );
}

export function Skeleton({ width = "100%", height = 16 }: { width?: number | string; height?: number | string }) {
  return <span className="dc-skeleton" style={{ width, height }} aria-hidden="true" />;
}

export function EmptyState({ title, children, action, level = 2 }: { title: string; children?: ReactNode; action?: ReactNode; level?: 2 | 3 | 4 }) {
  const Heading = `h${level}` as const;
  return (
    <div className="dc-empty">
      <Heading>{title}</Heading>
      {children && <p>{children}</p>}
      {action}
    </div>
  );
}
