#!/usr/bin/env python3
"""Генерирует src/config-new/DataProcessors/РМК_Yeni (метаданные + управляемая форма, тёмная тема)
на основе выгрузки РМК2. Запуск: python3 tools/gen_rmk_yeni.py <каталог выгрузки 'pos ekran'>"""
import os, re, sys, uuid
from xml.sax.saxutils import escape

SRC = sys.argv[1]
ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'src', 'config-new', 'DataProcessors')
NAME = 'РМК_Yeni'

def dec(s): return re.sub(r'#U([0-9a-fA-F]{4})', lambda m: chr(int(m.group(1), 16)), s)
def find(dirpath, name):
    for f in os.listdir(dirpath):
        if dec(f) == name: return os.path.join(dirpath, f)
    raise FileNotFoundError(name)

# ---------- 1. метаданные обработки ----------
dp_dir = os.path.join(SRC, 'DataProcessors')
src_xml = open(find(dp_dir, 'РМК2.xml'), encoding='utf-8-sig').read()
umap = {}
def newuuid(m):
    u = m.group(0)
    return umap.setdefault(u, str(uuid.uuid4()))
meta = re.sub(r'[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}', newuuid, src_xml)
meta = meta.replace('РМК2', NAME)
# убрать формы/макеты, оставить одну форму
meta = re.sub(r'\s*<Form>[^<]*</Form>', '', meta)
meta = re.sub(r'\s*<Template>[^<]*</Template>', '', meta)
head, sep, tail = meta.rpartition('</ChildObjects>')
meta = head + '\t<Form>Форма</Form>\n\t\t' + sep + tail
os.makedirs(ROOT, exist_ok=True)
open(os.path.join(ROOT, NAME + '.xml'), 'w', encoding='utf-8-sig').write(meta)

NS = ('xmlns:app="http://v8.1c.ru/8.2/managed-application/core" xmlns:cfg="http://v8.1c.ru/8.1/data/enterprise/current-config" '
      'xmlns:cmi="http://v8.1c.ru/8.2/managed-application/cmi" xmlns:ent="http://v8.1c.ru/8.1/data/enterprise" '
      'xmlns:lf="http://v8.1c.ru/8.2/managed-application/logform" xmlns:style="http://v8.1c.ru/8.1/data/ui/style" '
      'xmlns:sys="http://v8.1c.ru/8.1/data/ui/fonts/system" xmlns:v8="http://v8.1c.ru/8.1/data/core" '
      'xmlns:v8ui="http://v8.1c.ru/8.1/data/ui" xmlns:web="http://v8.1c.ru/8.1/data/ui/colors/web" '
      'xmlns:win="http://v8.1c.ru/8.1/data/ui/colors/windows" xmlns:xr="http://v8.1c.ru/8.3/xcf/readable" '
      'xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" version="2.11"')

# ---------- 2. метаданные формы ----------
fm = f'''<?xml version="1.0" encoding="UTF-8"?>
<MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" {NS.replace('xmlns:xr=', 'xmlns:xen="http://v8.1c.ru/8.3/xcf/enums" xmlns:xpr="http://v8.1c.ru/8.3/xcf/predef" xmlns:xr=')}>
	<Form uuid="{uuid.uuid4()}">
		<Properties>
			<Name>Форма</Name>
			<Synonym>
				<v8:item>
					<v8:lang>ru</v8:lang>
					<v8:content>РМК (новый дизайн)</v8:content>
				</v8:item>
				<v8:item>
					<v8:lang>az</v8:lang>
					<v8:content>RMK (yeni dizayn)</v8:content>
				</v8:item>
			</Synonym>
			<Comment/>
			<FormType>Managed</FormType>
			<IncludeHelpInContents>false</IncludeHelpInContents>
			<UsePurposes>
				<v8:Value xsi:type="app:ApplicationUsePurpose">PlatformApplication</v8:Value>
			</UsePurposes>
			<ExtendedPresentation/>
		</Properties>
	</Form>
</MetaDataObject>'''
fdir = os.path.join(ROOT, NAME, 'Forms')
os.makedirs(os.path.join(fdir, 'Форма', 'Ext', 'Form'), exist_ok=True)
open(os.path.join(fdir, 'Форма.xml'), 'w', encoding='utf-8-sig').write(fm)

