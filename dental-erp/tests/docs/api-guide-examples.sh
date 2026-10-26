#!/usr/bin/env bash
# docs/guides/api-guide.md-dəki curl nümunələrinin İCRA OLUNAN versiyası. Bələdçidəki hər `curl` əmri bu faylda sözbəsöz var
# (tests/docs/check-docs.py yoxlayır), bu skript isə onları real stack-ə qarşı işlədib status kodlarını və əsas sahələri təsdiqləyir.
# İstifadə:  infra/dev/run-stack.sh start  →  tests/docs/api-guide-examples.sh      (seed parolu: Passw0rd!e2e, tenant: demo)
set -euo pipefail
API="${API:-http://127.0.0.1:5100}"
TENANT="${TENANT:-demo}"

WORK="$(mktemp -d)"; trap 'rm -rf "$WORK"' EXIT
# `curl` sarğısı: gövdə $WORK/body-yə, status kodu $WORK/status-a yazılır. Bələdçidəki əmrlər sarğısız tam curl əmridir.
curl() { command curl -s -o "$WORK/body" -w "%{http_code}" "$@" > "$WORK/status"; }
status() { cat "$WORK/status"; }
field() { python3 -c 'import sys,json;d=json.load(open(sys.argv[1]));print(eval(sys.argv[2]))' "$WORK/body" "$1"; }
expect() { [[ "$2" == "$1" ]] || { echo "FAIL: $3 (gözlənilən $1, gələn $2)"; echo "--- gövdə:"; head -c 600 "$WORK/body"; echo; exit 1; }; echo "ok: $3"; }
uuid() { cat /proc/sys/kernel/random/uuid; }

# ---------- 1. Giriş ----------
curl -X POST "$API/v1/auth/login" -H "X-Tenant: $TENANT" -H "Content-Type: application/json" -d '{"email":"admin@demo.test","password":"Passw0rd!e2e"}'
expect 200 "$(status)" "login"
TOKEN="$(field 'd["accessToken"]')"; REFRESH="$(field 'd["refreshToken"]')"
expect 600 "$(field 'd["expiresIn"]')" "access token 600 san yaşayır"

curl -X POST "$API/v1/auth/login" -H "X-Tenant: $TENANT" -H "Content-Type: application/json" -d '{"email":"admin@demo.test","password":"yanlis-parol"}'
expect 401 "$(status)" "yanlış parol 401"
expect auth.invalid_credentials "$(field 'd["code"]')" "xəta kodu sabitdir"

curl "$API/v1/auth/me" -H "X-Tenant: $TENANT" -H "Authorization: Bearer $TOKEN"
expect 200 "$(status)" "auth/me"
BRANCH="$(field 'd["branches"][0]')"; ME_ID="$(field 'd["id"]')"

curl "$API/v1/patients" -H "X-Tenant: $TENANT"
expect 401 "$(status)" "tokensiz 401"

# ---------- 2. Token yeniləmə (rotasiya) ----------
curl -X POST "$API/v1/auth/refresh" -H "X-Tenant: $TENANT" -H "Content-Type: application/json" -d "{\"refreshToken\":\"$REFRESH\"}"
expect 200 "$(status)" "refresh"
NEW_REFRESH="$(field 'd["refreshToken"]')"; TOKEN="$(field 'd["accessToken"]')"
[[ "$NEW_REFRESH" != "$REFRESH" ]] && echo "ok: refresh token hər dəfə dəyişir"

# ---------- 3. Pasiyent ----------
PHONE="+99455$(shuf -i 1000000-9999999 -n 1)"
curl -X POST "$API/v1/patients" -H "X-Tenant: $TENANT" -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d "{\"branchId\":\"$BRANCH\",\"firstName\":\"Nigar\",\"lastName\":\"Quliyeva\",\"phone\":\"$PHONE\",\"birthDate\":\"1990-05-01\",\"gender\":\"F\"}"
expect 201 "$(status)" "pasiyent yaradıldı"
PATIENT="$(field 'd["id"]')"
expect 1 "$(field 'd["rowVersion"]')" "ilk rowVersion=1"
[[ "$(field 'd["phone"]')" == *"*"* ]] && echo "ok: telefon default maskalıdır"

curl -X POST "$API/v1/patients" -H "X-Tenant: $TENANT" -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d "{\"branchId\":\"$BRANCH\",\"firstName\":\"\",\"lastName\":\"Quliyeva\"}"
expect 422 "$(status)" "doğrulama xətası 422"
expect validation.failed "$(field 'd["code"]')" "validation.failed"

curl -X POST "$API/v1/patients" -H "X-Tenant: $TENANT" -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d "{\"branchId\":\"$BRANCH\",\"firstName\":\"Aysel\",\"lastName\":\"Quliyeva\",\"phone\":\"$PHONE\"}"
expect 409 "$(status)" "eyni telefon: dublikat xəbərdarlığı"
expect "$PATIENT" "$(field 'd["existingPatientId"]')" "409 mövcud pasiyentin id-sini göstərir"

