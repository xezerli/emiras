@echo off
chcp 65001 >nul
rem РМК_Yeni.epf yaratmaq. Aşağıdakı 2 yolu öz kompüterinizə görə dəyişin.
set PLATFORM=C:\Program Files\1cv8\8.3.18.1208\bin\1cv8.exe
set BAZA=C:\Baza\Test

"%PLATFORM%" DESIGNER /F "%BAZA%" /LoadExternalDataProcessorOrExternalReportFromFiles "%~dp0РМК_Yeni.xml" "%~dp0РМК_Yeni.epf" /Out "%~dp0epf-log.txt"
type "%~dp0epf-log.txt"
echo.
if exist "%~dp0РМК_Yeni.epf" (echo HAZIRDIR: %~dp0РМК_Yeni.epf) else (echo XETA: epf yaranmadi, epf-log.txt faylini gonderin)
pause
