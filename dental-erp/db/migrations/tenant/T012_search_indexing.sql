-- T012: Axtarış indeksləşdirməsi (Mərhələ 7, Dilim 7c).
-- Hər indexer öz kursorunu saxlayır: change_log-un xid sərhədi (T011). Kursor yoxdursa indexer əvvəlcə tam doldurma (backfill) edir.
CREATE TABLE indexer_cursors (
    name       text PRIMARY KEY,
    cursor     bigint NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT now()
);
