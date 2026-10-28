"""Собирает ИмпортСчетовФактур_ETaxes.epf: внешняя обработка импорта счетов-фактур (Copy_U) с встроенным
компонентом ETaxes (вход через ASAN без отдельных форм). Использование:
  python3 make_import_epf.py <Copy_U.epf> <ETaxes_Dima.epf> <выход.epf>"""
import sys, re, zlib, struct, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import epftool as t

copy_u, dima, out = sys.argv[1:4]
SRC = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'src')
rd = lambda p: open(os.path.join(SRC, p), 'rb').read().replace(b'\r\n', b'\n').replace(b'\n', b'\r\n')

cu = t.parse(open(copy_u, 'rb').read())
di = t.parse(open(dima, 'rb').read())

def find(c, *names):
    e = None
    for n in names:
        e = [x for x in (e['sub'] if e else c)['entries'] if x['name'] == n][0]
    return e

# --- 1. Компонент ETaxes (модуль объекта обработки Dima) с исправлениями ---
D = find(di, '87f91173-a3df-458d-bded-66c4db3d1335.0', 'text')['data']
def rep(data, old, new):
    old, new = old.encode(), new.encode()
    assert data.count(old) == 1, old[:60]
    return data.replace(old, new)

D = rep(D, '\tЕсли HTTPОтвет = Неопределено Тогда\r\n\t\tТекстОшибкиПортала = "";\r\n\t\tВозврат;\r\n\tКонецЕсли;',
           '\tЕсли HTTPОтвет = Неопределено Тогда\r\n\t\t// Не затираем текст исключения, записанный в ВыполнитьHTTPЗапрос\r\n\t\tВозврат;\r\n\tКонецЕсли;')
D = rep(D, 'HTTPСоединение(ИмяСервера, Порт, , , , 30, ЗащищенноеСоединение)', 'HTTPСоединение(ИмяСервера, Порт, , , , 120, ЗащищенноеСоединение)')
anchor = '\t\tПараметрыЗапроса.Заголовки.Вставить("Accept", "application/json, text/plain, */*");\r\n\tКонецЕсли;\r\n'
D = rep(D, anchor, anchor +
    '\r\n\t// Портал может не отвечать на стандартный User-Agent платформы 1С\r\n'
    '\tЕсли Не ЗначениеЗаполнено(ЗаголовокHTTPБезУчетаРегистра(ПараметрыЗапроса.Заголовки, "User-Agent")) Тогда\r\n'
    '\t\tПараметрыЗапроса.Заголовки.Вставить("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/155.0.0.0 Safari/537.36");\r\n'
    '\tКонецЕсли;\r\n'
    '\tПараметрыЗапроса.Заголовки.Вставить("Origin", "https://" + ИмяСервера);\r\n'
    '\tПараметрыЗапроса.Заголовки.Вставить("Referer", "https://" + ИмяСервера + "/eportal/login/asan");\r\n'
    '\tПараметрыЗапроса.Заголовки.Вставить("Accept-Language", "az,ru;q=0.9,en;q=0.8");\r\n')

BOM = b'\xef\xbb\xbf'
D = D[len(BOM):] if D.startswith(BOM) else D
perem = 'Перем ТокенСессии;\r\nПерем ТекстОшибкиПортала;\r\n'.encode()
assert perem in D
D_body = D.replace(perem, b'')                      # объявления переменных поднимем в начало модуля

# --- 2. Модуль объекта Copy_U ---
obj = find(cu, '0397da0c-7fab-4c24-b38d-40b3899ec8f0.0', 'text')
C = obj['data']
C = C[len(BOM):] if C.startswith(BOM) else C
PREFIX = 'Обработки.ВзаимодействиеСПорталомETaxes.'.encode()
C = C.replace(PREFIX, b'').replace('ПолучитьТелоКакСтроку()'.encode(), 'ПолучитьТелоКакСтроку("UTF-8")'.encode())
obj['data'] = (BOM + perem + b'\r\n' + C.rstrip(b'\r\n') + b'\r\n\r\n'
               + '// ===== Компонент ETaxes (авторизация и HTTP-запросы к new.e-taxes.gov.az) =====\r\n'.encode()
               + D_body.rstrip(b'\r\n') + b'\r\n' + rd('ИмпортСчетовФактур_ASAN_добавка.bsl'))

