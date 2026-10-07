-- P001: Platform (shared) sxem. Bütün tenant-lar üçün ortaq metadata.
-- İkinci müdafiə qatı: Row-Level Security (app.tenant_id session parametri ilə).
CREATE SCHEMA IF NOT EXISTS platform;
CREATE EXTENSION IF NOT EXISTS pgcrypto;
CREATE EXTENSION IF NOT EXISTS pg_trgm;
CREATE EXTENSION IF NOT EXISTS btree_gist;

CREATE TABLE platform.plans (
    code            text PRIMARY KEY,                       -- starter | pro | enterprise
    name            text NOT NULL,
    max_users       int,
    max_branches    int,
    features        jsonb NOT NULL DEFAULT '{}'::jsonb      -- feature flag-lar
);

CREATE TABLE platform.tenants (
    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    slug            text NOT NULL UNIQUE CHECK (slug ~ '^[a-z0-9][a-z0-9-]{1,40}$'),
    name            text NOT NULL,
    schema_name     text NOT NULL UNIQUE CHECK (schema_name ~ '^t_[a-z0-9_]{1,40}$'),
    isolation       text NOT NULL DEFAULT 'schema' CHECK (isolation IN ('schema','database')),
    deployment      text NOT NULL DEFAULT 'cloud'  CHECK (deployment IN ('cloud','local')),
    plan_code       text NOT NULL REFERENCES platform.plans(code),
    status          text NOT NULL DEFAULT 'active' CHECK (status IN ('trial','active','suspended','closed')),
    country         char(2) NOT NULL DEFAULT 'AZ',
    default_currency char(3) NOT NULL DEFAULT 'AZN',
    locale          text NOT NULL DEFAULT 'az',
    timezone        text NOT NULL DEFAULT 'Asia/Baku',
    schema_version  text,                                    -- tətbiq olunmuş son tenant migration
    created_at      timestamptz NOT NULL DEFAULT now(),
    deleted_at      timestamptz
);

CREATE TABLE platform.tenant_domains (
    domain          text PRIMARY KEY,
    tenant_id       uuid NOT NULL REFERENCES platform.tenants(id) ON DELETE CASCADE,
    is_primary      boolean NOT NULL DEFAULT false
);
CREATE INDEX ix_tenant_domains_tenant ON platform.tenant_domains(tenant_id);

CREATE TABLE platform.licenses (
    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id       uuid NOT NULL REFERENCES platform.tenants(id) ON DELETE CASCADE,
    hardware_fp     text,                                    -- local server üçün
    valid_from      timestamptz NOT NULL,
    valid_to        timestamptz NOT NULL,
    signed_token    text NOT NULL,                           -- imzalı JWT-lisenziya
    CHECK (valid_to > valid_from)
);
CREATE INDEX ix_licenses_tenant ON platform.licenses(tenant_id, valid_to DESC);

-- Login zamanı email -> tenant tapmaq üçün (email blind-index, açıq email saxlanmır)
CREATE TABLE platform.login_directory (
    email_hash      bytea NOT NULL,
    tenant_id       uuid  NOT NULL REFERENCES platform.tenants(id) ON DELETE CASCADE,
    PRIMARY KEY (email_hash, tenant_id)
);
CREATE INDEX ix_login_directory_tenant ON platform.login_directory(tenant_id);

-- RLS: login_directory yalnız cari tenant-ın sətirlərini göstərir, lookup üçün ayrıca rol
ALTER TABLE platform.login_directory ENABLE ROW LEVEL SECURITY;
ALTER TABLE platform.login_directory FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON platform.login_directory
    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

INSERT INTO platform.plans(code, name, max_users, max_branches, features) VALUES
 ('starter',    'Starter',    10, 1,    '{"ai":false,"imaging":false}'),
 ('pro',        'Pro',        50, 5,    '{"ai":true,"imaging":true}'),
 ('enterprise', 'Enterprise', NULL, NULL,'{"ai":true,"imaging":true,"dedicated_db":true}');