# ---------- 3. Form.xml ----------
# Палитра (тёмная тема)
BG = '#10151C'; PANEL = '#18202A'; PANEL2 = '#202A36'; LINE = '#2B3746'
TXT = '#E8EEF5'; MUTED = '#8FA1B5'; ACCENT = '#3B82F6'; GREEN = '#22A55B'; RED = '#D64545'; AMBER = '#E0A030'
BTN = '#2A3544'

_id = [0]
def nid():
    _id[0] += 1
    return _id[0]

def ml(ru, az=None):
    az = az or ru
    return ('<v8:item><v8:lang>ru</v8:lang><v8:content>%s</v8:content></v8:item>'
            '<v8:item><v8:lang>az</v8:lang><v8:content>%s</v8:content></v8:item>') % (escape(ru), escape(az))

def font(h=10, bold=False):
    return '<Font ref="sys:DefaultGUIFont" height="%d" bold="%s" italic="false" underline="false" strikeout="false" kind="WindowsFont"/>' % (h, 'true' if bold else 'false')

def common_tail(name):
    return ('<ContextMenu name="%sКонтекстноеМеню" id="%d"/>'
            '<ExtendedTooltip name="%sРасширеннаяПодсказка" id="%d"/>') % (name, nid(), name, nid())

def group(name, direction, children, back=None, title=None, extra=''):
    i = nid()
    s = f'<UsualGroup name="{name}" id="{i}">'
    s += f'<Group>{direction}</Group><Behavior>Usual</Behavior><Representation>None</Representation><ShowTitle>false</ShowTitle>'
    if back: s += f'<BackColor>{back}</BackColor>'
    s += extra
    s += f'<ExtendedTooltip name="{name}РасширеннаяПодсказка" id="{nid()}"/>'
    s += '<ChildItems>' + ''.join(children) + '</ChildItems></UsualGroup>'
    return s

def label(name, ru, az=None, h=10, bold=False, color=TXT, width=None, hstretch=False, height=None):
    i = nid()
    s = f'<LabelDecoration name="{name}" id="{i}">'
    if width: s += f'<Width>{width}</Width>'
    if height: s += f'<Height>{height}</Height>'
    if hstretch: s += '<HorizontalStretch>true</HorizontalStretch>'
    s += f'<TextColor>{color}</TextColor>' + font(h, bold)
    s += f'<Title formatted="false">{ml(ru, az)}</Title>'
    s += common_tail(name) + '</LabelDecoration>'
    return s

def button(name, command, ru, az=None, back=BTN, color=TXT, h=10, bold=False, width=None, height=2):
    i = nid()
    s = f'<Button name="{name}" id="{i}"><Type>UsualButton</Type>'
    if width: s += f'<Width>{width}</Width>'
    s += f'<Height>{height}</Height>'
    s += f'<CommandName>Form.Command.{command}</CommandName>'
    s += f'<TextColor>{color}</TextColor><BackColor>{back}</BackColor><BorderColor>{LINE}</BorderColor>' + font(h, bold)
    s += f'<Title>{ml(ru, az)}</Title>'
    s += f'<ExtendedTooltip name="{name}РасширеннаяПодсказка" id="{nid()}"/></Button>'
    return s

def field(name, path, ru=None, az=None, ro=False, width=None, events=None, hstretch=False, color=TXT, back=PANEL2, h=10, textedit=True):
    i = nid()
    s = f'<InputField name="{name}" id="{i}"><DataPath>{path}</DataPath>'
    if ro: s += '<ReadOnly>true</ReadOnly>'
    s += '<EditMode>EnterOnInput</EditMode>'
    if ru: s += f'<Title>{ml(ru, az)}</Title>'
    if width: s += f'<Width>{width}</Width>'
    if hstretch: s += '<HorizontalStretch>true</HorizontalStretch>'
    s += f'<TextColor>{color}</TextColor><BackColor>{back}</BackColor>' + font(h)
    s += common_tail(name)
    if events:
        s += '<Events>' + ''.join(f'<Event name="{e}">{h_}</Event>' for e, h_ in events) + '</Events>'
    s += '</InputField>'
    return s