# --- 3. Модуль формы Copy_U ---
fm = find(cu, '4a61780f-44ce-4523-9766-d406c417f7d8.0', 'module')
F = fm['data'].replace(PREFIX, b'')
start = F.index('Процедура ЗапуститьПроцессАвторизации() Экспорт'.encode())
end = F.index('КонецПроцедуры'.encode(), start) + len('КонецПроцедуры'.encode())
new_proc = ('Процедура ЗапуститьПроцессАвторизации() Экспорт\r\n\r\n'
            '\tТокен = ВосстановитьЗначение("Обработка.ВзаимодействиеСПорталомETaxes.Токен");\r\n'
            '\tЕсли ЗначениеЗаполнено(Токен) И ПроверитьАктуальностьТокена(Токен) Тогда\r\n'
            '\t\tОбновитьСписокДокументов();\r\n\t\tВозврат;\r\n\tКонецЕсли;\r\n\r\n'
            '\tЕсли ВыполнитьВходЧерезASAN() Тогда\r\n'
            '\t\tСохранитьЗначение("Обработка.ВзаимодействиеСПорталомETaxes.Токен", Токен);\r\n'
            '\t\tОбновитьСписокДокументов();\r\n'
            '\tИначе\r\n'
            '\t\tТокен = "";\r\n'
            '\tКонецЕсли;\r\n\r\nКонецПроцедуры').encode()
fm['data'] = F[:start] + new_proc + F[end:]

# --- 3б. Товары счёта-фактуры загружаются при активизации строки, а не только по кнопке ---
F = fm['data']
s0 = F.index('Процедура ТаблицаДокументовПриАктивизацииСтроки(Элемент)'.encode())
e0 = F.index('КонецПроцедуры'.encode(), s0) + len('КонецПроцедуры'.encode())
act = ('Процедура ТаблицаДокументовПриАктивизацииСтроки(Элемент)\r\n\r\n'
       '\tТекущиеДанные = ЭлементыФормы.ТаблицаДокументов.ТекущиеДанные;\r\n\r\n'
       '\tЕсли ТекущиеДанные <> Неопределено Тогда\r\n'
       '\t\t// Товары счёта-фактуры загружаем при выборе строки, если они ещё не получены\r\n'
       '\t\tЕсли Товары.Найти(ТекущиеДанные.УникальныйИдентификатор, "УникальныйИдентификатор") = Неопределено Тогда\r\n'
       '\t\t\tЕсли ПолучитьСодержимоеСчетаФактуры(ТекущиеДанные) Тогда\r\n'
       '\t\t\t\tНайтиНоменклатуруТаблицыТоваров();\r\n'
       '\t\t\tКонецЕсли;\r\n'
       '\t\tКонецЕсли;\r\n'
       '\tКонецЕсли;\r\n\r\n'
       '\tОбновитьТаблицуТоваров();\r\n\r\n'
       'КонецПроцедуры').encode()
fm['data'] = F[:s0] + act + F[e0:]

# --- 4. Сборка: неизменённые элементы берём как есть ---
changed = [('0397da0c-7fab-4c24-b38d-40b3899ec8f0.0', 'text'), ('4a61780f-44ce-4523-9766-d406c417f7d8.0', 'module')]
def build(c, pre=()):
    ents = c['entries']; n = len(ents)
    pos = 16 + len(t.doc(b'\0' * (12 * n), 0x200)); toc = b''; blocks = b''
    for e in ents:
        p = pre + (e['name'],)
        if any(ch[:len(p)] == p for ch in changed):
            raw = build(e['sub'], p) if 'sub' in e else e['data']
            if e['comp']:
                co = zlib.compressobj(9, zlib.DEFLATED, -15); body = co.compress(raw) + co.flush()
            else:
                body = raw
        else:
            body = e['orig']
        h = t.doc(e['hdr'], len(e['hdr'])); b = t.doc(body, max(0x200, len(body)))
        toc += struct.pack('<III', pos, pos + len(h), 0x7fffffff); blocks += h + b; pos += len(h) + len(b)
    return c['head'] + t.doc(toc, 0x200 if len(toc) <= 0x200 else len(toc)) + blocks

res = build(cu)
open(out, 'wb').write(res)
chk = dict(t.tree(t.parse(res))); orig = dict(t.tree(cu))
print('сохранено', out, len(res), 'байт; элементов', len(chk))