curl -X POST "$API/v1/patients?confirmDuplicate=true" -H "X-Tenant: $TENANT" -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d "{\"branchId\":\"$BRANCH\",\"firstName\":\"Aysel\",\"lastName\":\"Quliyeva\",\"phone\":\"$PHONE\"}"
expect 201 "$(status)" "confirmDuplicate=true ilə ailə üzvü yaradıldı"

curl -D "$WORK/headers" "$API/v1/patients/$PATIENT" -H "X-Tenant: $TENANT" -H "Authorization: Bearer $TOKEN"
expect 200 "$(status)" "pasiyent kartı"
grep -qi '^etag: "1"' "$WORK/headers" && echo "ok: ETag rowVersion-dur"
grep -qi '^cache-control: no-store' "$WORK/headers" && echo "ok: PHI keşlənmir (no-store)"

curl -X PATCH "$API/v1/patients/$PATIENT" -H "X-Tenant: $TENANT" -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/merge-patch+json" -d '{"fatherName":"Elçin"}'
expect 428 "$(status)" "If-Match olmadan PATCH: 428"

curl -X PATCH "$API/v1/patients/$PATIENT" -H "X-Tenant: $TENANT" -H "Authorization: Bearer $TOKEN" -H "If-Match: \"99\"" -H "Content-Type: application/merge-patch+json" -d '{"fatherName":"Elçin"}'
expect 412 "$(status)" "köhnə If-Match: 412"

curl -X PATCH "$API/v1/patients/$PATIENT" -H "X-Tenant: $TENANT" -H "Authorization: Bearer $TOKEN" -H "If-Match: \"1\"" -H "Content-Type: application/merge-patch+json" -d '{"fatherName":"Elçin"}'
expect 200 "$(status)" "düzgün If-Match ilə yeniləndi"
expect 2 "$(field 'd["rowVersion"]')" "rowVersion artdı"

curl "$API/v1/patients/$PATIENT?reveal=true" -H "X-Tenant: $TENANT" -H "Authorization: Bearer $TOKEN"
expect 200 "$(status)" "reveal=true (patient:read_sensitive + audit)"
expect "$PHONE" "$(field 'd["phone"]')" "maskasız telefon"

curl "$API/v1/patients?limit=2" -H "X-Tenant: $TENANT" -H "Authorization: Bearer $TOKEN"
expect 200 "$(status)" "pasiyent siyahısı"
CURSOR="$(field 'd["nextCursor"]')"
curl "$API/v1/patients?limit=2&cursor=$CURSOR" -H "X-Tenant: $TENANT" -H "Authorization: Bearer $TOKEN"
expect 200 "$(status)" "kursorla növbəti səhifə"

curl "$API/v1/patients?q=Quliyeva" -H "X-Tenant: $TENANT" -H "Authorization: Bearer $TOKEN"
expect 200 "$(status)" "SQL axtarışı (?q=)"

curl "$API/v1/search/patients?q=Quliyeva" -H "X-Tenant: $TENANT" -H "Authorization: Bearer $TOKEN"
case "$(status)" in 200) echo "ok: Elasticsearch axtarışı" ;; 503) echo "ok: Elasticsearch yoxdur: 503 (klient SQL axtarışına keçməlidir)" ;; *) expect "200 və ya 503" "$(status)" "search/patients" ;; esac

# ---------- 4. Qəbul ----------
curl -X POST "$API/v1/auth/login" -H "X-Tenant: $TENANT" -H "Content-Type: application/json" -d '{"email":"doctor@demo.test","password":"Passw0rd!e2e"}'
DOCTOR_TOKEN="$(field 'd["accessToken"]')"
curl "$API/v1/auth/me" -H "X-Tenant: $TENANT" -H "Authorization: Bearer $DOCTOR_TOKEN"
DOCTOR="$(field 'd["id"]')"

TODAY="$(date -u +%F)"
curl "$API/v1/appointments/availability?providerId=$DOCTOR&date=$TODAY&durationMin=30" -H "X-Tenant: $TENANT" -H "Authorization: Bearer $TOKEN"
expect 200 "$(status)" "boş slotlar"
SLOT_START="$(field 'd[0]["start"] if d else ""')"; SLOT_END="$(field 'd[0]["end"] if d else ""')"
if [[ -z "$SLOT_START" ]]; then
  echo "skip: bu gün boş slot yoxdur (gecə yarısına yaxın); qəbul nümunələri atlandı"
else
  curl -X POST "$API/v1/appointments" -H "X-Tenant: $TENANT" -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d "{\"branchId\":\"$BRANCH\",\"patientId\":\"$PATIENT\",\"providerId\":\"$DOCTOR\",\"start\":\"$SLOT_START\",\"end\":\"$SLOT_END\",\"reason\":\"Yoxlama\"}"
  expect 201 "$(status)" "qəbul yaradıldı"
  APPT="$(field 'd["id"]')"
  expect booked "$(field 'd["status"]')" "status=booked"

  curl -X POST "$API/v1/appointments" -H "X-Tenant: $TENANT" -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d "{\"branchId\":\"$BRANCH\",\"patientId\":\"$PATIENT\",\"providerId\":\"$DOCTOR\",\"start\":\"$SLOT_START\",\"end\":\"$SLOT_END\"}"
  expect 409 "$(status)" "eyni həkim eyni vaxt: 409"
  expect appointment.overlap "$(field 'd["code"]')" "appointment.overlap"
  expect true "$(field 'str(len(d["alternativeSlots"]) > 0).lower()')" "alternativeSlots təklif olunur"

  curl -X POST "$API/v1/appointments/$APPT/check-in" -H "X-Tenant: $TENANT" -H "Authorization: Bearer $TOKEN"
  expect 200 "$(status)" "check-in (növbə bileti)"
  expect true "$(field 'str(d["ticketNo"] >= 1).lower()')" "növbə nömrəsi verildi"