def table(name, path, columns, events=None, rep='List', extra='', hstretch=True, vstretch=True, rowfont=10):
    i = nid()
    s = f'<Table name="{name}" id="{i}"><Representation>{rep}</Representation><CommandBarLocation>None</CommandBarLocation>'
    s += '<UseAlternationRowColor>true</UseAlternationRowColor>' + extra
    s += f'<DataPath>{path}</DataPath>'
    s += f'<TextColor>{TXT}</TextColor><BackColor>{PANEL}</BackColor><BorderColor>{LINE}</BorderColor>' + font(rowfont)
    if hstretch: s += '<HorizontalStretch>true</HorizontalStretch>'
    if vstretch: s += '<VerticalStretch>true</VerticalStretch>'
    s += f'<ContextMenu name="{name}КонтекстноеМеню" id="{nid()}"/>'
    s += f'<AutoCommandBar name="{name}КоманднаяПанель" id="{nid()}"><Autofill>false</Autofill></AutoCommandBar>'
    s += f'<ExtendedTooltip name="{name}РасширеннаяПодсказка" id="{nid()}"/>'
    if events:
        s += '<Events>' + ''.join(f'<Event name="{e}">{h_}</Event>' for e, h_ in events) + '</Events>'
    s += '<ChildItems>' + ''.join(columns) + '</ChildItems></Table>'
    return s

def col(name, path, ru, az=None, ro=True, width=None, events=None, hstretch=False, label_=False):
    i = nid()
    tag = 'LabelField' if label_ else 'InputField'
    s = f'<{tag} name="{name}" id="{i}"><DataPath>{path}</DataPath>'
    if ro and tag == 'InputField': s += '<ReadOnly>true</ReadOnly>'
    s += f'<Title>{ml(ru, az)}</Title>'
    if width: s += f'<Width>{width}</Width>'
    if hstretch: s += '<HorizontalStretch>true</HorizontalStretch>'
    s += common_tail(name)
    if events and tag == 'InputField':
        s += '<Events>' + ''.join(f'<Event name="{e}">{h_}</Event>' for e, h_ in events) + '</Events>'
    s += f'</{tag}>'
    return s

# --- Верхняя панель ---
top = group('ГруппаШапка', 'Horizontal', [
    label('ДекорЗаголовок', 'РМК', 'RMK', h=14, bold=True, color=ACCENT, width=6),
    label('ДекорПользователь', '', '', h=10, color=MUTED, hstretch=True),
    button('кнНовыйЧек', 'НовыйЧек', 'Новый чек (F2)', 'Yeni çek (F2)', width=16),
    button('кнАктивныеЧеки', 'АктивныеЧеки', 'Отложенные (F3)', 'Aktiv çeklər (F3)', width=16),
    button('кнЖурнал', 'Журнал', 'Журнал', 'Jurnal', width=10),
    button('кнЗедОтчет', 'ЗедОтчет', 'Z-отчёт', 'Z-hesabat', width=10),
    button('кнЗакрытьСмену', 'ЗакрытьСмену', 'Закрыть смену', 'Növbəni bağla', width=14),
    button('кнСинхронизация', 'Синхронизация', 'Синхронизация', 'Sinxronlaşdır', width=14),
    button('кнЗакрыть', 'Закрыть', 'Закрыть', 'Bağla', back=RED, width=10),
], back=PANEL)

# --- Левая колонка: поиск + группы + каталог ---
left = group('ГруппаЛево', 'Vertical', [
    group('ГруппаПоиск', 'Horizontal', [
        field('ПоискСтрока', 'ПоискСтрока', 'Поиск / штрихкод', 'Axtarış / ştrixkod', hstretch=True, h=12,
              events=[('OnChange', 'ПоискСтрокаПриИзменении')]),
        button('кнВсеТовары', 'ВсеТовары', 'Все', 'Hamısı', width=8),
    ], back=PANEL),
    group('ГруппаКаталог', 'Horizontal', [
        table('ГруппыТоваров', 'ГруппыТоваров', [
            col('ГруппыТоваровНаименование', 'ГруппыТоваров.Description', 'Группы', 'Qruplar', width=22, hstretch=True),
        ], events=[('OnActivateRow', 'ГруппыТоваровПриАктивизацииСтроки')], rep='Tree',
           extra='<InitialTreeView>ExpandTopLevel</InitialTreeView>', hstretch=False),
        table('КаталогТоваров', 'КаталогТоваров', [
            col('КаталогТоваровКод', 'КаталогТоваров.Код', 'Код', 'Kod', width=8),
            col('КаталогТоваровАртикул', 'КаталогТоваров.Артикул', 'Артикул', 'Artikul', width=12),
            col('КаталогТоваровНаименование', 'КаталогТоваров.Наименование', 'Наименование', 'Ad', width=30, hstretch=True),
            col('КаталогТоваровЦена', 'КаталогТоваров.Цена', 'Цена', 'Qiymət', width=9),
            col('КаталогТоваровОстаток', 'КаталогТоваров.Остаток', 'Остаток', 'Qalıq', width=9),
        ], events=[('Selection', 'КаталогТоваровВыбор')]),
    ], back=BG),
    group('ГруппаКнопкиКаталог', 'Horizontal', [
        button('кнДобавитьТовар', 'ДобавитьТовар', 'Добавить (Enter)', 'Əlavə et (Enter)', back=ACCENT, bold=True, width=18),
        button('кнДобавитьСКоличеством', 'ДобавитьТоварСКоличеством', 'Добавить с кол-вом…', 'Miqdarla əlavə et…', width=18),
    ], back=PANEL),
], back=BG, extra='<Width>70</Width>')

