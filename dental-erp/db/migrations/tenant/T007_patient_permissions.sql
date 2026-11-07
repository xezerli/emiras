-- T007: Patient modulu üçün əlavə icazə. Tam (maskasız) telefon/email/FİN oxuma ayrıca icazədir və audit olunur.
INSERT INTO permissions(code, description) VALUES ('patient:read_sensitive', 'Maskasız telefon/email/FİN oxuma (audit olunur)');

-- Direktor və administrator: bütün tenant
INSERT INTO role_permissions(role_id, permission_code, scope)
SELECT id, 'patient:read_sensitive', 'tenant' FROM roles WHERE code IN ('director', 'clinic_admin');

-- Reception filial üzrə (zəng-mərkəzi/qəbul üçün nömrəni görməlidir), həkim yalnız öz pasiyentləri
INSERT INTO role_permissions(role_id, permission_code, scope)
SELECT id, 'patient:read_sensitive', 'branch' FROM roles WHERE code = 'reception';
INSERT INTO role_permissions(role_id, permission_code, scope)
SELECT id, 'patient:read_sensitive', 'own' FROM roles WHERE code = 'doctor';
