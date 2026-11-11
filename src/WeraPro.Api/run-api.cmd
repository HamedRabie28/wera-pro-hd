@echo off
rem ─────────────────────────────────────────────────────────────────────────────
rem  run-api.cmd — مشغل خادم WERA PRO HD API (إنتاج Windows)
rem  يعمل بجانب WeraPro.Api.exe في مجلد النشر. عند أول تشغيل:
rem    1) يولّد مفتاح توقيع JWT عشوائياً (48 محرفاً) ويحفظه محلياً في
rem       %LOCALAPPDATA%\WERA_PRO_HD\api-signing.key — لا يُوزّع أبداً مع الحزمة؛
rem    2) يضبط ASPNETCORE_URLS وفق appsettings.json (الافتراضي http://localhost:5080)؛
rem    3) يشغّل الخادم ثم يبقى يعرض السجل.
rem  لتخصيص كلمة مرور حساب المالك عند أول إقلاع على قاعدة فارغة: BOOTSTRAP__OWNERPASSWORD
rem  (8 أحرف ورقم على الأقل — الافتراضي Wera@2026 غيّرها بعد أول دخول).
rem ─────────────────────────────────────────────────────────────────────────────
setlocal EnableExtensions
set "KEY_DIR=%LOCALAPPDATA%\WERA_PRO_HD"
set "KEY_FILE=%KEY_DIR%\api-signing.key"

if not exist "%KEY_DIR%" mkdir "%KEY_DIR%"

if exist "%KEY_FILE%" goto :have_key
echo [run-api] توليد مفتاح توقيع جديد لأول تشغيل...
powershell -NoProfile -Command "$c='-ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789'; $r=New-Object System.Random; -join (1..48 | ForEach-Object { $c[$r.Next($c.Length)] })" > "%TEMP%\wera_key.tmp"
for /f "usebackq delims=" %%K in ("%TEMP%\wera_key.tmp") do set "SIGNING_KEY=%%K"
del "%TEMP%\wera_key.tmp" >nul 2>&1
if "%SIGNING_KEY%"=="" (
    echo [run-api] تعذر توليد المفتاح — شغّل يدوياً بضبط TOKENS__SIGNINGKEYSECRET.
    exit /b 1
)
>"%KEY_FILE%" echo %SIGNING_KEY%
attrib +H "%KEY_FILE%" >nul 2>&1

:have_key
set /p SIGNING_KEY=<"%KEY_FILE%"
set "TOKENS__SIGNINGKEYSECRET=%SIGNING_KEY%"
set "ASPNETCORE_URLS=%ASPNETCORE_URLS%"
if "%ASPNETCORE_URLS%"=="" set "ASPNETCORE_URLS=http://localhost:5080"

echo [run-api] تشغيل WeraPro.Api على %ASPNETCORE_URLS% ...
"%~dp0WeraPro.Api.exe" %*
endlocal
