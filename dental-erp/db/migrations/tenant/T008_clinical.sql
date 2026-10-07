-- T008: Clinical modulu (Mərhələ 7, Dilim 4)

-- 1) Prosedur kataloqu (CDT əsaslı, yerli adlarla). Klinika özününküləri əlavə edə bilər.
INSERT INTO procedure_codes(code, name, category, requires_tooth, default_duration_min) VALUES
 ('D0120', 'Periodik müayinə',                    'Diaqnostika',   false, 20),
 ('D0140', 'Təcili (ağrı) müayinəsi',             'Diaqnostika',   false, 20),
 ('D0220', 'Nöqtəvi rentgen',                     'Diaqnostika',   true,  10),
 ('D0330', 'Panoram rentgen',                     'Diaqnostika',   false, 15),
 ('D1110', 'Peşəkar diş təmizliyi',               'Profilaktika',  false, 45),
 ('D2140', 'Amalqam plomb (1 səth)',              'Terapiya',      true,  40),
 ('D2391', 'Kompozit plomb (1 səth)',             'Terapiya',      true,  40),
 ('D2392', 'Kompozit plomb (2 səth)',             'Terapiya',      true,  50),
 ('D2740', 'Keramik tac',                         'Ortopediya',    true,  60),
 ('D3310', 'Kanal müalicəsi (ön diş)',            'Endodontiya',   true,  60),
 ('D3320', 'Kanal müalicəsi (premolyar)',         'Endodontiya',   true,  75),
 ('D3330', 'Kanal müalicəsi (molyar)',            'Endodontiya',   true,  90),
 ('D4341', 'Periodontal kürətaj (kvadrant)',      'Periodontiya',  false, 45),
 ('D6010', 'İmplantın qoyulması',                 'İmplantologiya', true, 90),
 ('D7140', 'Dişin çıxarılması (sadə)',            'Cərrahiyyə',    true,  30),
 ('D7210', 'Dişin çıxarılması (cərrahi)',         'Cərrahiyyə',    true,  60),
 ('D8080', 'Ortodontik müalicə (braket sistemi)', 'Ortodontiya',   false, 60),
 ('D9110', 'Təcili ağrı müalicəsi',               'Terapiya',      true,  30);

-- 2) Resept: allergiya xəbərdarlığına baxmayaraq yazılıbsa səbəb saxlanılır
ALTER TABLE prescriptions ADD COLUMN override_reason text;
ALTER TABLE prescriptions ADD CONSTRAINT ck_rx_override CHECK (allergy_check_passed OR override_reason IS NOT NULL);

-- 3) Bir həkimin bir pasiyent üçün eyni anda yalnız bir açıq vizti ola bilər (yarışa qarşı DB qoruması)
CREATE UNIQUE INDEX ux_visits_one_open ON visits(patient_id, provider_id) WHERE status = 'open';

-- 4) Odontoqram: eyni diş+səth üçün yalnız bir AKTUAL qeyd (səth NULL = bütün diş)
CREATE UNIQUE INDEX ux_tooth_current_surface ON tooth_records(patient_id, tooth_fdi, COALESCE(surface, '')) WHERE superseded_at IS NULL;

-- 5) Plan bəndi üçün optimistic concurrency: eyni bəndi iki nəfər eyni anda "icra olundu" edə bilməz (ikiqat faktura riski)
ALTER TABLE treatment_plan_items
    ADD COLUMN row_version int NOT NULL DEFAULT 1,
    ADD COLUMN updated_at timestamptz NOT NULL DEFAULT now();
SELECT attach_standard_triggers('treatment_plan_items', false);
