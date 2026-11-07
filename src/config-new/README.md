# РМК_Yeni — yeni dizaynlı kassa (idarə olunan forma, tünd tema)

`DataProcessors/РМК_Yeni*` — `РМК2`-nin əsasında yaradılmış ayrıca emal. Canlı `РМК2`-yə toxunulmur.
Yenidən generasiya: `python3 tools/gen_rmk_yeni.py "<dump qovluğu>"` (modul: `tools/rmk_yeni_module.bsl`).

## 1C-yə yükləmə (test bazasında!)
1. Dump qovluğunuza (Configuration.xml olan yer) `DataProcessors/РМК_Yeni.xml` və `DataProcessors/РМК_Yeni/` kopyalayın.
2. `Configuration.xml`:
   - `<ChildObjects>` daxilinə `<DataProcessor>РМК_Yeni</DataProcessor>` əlavə edin;
   - `<UseManagedFormInOrdinaryApplication>false</UseManagedFormInOrdinaryApplication>` → `true`.
3. Konfiqurator → Konfiqurasiya → «Загрузить конфигурацию из файлов…» → «Частичная загрузка» → `Configuration.xml`,
   `DataProcessors/РМК_Yeni.xml`, `DataProcessors/РМК_Yeni/Forms/Форма.xml`, `…/Forms/Форма/Ext/Form.xml`,
   `…/Forms/Форма/Ext/Form/Module.bsl` seçin.
4. Emalı interfeysə əlavə edin və ya Konfiquratorda «Открыть» ilə sınayın.

## Mərhələ 1-də var
Axtarış/ştrixkod/skaner, qrup ağacı və mal siyahısı (qiymət, qalıq), çek cədvəli, endirim, alıcı, satıcı,
satış (`Чек`, ödəniş `ФормаОплата` üzərindən), qaytarma, bir neçə çek (F2/F3), Z-hesabat, növbənin bağlanması.

## Hələ yoxdur
Hədiyyə və endirim kartları, aksiyalar («Компании»), sürətli seçim, ödəniş və çek çapı formalarının yeni dizaynı.

## Diqqət
Form.xml 1C olmadan yazılıb və platformada yoxlanmayıb. Yükləmə xəta versə, xəta mətnini göndərin.
