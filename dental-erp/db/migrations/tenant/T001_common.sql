-- T001: Tenant sxemi üçün ümumi funksiyalar. search_path = t_<tenant>, public olmalıdır.
CREATE EXTENSION IF NOT EXISTS pgcrypto;
CREATE EXTENSION IF NOT EXISTS pg_trgm;
CREATE EXTENSION IF NOT EXISTS btree_gist;

-- Optimistic concurrency + updated_at
CREATE FUNCTION trg_touch() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    NEW.updated_at  := now();
    NEW.row_version := COALESCE(OLD.row_version, 0) + 1;
    RETURN NEW;
END $$;

-- Sync üçün dəyişiklik jurnalı (monoton ardıcıllıq)
CREATE TABLE change_log (
    seq         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    entity      text   NOT NULL,
    entity_id   uuid   NOT NULL,
    op          char(1) NOT NULL CHECK (op IN ('I','U','D')),
    changed_at  timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_change_log_entity ON change_log(entity, entity_id);

CREATE FUNCTION trg_change_log() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO change_log(entity, entity_id, op)
    VALUES (TG_TABLE_NAME,
            (CASE WHEN TG_OP = 'DELETE' THEN OLD.id ELSE NEW.id END),
            left(TG_OP, 1));
    RETURN NULL;
END $$;

-- Transactional Outbox / Inbox (RabbitMQ üçün)
CREATE TABLE outbox_messages (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    type          text  NOT NULL,
    routing_key   text  NOT NULL,
    payload       jsonb NOT NULL,
    occurred_at   timestamptz NOT NULL DEFAULT now(),
    processed_at  timestamptz,
    attempts      int NOT NULL DEFAULT 0,
    last_error    text
);
CREATE INDEX ix_outbox_pending ON outbox_messages(occurred_at) WHERE processed_at IS NULL;

CREATE TABLE inbox_messages (
    message_id    uuid NOT NULL,
    consumer      text NOT NULL,
    received_at   timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (message_id, consumer)
);

-- Audit: append-only, aylıq partition, hash-chain (hash tətbiq qatında hesablanır)
CREATE TABLE audit_log (
    id          bigint GENERATED ALWAYS AS IDENTITY,
    occurred_at timestamptz NOT NULL DEFAULT now(),
    user_id     uuid,
    action      text  NOT NULL,                -- patient.read, invoice.refund ...
    entity      text,
    entity_id   uuid,
    ip          inet,
    device_id   uuid,
    before_data jsonb,
    after_data  jsonb,
    prev_hash   bytea,
    hash        bytea NOT NULL,
    PRIMARY KEY (id, occurred_at)
) PARTITION BY RANGE (occurred_at);
CREATE INDEX ix_audit_entity ON audit_log(entity, entity_id, occurred_at DESC);
CREATE INDEX ix_audit_user   ON audit_log(user_id, occurred_at DESC);

CREATE FUNCTION audit_create_partition(p_month date) RETURNS void LANGUAGE plpgsql AS $$
DECLARE
    s date := date_trunc('month', p_month)::date;
    e date := (date_trunc('month', p_month) + interval '1 month')::date;
    n text := format('audit_log_%s', to_char(s, 'YYYY_MM'));
BEGIN
    EXECUTE format('CREATE TABLE IF NOT EXISTS %I PARTITION OF audit_log FOR VALUES FROM (%L) TO (%L)', n, s, e);
END $$;

CREATE FUNCTION trg_audit_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'audit_log is append-only';
END $$;
CREATE TRIGGER audit_no_update BEFORE UPDATE OR DELETE ON audit_log
    FOR EACH ROW EXECUTE FUNCTION trg_audit_immutable();

SELECT audit_create_partition(current_date);
SELECT audit_create_partition((current_date + interval '1 month')::date);

-- Standart trigger-ləri cədvələ bağlayır (touch + sync change_log)
CREATE FUNCTION attach_standard_triggers(p_table regclass, p_sync boolean DEFAULT true) RETURNS void
LANGUAGE plpgsql AS $$
BEGIN
    EXECUTE format('CREATE TRIGGER trg_touch BEFORE UPDATE ON %s FOR EACH ROW EXECUTE FUNCTION trg_touch()', p_table);
    IF p_sync THEN
        EXECUTE format('CREATE TRIGGER trg_change_log AFTER INSERT OR UPDATE OR DELETE ON %s FOR EACH ROW EXECUTE FUNCTION trg_change_log()', p_table);
    END IF;
END $$;
