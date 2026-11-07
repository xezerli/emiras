-- T002: Filial, otaq, istifadəçi, RBAC, token, cihaz
CREATE TABLE branches (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    code text NOT NULL UNIQUE, name text NOT NULL,
    address jsonb, phone text, timezone text,
    is_active boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(),
    row_version int NOT NULL DEFAULT 1
);
CREATE TABLE rooms (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    branch_id uuid NOT NULL REFERENCES branches(id),
    name text NOT NULL, kind text NOT NULL DEFAULT 'operatory'
        CHECK (kind IN ('operatory','xray','lab','sterilization','consult')),
    is_active boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(),
    row_version int NOT NULL DEFAULT 1,
    UNIQUE (branch_id, name)
);

CREATE TABLE users (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    email_hash bytea NOT NULL UNIQUE,            -- blind index (HMAC-SHA256)
    email_enc bytea NOT NULL,                    -- AES-256-GCM
    password_hash text NOT NULL,                 -- Argon2id
    full_name text NOT NULL,
    specialty text CHECK (specialty IN ('general','orthodontist','surgeon','implantologist','endodontist','pediatric','hygienist')),
    license_no text,
    totp_secret_enc bytea, two_factor_enabled boolean NOT NULL DEFAULT false,
    allowed_ips cidr[] NOT NULL DEFAULT '{}',    -- IP restriction (boşdursa məhdudiyyət yoxdur)
    default_branch_id uuid REFERENCES branches(id),
    locale text NOT NULL DEFAULT 'az',
    status text NOT NULL DEFAULT 'active' CHECK (status IN ('active','locked','disabled')),
    failed_logins int NOT NULL DEFAULT 0, locked_until timestamptz,
    last_login_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(),
    deleted_at timestamptz, row_version int NOT NULL DEFAULT 1
);
CREATE INDEX ix_users_branch ON users(default_branch_id) WHERE deleted_at IS NULL;

CREATE TABLE permissions (
    code text PRIMARY KEY,                       -- resource:action
    description text
);
CREATE TABLE roles (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    code text NOT NULL UNIQUE, name text NOT NULL,
    is_system boolean NOT NULL DEFAULT false
);
CREATE TABLE role_permissions (
    role_id uuid NOT NULL REFERENCES roles(id) ON DELETE CASCADE,
    permission_code text NOT NULL REFERENCES permissions(code) ON DELETE CASCADE,
    scope text NOT NULL DEFAULT 'branch' CHECK (scope IN ('own','branch','tenant')),   -- ABAC
    max_amount numeric(14,2),                    -- məs. refund limiti
    PRIMARY KEY (role_id, permission_code)
);
CREATE TABLE user_roles (
    user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    role_id uuid NOT NULL REFERENCES roles(id),
    branch_id uuid REFERENCES branches(id),      -- NULL = bütün filiallar
    PRIMARY KEY (user_id, role_id)
);
CREATE INDEX ix_user_roles_user ON user_roles(user_id);

CREATE TABLE user_devices (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    name text, platform text, push_token text,
    trusted boolean NOT NULL DEFAULT false,
    last_seen_at timestamptz, created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_user_devices_user ON user_devices(user_id);

-- Refresh token rotation: family_id ilə reuse detection
CREATE TABLE refresh_tokens (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    device_id uuid REFERENCES user_devices(id) ON DELETE SET NULL,
    family_id uuid NOT NULL,
    token_hash bytea NOT NULL UNIQUE,            -- SHA-256, açıq token saxlanmır
    expires_at timestamptz NOT NULL,
    used_at timestamptz, revoked_at timestamptz, replaced_by uuid,
    ip inet, created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_refresh_user ON refresh_tokens(user_id) WHERE revoked_at IS NULL;
CREATE INDEX ix_refresh_family ON refresh_tokens(family_id);
CREATE INDEX ix_refresh_expiry ON refresh_tokens(expires_at);

SELECT attach_standard_triggers('branches');
SELECT attach_standard_triggers('rooms');
SELECT attach_standard_triggers('users', false);   -- sync olunmur (təhlükəsizlik)

-- Sistem rolları və əsas icazələr
INSERT INTO permissions(code, description) VALUES
 ('patient:read','Pasiyentə baxış'),('patient:write','Pasiyent yaratma/redaktə'),('patient:export','Pasiyent datası export'),
 ('appointment:read','Təqvimə baxış'),('appointment:write','Qəbul yaratma/dəyişmə'),
 ('clinical:read','Klinik qeydlərə baxış'),('clinical:write','Klinik qeyd/odontoqram'),('prescription:write','Resept'),
 ('invoice:read','Faktura baxış'),('invoice:write','Faktura yaratma'),('payment:write','Ödəniş qəbulu'),('invoice:refund','Geri qaytarma'),
 ('report:read','Hesabatlar'),('user:manage','İstifadəçi idarəsi'),('settings:manage','Tənzimləmələr'),('audit:read','Audit log');
INSERT INTO roles(code, name, is_system) VALUES
 ('director','Direktor',true),('manager','Menecer',true),('clinic_admin','Klinik Administrator',true),
 ('reception','Reception',true),('doctor','Həkim',true),('hygienist','Gigiyenist',true),
 ('lab','Laboratoriya',true),('radiology','Rentgen',true),('finance','Maliyyə',true),('cashier','Kassir',true);
INSERT INTO role_permissions(role_id, permission_code, scope)
SELECT r.id, p.code, 'tenant' FROM roles r CROSS JOIN permissions p WHERE r.code IN ('director','clinic_admin');
INSERT INTO role_permissions(role_id, permission_code, scope)
SELECT r.id, p.code, 'branch' FROM roles r JOIN permissions p ON p.code IN
 ('patient:read','patient:write','appointment:read','appointment:write','invoice:read','payment:write') WHERE r.code='reception';
INSERT INTO role_permissions(role_id, permission_code, scope)
SELECT r.id, p.code, 'own' FROM roles r JOIN permissions p ON p.code IN
 ('patient:read','appointment:read','clinical:read','clinical:write','prescription:write','invoice:read') WHERE r.code='doctor';
INSERT INTO role_permissions(role_id, permission_code, scope, max_amount)
SELECT r.id, p.code, 'branch', CASE WHEN p.code='invoice:refund' THEN 0 END FROM roles r JOIN permissions p ON p.code IN
 ('invoice:read','invoice:write','payment:write','invoice:refund') WHERE r.code='cashier';
