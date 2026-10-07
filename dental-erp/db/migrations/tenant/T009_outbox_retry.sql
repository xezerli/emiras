-- T009: Outbox publisher üçün exponential backoff (Mərhələ 7, Dilim 5)
ALTER TABLE outbox_messages ADD COLUMN next_attempt_at timestamptz NOT NULL DEFAULT now();
-- Göndərilməli mesajlar: yalnız hələ işlənməmiş və növbəti cəhd vaxtı gəlmiş sətirlər
DROP INDEX ix_outbox_pending;
CREATE INDEX ix_outbox_pending ON outbox_messages(next_attempt_at, occurred_at) WHERE processed_at IS NULL;
-- Təmizləmə job-u üçün
CREATE INDEX ix_outbox_processed ON outbox_messages(processed_at) WHERE processed_at IS NOT NULL;