# --- Центр: чек ---
center = group('ГруппаЦентр', 'Vertical', [
    group('ГруппаЧекШапка', 'Horizontal', [
        label('ДекорЧек', 'Чек №1', 'Çek №1', h=14, bold=True, color=TXT, hstretch=True),
        label('ДекорКонтрагент', '', '', h=10, color=AMBER, hstretch=True),
    ], back=PANEL),
    table('Товары', 'Объект.Товары', [
        col('ТоварыНомерСтроки', 'Объект.Товары.LineNumber', '№', '№', width=4, label_=True),
        col('ТоварыТовар', 'Объект.Товары.Товар', 'Товар', 'Mal', width=34, hstretch=True),
        col('ТоварыЕдиница', 'Объект.Товары.Единица', 'Ед.', 'Ölçü vahidi', ro=False, width=8,
            events=[('OnChange', 'ТоварыЕдиницаПриИзменении')]),
        col('ТоварыКоличество', 'Объект.Товары.Количество', 'Кол-во', 'Miqdar', ro=False, width=8,
            events=[('OnChange', 'ТоварыКоличествоПриИзменении')]),
        col('ТоварыЦена', 'Объект.Товары.Цена', 'Цена', 'Qiymət', ro=False, width=9,
            events=[('OnChange', 'ТоварыЦенаПриИзменении')]),
        col('ТоварыПроцентСкидки', 'Объект.Товары.ПроцентСкидки', '%', 'A%', ro=False, width=6,
            events=[('OnChange', 'ТоварыПроцентСкидкиПриИзменении')]),
        col('ТоварыСкидка', 'Объект.Товары.Скидка', 'Скидка', 'Endirim', width=8),
        col('ТоварыСумма', 'Объект.Товары.Сумма', 'Сумма', 'Məbləğ', width=10),
        col('ТоварыОстаток', 'Объект.Товары.Остаток', 'Остаток', 'Qalıq', width=8),
    ], events=[('BeforeRowChange', 'ТоварыПередНачаломИзменения'),
               ('BeforeDeleteRow', 'ТоварыПередУдалением'),
               ('AfterDeleteRow', 'ТоварыПослеУдаления')], rowfont=12),
    group('ГруппаКнопкиЧек', 'Horizontal', [
        button('кнИзменитьКоличество', 'ИзменитьКоличество', 'Кол-во…', 'Miqdar…', width=12),
        button('кнУдалитьСтроку', 'УдалитьСтроку', 'Удалить строку', 'Sətri sil', width=14),
        button('кнОчиститьЧек', 'ОчиститьЧек', 'Очистить чек', 'Çeki sil', back=RED, width=14),
    ], back=PANEL),
], back=BG)

