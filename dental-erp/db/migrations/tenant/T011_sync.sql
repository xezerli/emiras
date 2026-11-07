-- T011: Sync (Mərhələ 7, Dilim 7b).
--
-- 1) change_log.xid: dəyişikliyi yazan tranzaksiyanın id-si. seq ardıcıllığı COMMIT sırası ilə üst-üstə düşmür: iki paralel tranzaksiyada
--    kiçik seq-i alan sonra commit edə bilər. Klient böyük seq-ə qədər oxuyubsa o dəyişiklik həmişəlik itərdi.
--    Pull yalnız xid < pg_snapshot_xmin olan (yəni bütün köhnə tranzaksiyaları bitmiş) sətirləri qaytarır və kursor xid-dir.
ALTER TABLE change_log ADD COLUMN xid xid8 NOT NULL DEFAULT pg_current_xact_id();
CREATE INDEX ix_change_log_xid ON change_log(xid, seq);

-- 2) Təmizlənmiş jurnal sərhədi: kursoru bundan kiçik olan klient tam yenidən sinxron etməlidir (410)
CREATE TABLE sync_state (
    key   text PRIMARY KEY,
    value bigint NOT NULL
);

-- 3) Push idempotentliyi: eyni clientChangeId təkrar gələndə əvvəlki nəticə qaytarılır, effekt təkrarlanmır
CREATE TABLE sync_client_changes (
    client_change_id uuid PRIMARY KEY,
    device_id        uuid NOT NULL,
    user_id          uuid NOT NULL,
    entity           text NOT NULL,
    entity_id        uuid NOT NULL,
    outcome          text NOT NULL CHECK (outcome IN ('applied', 'conflict', 'rejected')),
    response         jsonb NOT NULL,
    created_at       timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_sync_client_changes_created ON sync_client_changes(created_at);
