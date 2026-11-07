-- T006: Billing context. Pul: numeric(14,2); məbləğlər tətbiq qatında Money VO ilə.
CREATE TABLE services (                   -- qiymət siyahısı
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    procedure_code text REFERENCES procedure_codes(code), code text NOT NULL UNIQUE, name text NOT NULL,
    category text, price numeric(14,2) NOT NULL CHECK (price >= 0), currency char(3) NOT NULL DEFAULT 'AZN',
    vat_rate numeric(5,2) NOT NULL DEFAULT 0, is_active boolean NOT NULL DEFAULT true,
    valid_from date NOT NULL DEFAULT current_date,
    created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(), row_version int NOT NULL DEFAULT 1
);

CREATE SEQUENCE invoice_number_seq;

CREATE TABLE invoices (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    number text NOT NULL UNIQUE DEFAULT ('INV-' || to_char(now(),'YYYY') || '-' || lpad(nextval('invoice_number_seq')::text, 6, '0')),
    kind text NOT NULL DEFAULT 'invoice' CHECK (kind IN ('estimate','invoice','credit_note')),
    branch_id uuid NOT NULL REFERENCES branches(id), patient_id uuid NOT NULL REFERENCES patients(id),
    provider_id uuid REFERENCES users(id), visit_id uuid REFERENCES visits(id), plan_id uuid REFERENCES treatment_plans(id),
    status text NOT NULL DEFAULT 'draft' CHECK (status IN ('draft','issued','partially_paid','paid','void','refunded')),
    currency char(3) NOT NULL DEFAULT 'AZN',
    subtotal numeric(14,2) NOT NULL DEFAULT 0, discount_total numeric(14,2) NOT NULL DEFAULT 0,
    tax_total numeric(14,2) NOT NULL DEFAULT 0, total numeric(14,2) NOT NULL DEFAULT 0,
    paid_total numeric(14,2) NOT NULL DEFAULT 0,
    promo_code text, insurance_policy_id uuid REFERENCES patient_insurance_policies(id),
    insurance_amount numeric(14,2) NOT NULL DEFAULT 0,
    issued_at timestamptz, due_date date, notes text, created_by uuid REFERENCES users(id),
    created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(), row_version int NOT NULL DEFAULT 1,
    CHECK (paid_total >= 0 AND total >= 0),
    CHECK (status <> 'paid' OR paid_total >= total - insurance_amount)
);
CREATE INDEX ix_invoices_patient ON invoices(patient_id, created_at DESC);
CREATE INDEX ix_invoices_debt ON invoices(branch_id, due_date) WHERE status IN ('issued','partially_paid');
CREATE INDEX ix_invoices_issued ON invoices(branch_id, issued_at DESC);

CREATE TABLE invoice_items (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    invoice_id uuid NOT NULL REFERENCES invoices(id) ON DELETE CASCADE,
    service_id uuid REFERENCES services(id), plan_item_id uuid REFERENCES treatment_plan_items(id),
    description text NOT NULL, tooth_fdi smallint, provider_id uuid REFERENCES users(id),
    quantity int NOT NULL DEFAULT 1 CHECK (quantity > 0), unit_price numeric(14,2) NOT NULL CHECK (unit_price >= 0),
    discount numeric(14,2) NOT NULL DEFAULT 0 CHECK (discount >= 0), vat_rate numeric(5,2) NOT NULL DEFAULT 0,
    line_total numeric(14,2) GENERATED ALWAYS AS (quantity * unit_price - discount) STORED
);
CREATE INDEX ix_invoice_items_invoice ON invoice_items(invoice_id);
CREATE INDEX ix_invoice_items_provider ON invoice_items(provider_id);   -- komissiya/gəlir hesabatı

CREATE TABLE cash_shifts (                -- kassa smeni
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    branch_id uuid NOT NULL REFERENCES branches(id), cashier_id uuid NOT NULL REFERENCES users(id),
    opened_at timestamptz NOT NULL DEFAULT now(), closed_at timestamptz,
    opening_cash numeric(14,2) NOT NULL DEFAULT 0, closing_cash numeric(14,2), expected_cash numeric(14,2)
);
CREATE UNIQUE INDEX ux_shift_open ON cash_shifts(cashier_id) WHERE closed_at IS NULL;

CREATE TABLE payments (                   -- append-only: düzəliş = əks əməliyyat (refund)
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    invoice_id uuid REFERENCES invoices(id), patient_id uuid NOT NULL REFERENCES patients(id),
    shift_id uuid REFERENCES cash_shifts(id),
    kind text NOT NULL DEFAULT 'payment' CHECK (kind IN ('payment','refund','prepayment')),
    method text NOT NULL CHECK (method IN ('cash','card','transfer','gift_card','insurance','installment','pos')),
    amount numeric(14,2) NOT NULL CHECK (amount > 0), currency char(3) NOT NULL DEFAULT 'AZN',
    reference text, gateway_txn_id text, idempotency_key text UNIQUE,
    received_by uuid REFERENCES users(id), paid_at timestamptz NOT NULL DEFAULT now(),
    refund_of uuid REFERENCES payments(id), reason text
);
CREATE INDEX ix_payments_invoice ON payments(invoice_id);
CREATE INDEX ix_payments_shift ON payments(shift_id);
CREATE INDEX ix_payments_paid_at ON payments(paid_at DESC);

CREATE FUNCTION trg_payments_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN RAISE EXCEPTION 'payments are append-only; post a refund instead'; END $$;
CREATE TRIGGER payments_no_change BEFORE UPDATE OR DELETE ON payments FOR EACH ROW EXECUTE FUNCTION trg_payments_immutable();

CREATE TABLE installment_plans (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    invoice_id uuid NOT NULL REFERENCES invoices(id), total numeric(14,2) NOT NULL, down_payment numeric(14,2) NOT NULL DEFAULT 0,
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE installments (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    plan_id uuid NOT NULL REFERENCES installment_plans(id) ON DELETE CASCADE,
    seq smallint NOT NULL, due_date date NOT NULL, amount numeric(14,2) NOT NULL CHECK (amount > 0),
    paid_amount numeric(14,2) NOT NULL DEFAULT 0, UNIQUE (plan_id, seq)
);
CREATE INDEX ix_installments_due ON installments(due_date) WHERE paid_amount < amount;

CREATE TABLE gift_cards (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(), code text NOT NULL UNIQUE,
    balance numeric(14,2) NOT NULL CHECK (balance >= 0), expires_at date, is_active boolean NOT NULL DEFAULT true
);
CREATE TABLE promo_codes (
    code text PRIMARY KEY, percent numeric(5,2), fixed_amount numeric(14,2),
    valid_from date, valid_to date, max_uses int, used_count int NOT NULL DEFAULT 0, is_active boolean NOT NULL DEFAULT true,
    CHECK ((percent IS NOT NULL) <> (fixed_amount IS NOT NULL))
);

SELECT attach_standard_triggers('services');
SELECT attach_standard_triggers('invoices');
