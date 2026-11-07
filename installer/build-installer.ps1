# ═══════════════════════════════════════════════════════════════════════════
#  WERA PRO HD — Professional Installer Build Script
#  build-installer.ps1 · أمر المالك 2026-11-07
#
#  الاستخدام (من جذر المستودع أو من مجلد installer):
#    powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
#    powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1 -Platform arm64
#    powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1 -AppVersion 0.4.0
#    powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1 -SkipPublish
#
#  المتطلبات: .NET SDK 10 · Inno Setup 6.3+ (اختياري: يُنزَّل vc_redist تلقائيًا)
#  الناتج:     installer\output\WeraProSetup-<الإصدار>-win-<المعمارية>.exe
# ═══════════════════════════════════════════════════════════════════════════
[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64')]
    [string]$Platform = 'x64',

    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [string]$AppVersion = '0.3.0',

    # تخطي النشر (عند وجود ناتج publish سابق صالح)
    [switch]$SkipPublish,

    # تخطي تنزيل تشغيليات Visual C++ (الحزمة تعمل بدونه — مكون اختياري)
    [switch]$SkipVcRedist
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

# ── مسارات ثابتة ──
$InstallerDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot     = Split-Path -Parent $InstallerDir
$DesktopProj  = Join-Path $RepoRoot 'src\WeraPro.Desktop\WeraPro.Desktop.csproj'
$PublishDir   = Join-Path $InstallerDir ("publish\win-" + $Platform)
$RedistDir    = Join-Path $InstallerDir 'redist'
$OutputDir    = Join-Path $InstallerDir 'output'
$VcRedistUrl  = 'https://aka.ms/vs/17/release/vc_redist.x64.exe'
$VcRedistFile = Join-Path $RedistDir 'vc_redist.x64.exe'

function Write-Step([string]$msg)  { Write-Host ('==> ' + $msg) -ForegroundColor Cyan }
function Write-Ok([string]$msg)    { Write-Host ('    ' + $msg) -ForegroundColor Green }
function Write-Warn2([string]$msg) { Write-Host ('    ' + $msg) -ForegroundColor Yellow }

function Assert-Tool([string]$name) {
    if (-not (Get-Command $name -ErrorAction SilentlyContinue)) {
        throw ("المدخل المطلوب غير موجود: " + $name + " — ثبّت .NET SDK 10 أولاً.")
    }
}

function Find-InnoSetup {
    # 1) PATH
    $iscc = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($iscc) { return $iscc.Source }

    # 2) مسارات التثبيت القياسية (x86/x64)
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    )
    foreach ($p in $candidates) {
        if ($p -and (Test-Path $p)) { return $p }
    }
    return $null
}