fi

# ---------- 5. Faktura və ödəniş ----------
curl "$API/v1/services" -H "X-Tenant: $TENANT" -H "Authorization: Bearer $TOKEN"
expect 200 "$(status)" "xidmət qiymət siyahısı"
SERVICE="$(field '[s["id"] for s in d if s["code"]=="FILLING"][0]')"

curl -X POST "$API/v1/invoices" -H "X-Tenant: $TENANT" -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d "{\"kind\":\"invoice\",\"patientId\":\"$PATIENT\",\"branchId\":\"$BRANCH\",\"items\":[{\"serviceId\":\"$SERVICE\",\"quantity\":1}]}"
expect 201 "$(status)" "qaralama faktura"
INVOICE="$(field 'd["id"]')"
expect draft "$(field 'd["status"]')" "status=draft"

curl -X POST "$API/v1/invoices/$INVOICE/issue" -H "X-Tenant: $TENANT" -H "Authorization: Bearer $TOKEN"
expect 200 "$(status)" "faktura verildi"
expect issued "$(field 'd["status"]')" "status=issued"

IDEM="$(uuid)"
curl -X POST "$API/v1/invoices/$INVOICE/payments" -H "X-Tenant: $TENANT" -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -H "Idempotency-Key: $IDEM" -d '{"method":"card","amount":30}'
expect 201 "$(status)" "ödəniş qəbul edildi"
PAYMENT="$(field 'd["id"]')"
expect partially_paid "$(field 'd["invoiceStatus"]')" "faktura qismən ödənilib"

curl -X POST "$API/v1/invoices/$INVOICE/payments" -H "X-Tenant: $TENANT" -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -H "Idempotency-Key: $IDEM" -d '{"method":"card","amount":30}'
expect 201 "$(status)" "eyni açarla təkrar: eyni cavab"
expect "$PAYMENT" "$(field 'd["id"]')" "ikinci ödəniş YARANMADI (eyni payment id)"

curl -X POST "$API/v1/invoices/$INVOICE/payments" -H "X-Tenant: $TENANT" -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d '{"method":"card","amount":10}'
expect 400 "$(status)" "Idempotency-Key olmadan ödəniş: 400"
expect idempotency.key_required "$(field 'd["code"]')" "idempotency.key_required"

curl -X POST "$API/v1/invoices/$INVOICE/payments" -H "X-Tenant: $TENANT" -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -H "Idempotency-Key: $(uuid)" -d '{"method":"card","amount":999}'
expect 422 "$(status)" "qalıqdan çox ödəniş: 422"
expect billing.overpayment "$(field 'd["code"]')" "billing.overpayment"
expect 50.0 "$(field 'd["balance"]')" "xəta qalıq borcu bildirir"

curl "$API/v1/invoices/$INVOICE" -H "X-Tenant: $TENANT" -H "Authorization: Bearer $TOKEN"
expect 200 "$(status)" "faktura detalı"
expect 30.0 "$(field 'd["paidTotal"]')" "paidTotal=30"
expect 1 "$(field 'len(d["payments"])')" "tək ödəniş sətri"

# ---------- 6. İdarə paneli ----------
curl "$API/v1/dashboard/summary" -H "X-Tenant: $TENANT" -H "Authorization: Bearer $TOKEN"
expect 200 "$(status)" "dashboard"

# ---------- 7. İcazə: həkim maliyyə əməliyyatı edə bilməz ----------
curl -X POST "$API/v1/invoices/$INVOICE/payments" -H "X-Tenant: $TENANT" -H "Authorization: Bearer $DOCTOR_TOKEN" -H "Content-Type: application/json" -H "Idempotency-Key: $(uuid)" -d '{"method":"card","amount":5}'
expect 403 "$(status)" "icazəsiz rol: 403"

# ---------- 8. Çıxış ----------
curl -X POST "$API/v1/auth/logout" -H "X-Tenant: $TENANT" -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d "{\"refreshToken\":\"$NEW_REFRESH\"}"
expect 204 "$(status)" "logout (refresh token ləğv olundu)"
curl -X POST "$API/v1/auth/refresh" -H "X-Tenant: $TENANT" -H "Content-Type: application/json" -d "{\"refreshToken\":\"$NEW_REFRESH\"}"
expect 401 "$(status)" "ləğv olunmuş refresh token artıq işləmir"
echo "BÜTÜN NÜMUNƏLƏR OK"