# --- Правая колонка: итоги и действия ---
right = group('ГруппаПраво', 'Vertical', [
    label('ДекорИтогЗаголовок', 'К оплате', 'Cəmi', h=11, color=MUTED),
    label('ДекорИтог', '0,00', '0,00', h=34, bold=True, color=GREEN, hstretch=True, height=2),
    label('ДекорИтогИнфо', '', '', h=9, color=MUTED, hstretch=True, height=2),
    label('ДекорСотрудник', '', '', h=10, color=TXT, hstretch=True),
    button('кнСотрудник', 'Сотрудник', 'Продавец (F6)', 'Satıcı (F6)', width=24),
    button('кнКонтрагент', 'Контрагент', 'Покупатель (F5)', 'Alıcı (F5)', width=24),
    button('кнКонтрагентОчистить', 'КонтрагентОчистить', 'Сбросить покупателя', 'Alıcını təmizlə', width=24),
    button('кнСкидка', 'Скидка', 'Скидка % (F4)', 'Endirim % (F4)', width=24),
    button('кнСкидкаСброс', 'СкидкаСброс', 'Снять скидку', 'Endirimi ləğv et', width=24),
    button('кнВозврат', 'ВозвратЧек', 'ВОЗВРАТ (F8)', 'QAYTARMA (F8)', back=RED, bold=True, h=12, width=24, height=3),
    button('кнПродажа', 'Продажа', 'ОПЛАТА (F9)', 'ÖDƏNİŞ (F9)', back=GREEN, bold=True, h=16, width=24, height=5),
], back=PANEL, extra='<Width>28</Width>')

body = group('ГруппаТело', 'Horizontal', [left, center, right], back=BG)
child_items = '<ChildItems>' + top + body + '</ChildItems>'

# --- Реквизиты ---
attrs = '''<Attributes>
<Attribute name="Объект" id="1"><Type><v8:Type>cfg:DataProcessorObject.%(n)s</v8:Type></Type><MainAttribute>true</MainAttribute></Attribute>
<Attribute name="ПоискСтрока" id="2"><Type><v8:Type>xs:string</v8:Type><v8:StringQualifiers><v8:Length>100</v8:Length><v8:AllowedLength>Variable</v8:AllowedLength></v8:StringQualifiers></Type></Attribute>
<Attribute name="ПечатьЧек" id="3"><Type><v8:Type>xs:decimal</v8:Type><v8:NumberQualifiers><v8:Digits>1</v8:Digits><v8:FractionDigits>0</v8:FractionDigits><v8:AllowedSign>Nonnegative</v8:AllowedSign></v8:NumberQualifiers></Type></Attribute>
<Attribute name="ГруппыТоваров" id="4"><Type><v8:Type>cfg:DynamicList</v8:Type></Type><Settings xsi:type="DynamicList"><ManualQuery>false</ManualQuery><DynamicDataRead>true</DynamicDataRead><MainTable>Catalog.Номенклатура</MainTable></Settings></Attribute>
<Attribute name="КаталогТоваров" id="5"><Type><v8:Type>cfg:DynamicList</v8:Type></Type><Settings xsi:type="DynamicList"><ManualQuery>true</ManualQuery><DynamicDataRead>true</DynamicDataRead><QueryText>%(q)s</QueryText><MainTable>Catalog.Номенклатура</MainTable></Settings></Attribute>
</Attributes>'''

QUERY = '''ВЫБРАТЬ
	Н.Ссылка КАК Ссылка,
	Н.Код КАК Код,
	Н.Артикул КАК Артикул,
	Н.Наименование КАК Наименование,
	ВЫРАЗИТЬ(ЕСТЬNULL(Ц.Цена, 0) * ЕСТЬNULL(К.Курс, 1) КАК ЧИСЛО(15, 2)) КАК Цена,
	ЕСТЬNULL(О.КоличествоОстаток, 0) КАК Остаток
ИЗ
	Справочник.Номенклатура КАК Н
		ЛЕВОЕ СОЕДИНЕНИЕ РегистрСведений.ЦеныНоменклатуры.СрезПоследних(&Дата, ТипЦен = &ТипЦен) КАК Ц
		ПО Ц.Номенклатура = Н.Ссылка
			И Ц.Единица = Н.ОсновнаяЕдиница
		ЛЕВОЕ СОЕДИНЕНИЕ РегистрСведений.КурсыВалют.СрезПоследних(&Дата, ) КАК К
		ПО К.Валюта = Ц.Валюта
		ЛЕВОЕ СОЕДИНЕНИЕ РегистрНакопления.ТоварыНаСкладах.Остатки(&Дата, Склад = &Склад) КАК О
		ПО О.Товар = Н.Ссылка
ГДЕ
	НЕ Н.ЭтоГруппа
	И (&БезГруппы
		ИЛИ Н.Ссылка В ИЕРАРХИИ (&Группа))
	И (&Поиск = ""
		ИЛИ Н.Наименование ПОДОБНО &ПоискШаблон
		ИЛИ Н.Артикул ПОДОБНО &ПоискШаблон
		ИЛИ Н.Штрихкод = &Поиск)'''

