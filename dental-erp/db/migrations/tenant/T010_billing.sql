-- T010: Billing (Mərhələ 7, Dilim 5b). Pul qaydalarının DB səviyyəsində təminatı: tətbiq qatı səhv etsə də məlumat pozulmasın.

-- Artıq ödəniş mümkün deyil (paralel iki ödənişdə də son müdafiə xətti)
ALTER TABLE invoices ADD CONSTRAINT ck_invoices_no_overpay CHECK (paid_total <= total - insurance_amount);
ALTER TABLE invoice_items ADD CONSTRAINT ck_item_discount CHECK (discount <= quantity * unit_price);

-- Hər vizit üçün ən çox bir qaralama faktura: ProcedurePerformed consumer-lərinin paralel işləməsi iki qaralama yaratmasın
CREATE UNIQUE INDEX ux_invoices_one_draft_per_visit ON invoices(visit_id)
    WHERE kind = 'invoice' AND status = 'draft' AND visit_id IS NOT NULL;
-- Eyni plan bəndi iki dəfə fakturalanmasın
CREATE UNIQUE INDEX ux_invoice_items_plan_item ON invoice_items(plan_item_id) WHERE plan_item_id IS NOT NULL;

-- Geri qaytarma həmişə konkret ödənişə bağlıdır və əksinə
ALTER TABLE payments ADD CONSTRAINT ck_payments_refund_link CHECK ((kind = 'refund') = (refund_of IS NOT NULL));
CREATE INDEX ix_payments_refund_of ON payments(refund_of) WHERE refund_of IS NOT NULL;

-- Rəsmiləşdirilmiş (draft olmayan) fakturanın sətirləri dəyişməz
CREATE FUNCTION trg_invoice_items_draft_only() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE inv uuid := COALESCE(NEW.invoice_id, OLD.invoice_id);
BEGIN
    IF (SELECT status FROM invoices WHERE id = inv) <> 'draft' THEN
        RAISE EXCEPTION 'invoice items are immutable once the invoice is issued';
    END IF;
    RETURN COALESCE(NEW, OLD);
END $$;
CREATE TRIGGER invoice_items_draft_only BEFORE INSERT OR UPDATE OR DELETE ON invoice_items
    FOR EACH ROW EXECUTE FUNCTION trg_invoice_items_draft_only();
