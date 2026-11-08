# ═══════════════════════════════════════════════════════════════════════════
#  WERA PRO HD — Production Build Script
#  build-production.ps1 · 2026-11-08
#
#  يبني ملفات الإنتاج النهائية الكاملة في مجلد production/:
#    production/api-server/      خادم API منشور (Self-Contained لـ linux-x64 و win-x64)
#    production/desktop-win-x86/ تطبيق سطح المكتب منشور (32-bit)
#    production/desktop-win-x64/ تطبيق سطح المكتب منشور (64-bit)
#    production/installers/     حزم تثبيت Inno Setup (32-bit و 64-bit)
#
#  الاستخدام (يشترط Windows لجزء Desktop · API يمكن بناؤه على Linux أيضًا):
#    powershell -ExecutionPolicy Bypass -File installer\build-production.ps1
#    powershell -ExecutionPolicy Bypass -File installer\build-production.ps1 -SkipDesktop
#    powershell -ExecutionPolicy Bypass -File installer\build-production.ps1 -SkipApi
#    powershell -ExecutionPolicy Bypass -File installer\build-production.ps1 -ApiOnly
#
#  المتطلبات:
#    - .NET SDK 10 (لـ API و Desktop)
#    - Inno Setup 6.3+ (لحزم التثبيت فقط)
# ═══════════════════════════════════════════════════════════════════════════
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [string]$AppVersion = '0.3.0',

    # معماريات خادم API (Self-Contained لكل منهما)
    [ValidateSet('linux-x64', 'linux-arm64', 'win-x64', 'win-x86')]
    [string[]]$ApiRids = @('linux-x64', 'win-x64'),

    [switch]$SkipApi,
    [switch]$SkipDesktop,
    [switch]$SkipInstallers,
    [switch]$ApiOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

$InstallerDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot     = Split-Path -Parent $InstallerDir
$ProductionDir = Join-Path $RepoRoot 'production'

function Write-Step([string]$m) { Write-Host ('==> ' + $m) -ForegroundColor Cyan }
function Write-Ok([string]$m)   { Write-Host ('    ' + $m) -ForegroundColor Green }

try {
    Write-Host ''
    Write-Host '════════════════ WERA PRO HD — Production Build ════════════════' -ForegroundColor Magenta
    Write-Host ('    Configuration: ' + $Configuration + '  ·  Version: ' + $AppVersion)
    Write-Host ''

    if ($ApiOnly) { $SkipDesktop = $true; $SkipInstallers = $true }

    # ──────────────────────────────────────────────────────────────────────
    # 1) بناء خادم API (Self-Contained لكل معمارية موزّعة)
    # ──────────────────────────────────────────────────────────────────────
    if (-not $SkipApi) {
        Write-Step 'بناء خادم API (dotnet publish · Self-Contained)'
        $apiProj = Join-Path $RepoRoot 'src\WeraPro.Api\WeraPro.Api.csproj'
        $apiOut  = Join-Path $ProductionDir 'api-server'

        foreach ($rid in $ApiRids) {
            Write-Host ('  -- RID: ' + $rid) -ForegroundColor DarkCyan
            $ridOut = Join-Path $apiOut $rid
            if (Test-Path $ridOut) { Remove-Item $ridOut -Recurse -Force }
            & dotnet publish $apiProj -c $Configuration -r $rid --self-contained true -o $ridOut -p:VersionPrefix=$AppVersion
            if ($LASTEXITCODE -ne 0) { throw ('فشل publish API لـ ' + $rid) }
            $sz = [math]::Round((Get-ChildItem $ridOut -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
            Write-Ok ($rid + ': ' + $sz + ' MB في production\api-server\' + $rid)
        }
    }

    # ──────────────────────────────────────────────────────────────────────
    # 2) بناء تطبيق سطح المكتب (Self-Contained لـ x86 و x64)
    # ──────────────────────────────────────────────────────────────────────
    if (-not $SkipDesktop) {
        Write-Step 'بناء تطبيق سطح المكتب WinUI 3 (dotnet publish · Self-Contained)'
        $desktopProj = Join-Path $RepoRoot 'src\WeraPro.Desktop\WeraPro.Desktop.csproj'

        foreach ($plat in @('x86', 'x64')) {
            Write-Host ('  -- Platform: ' + $plat) -ForegroundColor DarkCyan
            $out = Join-Path $ProductionDir ('desktop-win-' + $plat)
            if (Test-Path $out) { Remove-Item $out -Recurse -Force }
            & dotnet publish $desktopProj -c $Configuration -r ('win-' + $plat) -p:Platform=$plat --self-contained true -o $out
            if ($LASTEXITCODE -ne 0) { throw ('فشل publish Desktop لـ ' + $plat) }
            $exe = Join-Path $out 'WeraPro.Desktop.exe'
            if (-not (Test-Path $exe)) { throw ('WeraPro.Desktop.exe غير موجود بعد النشر — غير متوقع') }
            $sz = [math]::Round((Get-ChildItem $out -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
            Write-Ok ('win-' + $plat + ': ' + $sz + ' MB في production\desktop-win-' + $plat)
        }
    }

    # ──────────────────────────────────────────────────────────────────────
    # 3) بناء حزم التثبيت Inno Setup (32-bit و 64-bit)
    # ──────────────────────────────────────────────────────────────────────
    if (-not $SkipInstallers) {
        Write-Step 'بناء حزم التثبيت Inno Setup (32-bit + 64-bit)'
        $buildScript = Join-Path $InstallerDir 'build-installer.ps1'
        & powershell -ExecutionPolicy Bypass -File $buildScript -BuildAll -AppVersion $AppVersion -Configuration $Configuration -SkipPublish
        if ($LASTEXITCODE -ne 0) { throw 'فشل build-installer.ps1 -BuildAll' }

        # نقل الحزم النهائية إلى production/installers/
        $installersOut = Join-Path $ProductionDir 'installers'
        New-Item -ItemType Directory -Force -Path $installersOut | Out-Null
        $srcDir = Join-Path $InstallerDir 'output'
        if (Test-Path $srcDir) {
            Get-ChildItem $srcDir -Filter 'WeraProSetup-*.exe' | ForEach-Object {
                Copy-Item $_.FullName -Destination $installersOut -Force
                Write-Ok ('حزمة: ' + $_.Name)
            }
        }
    }

    Write-Host ''
    Write-Host '════════════════ اكتمل البناء الإنتاجي ════════════════' -ForegroundColor Green
    Write-Host ('  ملفات الإنتاج في: ' + $ProductionDir) -ForegroundColor Cyan
    Write-Host ''
    exit 0
}
catch {
    Write-Host ''
    Write-Host ('فشل البناء الإنتاجي: ' + $_.Exception.Message) -ForegroundColor Red
    Write-Host ('الموقع: ' + $_.InvocationInfo.PositionMessage) -ForegroundColor DarkRed
    exit 1
}