cmds = [
    ('НовыйЧек', 'Новый чек', 'Yeni çek', 'НовыйЧекНажатие', 'F2'),
    ('АктивныеЧеки', 'Отложенные чеки', 'Aktiv çeklər', 'АктивныеЧекиНажатие', 'F3'),
    ('ОчиститьЧек', 'Очистить чек', 'Çeki sil', 'ОчиститьЧекНажатие', None),
    ('Журнал', 'Журнал', 'Jurnal', 'ЖурналНажатие', None),
    ('ЗедОтчет', 'Z-отчёт', 'Z-hesabat', 'ЗедОтчетНажатие', None),
    ('ЗакрытьСмену', 'Закрыть смену', 'Növbəni bağla', 'ЗакрытьСменуНажатие', None),
    ('Синхронизация', 'Синхронизация', 'Sinxronlaşdır', 'СинхронизацияНажатие', None),
    ('Закрыть', 'Закрыть', 'Bağla', 'ЗакрытьНажатие', None),
    ('ВсеТовары', 'Все товары', 'Bütün mallar', 'ВсеТоварыНажатие', None),
    ('ДобавитьТовар', 'Добавить товар', 'Mal əlavə et', 'ДобавитьТоварНажатие', None),
    ('ДобавитьТоварСКоличеством', 'Добавить с количеством', 'Miqdarla əlavə et', 'ДобавитьТоварСКоличествомНажатие', None),
    ('УдалитьСтроку', 'Удалить строку', 'Sətri sil', 'УдалитьСтрокуНажатие', None),
    ('ИзменитьКоличество', 'Изменить количество', 'Miqdarı dəyiş', 'ИзменитьКоличествоНажатие', None),
    ('Скидка', 'Скидка', 'Endirim', 'СкидкаНажатие', 'F4'),
    ('СкидкаСброс', 'Снять скидку', 'Endirimi ləğv et', 'СкидкаСбросНажатие', None),
    ('Контрагент', 'Покупатель', 'Alıcı', 'КонтрагентНажатие', 'F5'),
    ('КонтрагентОчистить', 'Сбросить покупателя', 'Alıcını təmizlə', 'КонтрагентОчиститьНажатие', None),
    ('Сотрудник', 'Продавец', 'Satıcı', 'СотрудникНажатие', 'F6'),
    ('ВозвратЧек', 'Возврат', 'Qaytarma', 'ВозвратНажатие', 'F8'),
    ('Продажа', 'Оплата', 'Ödəniş', 'ПродажаНажатие', 'F9'),
]
cmd_xml = '<Commands>'
for k, (n, ru, az, act, sc) in enumerate(cmds, 1):
    cmd_xml += f'<Command name="{n}" id="{k}"><Title>{ml(ru, az)}</Title><Action>{act}</Action>'
    if sc: cmd_xml += f'<Shortcut>{sc}</Shortcut>'
    cmd_xml += '</Command>'
cmd_xml += '</Commands>'

form = ('<?xml version="1.0" encoding="UTF-8"?>\n'
        f'<Form xmlns="http://v8.1c.ru/8.3/xcf/logform" {NS}>'
        f'<Title>{ml("РМК", "RMK")}</Title>'
        '<AutoTitle>false</AutoTitle><CommandBarLocation>None</CommandBarLocation>'
        f'<BackColor>{BG}</BackColor>'
        '<Events>'
        '<Event name="OnCreateAtServer">ПриСозданииНаСервере</Event>'
        '<Event name="BeforeOpen">ПередОткрытием</Event>'
        '<Event name="OnOpen">ПриОткрытии</Event>'
        '<Event name="BeforeClose">ПередЗакрытием</Event>'
        '<Event name="ExternalEvent">ВнешнееСобытие</Event>'
        '</Events>'
        + child_items
        + attrs % {'n': NAME, 'q': escape(QUERY)}
        + cmd_xml + '</Form>')
open(os.path.join(fdir, 'Форма', 'Ext', 'Form.xml'), 'w', encoding='utf-8-sig').write(form)

# ---------- 4. Модуль ----------
mod = open(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'rmk_yeni_module.bsl'), encoding='utf-8').read()
open(os.path.join(fdir, 'Форма', 'Ext', 'Form', 'Module.bsl'), 'w', encoding='utf-8-sig').write(mod)
print('OK ->', os.path.normpath(ROOT), '| ids:', _id[0])
