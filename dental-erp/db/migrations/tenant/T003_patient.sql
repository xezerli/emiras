-- T003: Patient context. PHI strategiyası:
--  * ad/soyad açıq (əməliyyat axtarışı, pg_trgm); telefon/email/FİN şifrəli (AES-256-GCM) + blind index hash
--  * tam mətn/fuzzy axtarış Elasticsearch-də
CREATE TABLE patients (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    chart_no bigint GENERATED ALWAYS AS IDENTITY UNIQUE,
    branch_id uuid NOT NULL REFERENCES branches(id),
    first_name text NOT NULL, last_name text NOT NULL, father_name text,
    birth_date date, gender char(1) CHECK (gender IN ('M','F','O')),
    national_id_enc bytea, national_id_hash bytea,
    phone_enc bytea, phone_hash bytea,
    email_enc bytea, email_hash bytea,
    address jsonb,
    blood_type text CHECK (blood_type IN ('A+','A-','B+','B-','AB+','AB-','O+','O-')),
    referral_source text, referred_by_patient_id uuid REFERENCES patients(id),
    preferred_channel text NOT NULL DEFAULT 'sms' CHECK (preferred_channel IN ('sms','whatsapp','telegram','email','push','none')),
    marketing_opt_in boolean NOT NULL DEFAULT false,
    no_show_count int NOT NULL DEFAULT 0, risk_score numeric(4,3),    -- AI
    status text NOT NULL DEFAULT 'active' CHECK (status IN ('active','inactive','deceased','merged')),
    notes text,
    created_by uuid REFERENCES users(id),
    created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(),
    deleted_at timestamptz, row_version int NOT NULL DEFAULT 1
);
CREATE INDEX ix_patients_name_trgm ON patients USING gin ((lower(last_name || ' ' || first_name)) gin_trgm_ops) WHERE deleted_at IS NULL;
CREATE INDEX ix_patients_phone ON patients(phone_hash) WHERE phone_hash IS NOT NULL;
CREATE INDEX ix_patients_nid   ON patients(national_id_hash) WHERE national_id_hash IS NOT NULL;
CREATE INDEX ix_patients_branch ON patients(branch_id, created_at DESC) WHERE deleted_at IS NULL;
CREATE INDEX ix_patients_birth ON patients(birth_date);

CREATE TABLE patient_allergies (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    patient_id uuid NOT NULL REFERENCES patients(id) ON DELETE CASCADE,
    substance text NOT NULL, reaction text,
    severity text NOT NULL DEFAULT 'moderate' CHECK (severity IN ('mild','moderate','severe','anaphylaxis')),
    noted_at date DEFAULT current_date, is_active boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(), row_version int NOT NULL DEFAULT 1
);
CREATE INDEX ix_allergies_patient ON patient_allergies(patient_id) WHERE is_active;

CREATE TABLE patient_conditions (          -- xəstəliklər (ICD-10)
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    patient_id uuid NOT NULL REFERENCES patients(id) ON DELETE CASCADE,
    icd10_code text, name text NOT NULL, since date, is_active boolean NOT NULL DEFAULT true, notes text,
    created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(), row_version int NOT NULL DEFAULT 1
);
CREATE INDEX ix_conditions_patient ON patient_conditions(patient_id) WHERE is_active;

CREATE TABLE patient_medications (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    patient_id uuid NOT NULL REFERENCES patients(id) ON DELETE CASCADE,
    name text NOT NULL, dose text, frequency text, started_on date, ended_on date,
    created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(), row_version int NOT NULL DEFAULT 1
);
CREATE INDEX ix_meds_patient ON patient_medications(patient_id);

CREATE TABLE anamnesis (                   -- versiyalı anamnez (hər dəyişiklik yeni sətir)
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    patient_id uuid NOT NULL REFERENCES patients(id) ON DELETE CASCADE,
    complaint text, history jsonb NOT NULL DEFAULT '{}'::jsonb,
    recorded_by uuid REFERENCES users(id), recorded_at timestamptz NOT NULL DEFAULT now(),
    created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(), row_version int NOT NULL DEFAULT 1
);
CREATE INDEX ix_anamnesis_patient ON anamnesis(patient_id, recorded_at DESC);

CREATE TABLE patient_family_links (
    patient_id uuid NOT NULL REFERENCES patients(id) ON DELETE CASCADE,
    related_patient_id uuid NOT NULL REFERENCES patients(id) ON DELETE CASCADE,
    relation text NOT NULL, is_guarantor boolean NOT NULL DEFAULT false,
    PRIMARY KEY (patient_id, related_patient_id), CHECK (patient_id <> related_patient_id)
);

CREATE TABLE insurance_companies (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    name text NOT NULL UNIQUE, contact jsonb, is_active boolean NOT NULL DEFAULT true
);
CREATE TABLE patient_insurance_policies (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    patient_id uuid NOT NULL REFERENCES patients(id) ON DELETE CASCADE,
    company_id uuid NOT NULL REFERENCES insurance_companies(id),
    policy_no text NOT NULL, coverage_percent numeric(5,2) CHECK (coverage_percent BETWEEN 0 AND 100),
    annual_limit numeric(14,2), valid_from date, valid_to date, is_primary boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(), row_version int NOT NULL DEFAULT 1
);
CREATE INDEX ix_policies_patient ON patient_insurance_policies(patient_id);

CREATE TABLE consent_templates (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    code text NOT NULL, version int NOT NULL, title text NOT NULL, body text NOT NULL,
    is_active boolean NOT NULL DEFAULT true, UNIQUE (code, version)
);
CREATE TABLE patient_consents (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    patient_id uuid NOT NULL REFERENCES patients(id) ON DELETE CASCADE,
    template_id uuid NOT NULL REFERENCES consent_templates(id),
    signed_at timestamptz, signature_key text,           -- S3 açarı (e-imza şəkli)
    signed_by_name text, document_hash bytea,            -- imzalanmış PDF hash-i
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_consents_patient ON patient_consents(patient_id);

-- Fayl metadata (rentgen, şəkil, PDF, analiz, STL, DICOM). Binar S3-dədir.
CREATE TABLE patient_documents (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    patient_id uuid NOT NULL REFERENCES patients(id) ON DELETE CASCADE,
    kind text NOT NULL CHECK (kind IN ('xray','cbct','mri','photo','model3d','lab_result','pdf','consent','other')),
    title text, s3_key text NOT NULL UNIQUE, mime_type text, size_bytes bigint, sha256 bytea,
    taken_at timestamptz, uploaded_by uuid REFERENCES users(id), meta jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(),
    deleted_at timestamptz, row_version int NOT NULL DEFAULT 1
);
CREATE INDEX ix_docs_patient ON patient_documents(patient_id, kind, taken_at DESC) WHERE deleted_at IS NULL;

SELECT attach_standard_triggers('patients');
SELECT attach_standard_triggers('patient_allergies');
SELECT attach_standard_triggers('patient_conditions');
SELECT attach_standard_triggers('patient_medications');
SELECT attach_standard_triggers('anamnesis');
SELECT attach_standard_triggers('patient_insurance_policies');
SELECT attach_standard_triggers('patient_documents');
