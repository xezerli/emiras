-- T005: Clinical context (odontoqram, plan, resept, qeydlər)
CREATE TABLE visits (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    patient_id uuid NOT NULL REFERENCES patients(id),
    appointment_id uuid REFERENCES appointments(id),
    provider_id uuid NOT NULL REFERENCES users(id),
    branch_id uuid NOT NULL REFERENCES branches(id),
    started_at timestamptz NOT NULL DEFAULT now(), ended_at timestamptz,
    chief_complaint text, status text NOT NULL DEFAULT 'open' CHECK (status IN ('open','closed')),
    created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(), row_version int NOT NULL DEFAULT 1
);
CREATE INDEX ix_visits_patient ON visits(patient_id, started_at DESC);
CREATE INDEX ix_visits_provider ON visits(provider_id, started_at DESC);

-- Odontoqram: diş-səth səviyyəsində vəziyyət. FDI nömrələmə (11-48 daimi, 51-85 süd).
CREATE TABLE tooth_records (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    patient_id uuid NOT NULL REFERENCES patients(id) ON DELETE CASCADE,
    visit_id uuid REFERENCES visits(id),
    tooth_fdi smallint NOT NULL CHECK (tooth_fdi BETWEEN 11 AND 48 OR tooth_fdi BETWEEN 51 AND 85),
    surface text CHECK (surface IN ('M','D','O','B','L','I','F','P')),   -- NULL = bütün diş
    condition text NOT NULL CHECK (condition IN
        ('healthy','caries','filling','crown','bridge','implant','root_canal','extracted','missing','impacted','fracture','periapical_lesion','veneer')),
    material text, notes text, recorded_by uuid REFERENCES users(id),
    recorded_at timestamptz NOT NULL DEFAULT now(),
    superseded_at timestamptz,               -- yeni qeyd köhnəni əvəz edir; tarixçə saxlanır
    created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(), row_version int NOT NULL DEFAULT 1
);
CREATE INDEX ix_tooth_current ON tooth_records(patient_id, tooth_fdi) WHERE superseded_at IS NULL;

CREATE TABLE perio_charts (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    patient_id uuid NOT NULL REFERENCES patients(id) ON DELETE CASCADE,
    visit_id uuid REFERENCES visits(id), charted_by uuid REFERENCES users(id),
    charted_at timestamptz NOT NULL DEFAULT now(),
    measurements jsonb NOT NULL,   -- {"18":{"pd":[3,2,3,..],"rec":[..],"bop":[..],"mobility":1}, ...}
    created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(), row_version int NOT NULL DEFAULT 1
);
CREATE INDEX ix_perio_patient ON perio_charts(patient_id, charted_at DESC);

CREATE TABLE procedure_codes (            -- müalicə kataloqu (CDT/yerli kod)
    code text PRIMARY KEY, name text NOT NULL, category text,
    requires_tooth boolean NOT NULL DEFAULT true, default_duration_min int NOT NULL DEFAULT 30,
    is_active boolean NOT NULL DEFAULT true
);

CREATE TABLE treatment_plans (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    patient_id uuid NOT NULL REFERENCES patients(id), provider_id uuid NOT NULL REFERENCES users(id),
    title text NOT NULL, status text NOT NULL DEFAULT 'draft'
        CHECK (status IN ('draft','proposed','accepted','in_progress','completed','rejected','cancelled')),
    version int NOT NULL DEFAULT 1, accepted_at timestamptz,
    ai_generated boolean NOT NULL DEFAULT false, ai_model text,   -- məsuliyyət izi
    created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(), row_version int NOT NULL DEFAULT 1
);
CREATE INDEX ix_plans_patient ON treatment_plans(patient_id, created_at DESC);

CREATE TABLE treatment_plan_items (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    plan_id uuid NOT NULL REFERENCES treatment_plans(id) ON DELETE CASCADE,
    procedure_code text NOT NULL REFERENCES procedure_codes(code),
    tooth_fdi smallint, surface text, phase smallint NOT NULL DEFAULT 1,
    unit_price numeric(14,2) NOT NULL CHECK (unit_price >= 0), quantity int NOT NULL DEFAULT 1 CHECK (quantity > 0),
    discount_percent numeric(5,2) NOT NULL DEFAULT 0 CHECK (discount_percent BETWEEN 0 AND 100),
    status text NOT NULL DEFAULT 'planned' CHECK (status IN ('planned','scheduled','done','cancelled')),
    performed_visit_id uuid REFERENCES visits(id), performed_at timestamptz, performed_by uuid REFERENCES users(id),
    sort_order int NOT NULL DEFAULT 0
);
CREATE INDEX ix_plan_items_plan ON treatment_plan_items(plan_id, phase, sort_order);
CREATE INDEX ix_plan_items_open ON treatment_plan_items(status) WHERE status IN ('planned','scheduled');

CREATE TABLE prescriptions (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    patient_id uuid NOT NULL REFERENCES patients(id), visit_id uuid REFERENCES visits(id),
    provider_id uuid NOT NULL REFERENCES users(id), issued_at timestamptz NOT NULL DEFAULT now(),
    items jsonb NOT NULL,                -- [{drug,dose,frequency,days,notes}]
    allergy_check_passed boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_rx_patient ON prescriptions(patient_id, issued_at DESC);

CREATE TABLE clinical_notes (            -- SOAP; imzalandıqdan sonra dəyişməz, düzəliş = addendum
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    patient_id uuid NOT NULL REFERENCES patients(id), visit_id uuid REFERENCES visits(id),
    author_id uuid NOT NULL REFERENCES users(id),
    subjective text, objective text, assessment text, plan text,
    source text NOT NULL DEFAULT 'typed' CHECK (source IN ('typed','voice','ai_draft')),
    signed_at timestamptz, addendum_of uuid REFERENCES clinical_notes(id),
    created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(), row_version int NOT NULL DEFAULT 1
);
CREATE INDEX ix_notes_patient ON clinical_notes(patient_id, created_at DESC);

CREATE FUNCTION trg_note_locked() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.signed_at IS NOT NULL THEN
        RAISE EXCEPTION 'Signed clinical note is immutable; create an addendum';
    END IF;
    RETURN COALESCE(NEW, OLD);
END $$;
CREATE TRIGGER note_locked BEFORE UPDATE OR DELETE ON clinical_notes FOR EACH ROW EXECUTE FUNCTION trg_note_locked();

SELECT attach_standard_triggers('visits');
SELECT attach_standard_triggers('tooth_records');
SELECT attach_standard_triggers('perio_charts');
SELECT attach_standard_triggers('treatment_plans');
SELECT attach_standard_triggers('clinical_notes');
