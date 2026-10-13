# AZ-API backend

1C emalı (`Az-api v8.2.epf`) ilə eyni API formatı. Emalda yalnız ünvan dəyişir:
`HTTPConnection = Новый HTTPСоединение("<sizin-server>", ...)`.

## İşə salmaq
    pip install -r requirements.txt
    AZAPI_DEMO=1 uvicorn app.main:app --port 8000     # demo rejim (telefonsuz)
    pytest

Mühit dəyişənləri: `AZAPI_ETAXES=demo|live` (default demo), `AZAPI_DEMO=1` (demo bank).
Emal HTTPS ilə qoşulur (`ЗащищенноеСоединениеOpenSSL`), ona görə önünə TLS proxy (nginx/Caddy) qoyun.

## Endpoint-lər
| Endpoint | Təyinat |
|---|---|
| `GET /api/1c/banks` | `{"banks":[{"name","url"}]}`: 1C siyahısı |
| `POST /api/1c/bank/{slug}` | bank çıxarışı (ASAN təsdiqi ilə çox addımlı) |
| `POST /api/tax/list` | e-qaimələr |
| `POST /api/1c/module/8.2/update` | emalın öz-özünə yenilənməsi (hələ `updatable:false`) |

Çox addımlı protokol: server `retry:true, code, pw` qaytarır, 1C 5 san gözləyib eyni sorğunu
`pw` ilə təkrarlayır, təsdiq gələndən sonra `success:true, data:[...]`.

## Yeni bank əlavə etmək
`app/connectors/banks/` altında yeni fayl (nümunə: `demo_bank.py`): `BankConnector`-dan miras al,
`name`, `slug` yaz, `fetch` metodunu doldur, `@register_bank` qoy. Başqa heç nə lazım deyil, bank
1C siyahısında avtomatik görünür.

`data` sətirlərinin açarları 1C-dəki `bank_ops` cədvəlinin sütun adlarıdır. Demo bankda fərz
olunub: c1 bank, c2 IBAN, c3 valyuta, c4 tarix (`yyyy-MM-ddTHH:mm:ss.000`), c5 referans, c6 VÖEN,
c7 ad, c8 qarşı hesab, c9 mədaxil, c10 məxaric, c11 təyinat, c12 ilkin qalıq, c13 son qalıq.
**Bunu emalın konfiquratorunda (Реквизиты -> bank_ops) yoxlayıb uyğunlaşdırın.**

## e-qaimə sütunları (`app/etaxes.py: to_rows`)
Emalın oxuduğu açarlar: c1 id, c4 status, c5/c6 satıcı VÖEN/ad, c7/c8 alıcı VÖEN/ad, c9..c12 tarixlər
(c12 qaimə tarixi), c13 seriya+nömrə, c27 kod, c28 ad, c29 ölçü vahidi, c30 say, c35 məbləğ,
c40 ƏDV, c41 digər vergi, c43 barkod, c44 `mal`|`xidmət`.

## Canlı e-taxes (QALAN İŞ)
`LiveETaxes` hələ boşdur. Real portal çağırışları (ASAN giriş, sertifikat seçimi, qaimə axtarışı)
sənədləşdirilmiş ictimai API deyil, brauzerin şəbəkə sorğularından çıxarılmalıdır
(DevTools -> Network -> "Save all as HAR", həssas başlıqları silin). Bank plaginləri üçün də eynisi.

## Deploy (HTTPS)
    AZAPI_DOMAIN=api.sizin-domen.az docker compose up -d --build
Domenin A qeydi serverin IP-sinə yönəlməlidir. Sonra 1C emalında `HTTPСоединение("api.sizin-domen.az", ...)`.
Qeyd: sessiyalar yaddaşdadır, ona görə yalnız bir `api` instansı işlədin.