try {
    Write-Host ''
    Write-Host '════════════════ WERA PRO HD — Installer Build ════════════════' -ForegroundColor Magenta
    Write-Host ("    Platform: " + $Platform + "  ·  Configuration: " + $Configuration + "  ·  Version: " + $AppVersion)
    Write-Host ''

    # ── 1) نشر Desktop (Self-Contained — لا متطلبات مسبقة على جهاز العميل) ──
    if (-not $SkipPublish) {
        Write-Step 'نشر تطبيق سطح المكتب (dotnet publish · self-contained · ReadyToRun)'
        Assert-Tool 'dotnet'

        if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }

        & dotnet publish $DesktopProj `
            -c $Configuration `
            -r ("win-" + $Platform) `
            -p:Platform=$Platform `
            --self-contained true `
            -o $PublishDir
        if ($LASTEXITCODE -ne 0) { throw 'فشل dotnet publish — راجع الرسائل أعلاه.' }

        $exe = Join-Path $PublishDir 'WeraPro.Desktop.exe'
        if (-not (Test-Path $exe)) { throw 'النشر اكتمل لكن WeraPro.Desktop.exe غير موجود — فحص غير متوقع.' }
        $size = [math]::Round((Get-ChildItem $PublishDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
        Write-Ok ("اكتمل النشر: " + $size + " MB في publish\win-" + $Platform)
    }
    else {
        Write-Warn2 'تخطي النشر — استخدام ناتج publish القائم (تأكد من حداثته).'
        if (-not (Test-Path (Join-Path $PublishDir 'WeraPro.Desktop.exe'))) {
            throw 'لا يوجد ناتج publish سابق — شغّل السكربت دون -SkipPublish.'
        }
    }

    # ── 2) تشغيليات Visual C++ (مكون اختياري — يُثبَّت عند الحاجة فقط) ──
    if (-not $SkipVcRedist) {
        Write-Step 'تشغيليات Visual C++ (اختياري — يُتجاوز تلقائيًا عند غيابه)'
        New-Item -ItemType Directory -Force -Path $RedistDir | Out-Null

        $needsDownload = $true
        if (Test-Path $VcRedistFile) {
            $sz = (Get-Item $VcRedistFile).Length
            if ($sz -gt 10MB) { $needsDownload = $false; Write-Ok ("موجود مسبقًا (" + [math]::Round($sz/1MB,1) + " MB)") }
        }
        if ($needsDownload) {
            try {
                [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
                Invoke-WebRequest -Uri $VcRedistUrl -OutFile $VcRedistFile -UseBasicParsing
                $sz = (Get-Item $VcRedistFile).Length
                if ($sz -lt 10MB) { throw ('حجم غير منطقي: ' + $sz) }
                Write-Ok ("نُزِّل بنجاح (" + [math]::Round($sz/1MB,1) + " MB)")
            }
            catch {
                Remove-Item $VcRedistFile -Force -ErrorAction SilentlyContinue
                Write-Warn2 ('تعذر التنزيل (' + $_.Exception.Message + ') — ستُبنى الحزمة بدونه; المكون يُتجاوز تلقائيًا في التثبيت.')
            }
        }
    }
    else {
        Write-Warn2 'تخطي VcRedist بطلب صريح.'
    }

    # ── 3) تحديد Inno Setup ──
    Write-Step 'البحث عن Inno Setup 6.3+ (ISCC.exe)'
    $iscc = Find-InnoSetup
    if (-not $iscc) {
        throw @'
Inno Setup غير مثبت. التثبيت الاحترافي:
  winget install JRSoftware.InnoSetup
أو حمّله من https://jrsoftware.org/isdl.php (الإصدار 6.3 فأحدث — مطلوب لدعم RightToLeft)
أعد تشغيل هذا السكربت بعد التثبيت.
'@
    }
    $innoVersion = (Get-Item $iscc).VersionInfo.FileVersion
    Write-Ok ('Inno Setup ' + $innoVersion + ' — ' + $iscc)
    try {
        $major = [int]($innoVersion.Split('.')[0]); $minor = [int]($innoVersion.Split('.')[1])
        if (($major -lt 6) -or (($major -eq 6) -and ($minor -lt 3))) {
            throw ('الإصدار ' + $innoVersion + ' أقدم من 6.3 — RightToLeft غير مدعوم. حدِّث Inno Setup.')
        }
    }
    catch { throw }

    # ── 4) بناء المثبِّت ──
    Write-Step 'بناء حزمة التثبيت (ISCC · lzma2/max)'
    New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

    & $iscc (Join-Path $InstallerDir 'WeraProSetup.iss') ("/DPlatform=" + $Platform) ("/DAppVersion=" + $AppVersion)
    if ($LASTEXITCODE -ne 0) { throw 'فشل ISCC — راجع رسائل المترجم أعلاه.' }

    # ── 5) التحقق من الناتج ──
    $setupExe = Join-Path $OutputDir ("WeraProSetup-" + $AppVersion + "-win-" + $Platform + ".exe")
    if (-not (Test-Path $setupExe)) { throw 'اكتمل ISCC دون أخطاء لكن ملف الحزمة غير موجود — غير متوقع.' }

    $hash = (Get-FileHash $setupExe -Algorithm SHA256).Hash
    Write-Host ''
    Write-Host '════════════════ اكتمل البناء بنجاح ════════════════' -ForegroundColor Green
    Write-Ok ("الحزمة: " + $setupExe)
    Write-Ok ("الحجم:  " + [math]::Round((Get-Item $setupExe).Length / 1MB, 1) + " MB")
    Write-Ok ("SHA256: " + $hash)
    Write-Host ''
    Write-Host 'التثبيت الصامت (نشر مؤسسي):' -ForegroundColor Cyan
    Write-Host ('  ' + $setupExe + ' /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /ALLUSERS')
    Write-Host ''
    exit 0
}
catch {
    Write-Host ''
    Write-Host ('فشل البناء: ' + $_.Exception.Message) -ForegroundColor Red
    Write-Host ('الموقع: ' + $_.InvocationInfo.PositionMessage) -ForegroundColor DarkRed
    exit 1
}
