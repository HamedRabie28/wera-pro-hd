; ═══════════════════════════════════════════════════════════════════════════
;  WERA PRO HD — Professional Windows Installer (Inno Setup 6.3+)
;  أمر المالك 2026-11-07: حزمة تثبيت احترافية متوافقة مع كل إصدارات
;  ويندوز المدعومة، بلا أخطاء، وبظهور النافذة دائمًا.
;
;  البناء:  installer\build-installer.ps1  (ينشر Desktop ثم يستدعي ISCC)
;  المعزول: .NET 10 Self-Contained + Windows App SDK Self-Contained
;           ← لا حاجة لتثبيت أي متطلبات مسبقة على جهاز العميل.
;  المدعوم: Windows 10 (1809/17763 فأحدث) و Windows 11 · x64 و ARM64.
;  صامت:    /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
; ═══════════════════════════════════════════════════════════════════════════

#ifndef AppVersion
  #define AppVersion "0.3.0"
#endif
#ifndef Platform
  #define Platform "x64"
#endif
#if Platform == "x64"
  #define ArchSuffix "win-x64"
#else
  #define ArchSuffix "win-arm64"
#endif

#define AppName "WERA PRO HD"
#define AppExeName "WeraPro.Desktop.exe"
#define AppPublisher "WERA PRO HD Build Team"
#define AppIdGuid "{{5D944405-EC34-4CD3-97B7-2CD9A6F23638}"

[Setup]
AppId={#AppIdGuid}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=https://wera-pro-hd.local/
AppSupportURL=https://wera-pro-hd.local/support
AppUpdatesURL=https://wera-pro-hd.local/updates

; التثبيت الافتراضي لكل المستخدمين (احترافي) مع سماح التثبيت للمستخدم الحالي
; عبر حوار الخيارات أو سطر الأوامر: /CURRENTUSER أو /ALLUSERS
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog commandline

; ── التوافق: Windows 10 1809 (17763) فأحدث + Windows 11 ──
;  (17763 هو الحد الأدنى المعماري لـ WinUI 3 / Windows App SDK من مايكروسوفت)
MinVersion=10.0.17763
#if Platform == "x64"
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#else
ArchitecturesAllowed=arm64
#endif

DefaultDirName={autopf}\WERA PRO HD
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}

; ── الهوية البصرية الاحترافية ──
SetupIconFile=Assets\setup.ico
WizardImageFile=Assets\wizard_large.bmp
WizardSmallImageFile=Assets\wizard_small.bmp
WizardStyle=modern
RightToLeft=yes

; ── معلومات الإصدار في خصائص الملف ──
VersionInfoVersion={#AppVersion}.0
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} — Retail Operations Workbench (Installer)
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}

; ── ضغط احترافي أقصى ──
Compression=lzma2/max
SolidCompression=yes
InternalCompressLevel=ultra64

OutputDir=output
OutputBaseFilename=WeraProSetup-{#AppVersion}-{#ArchSuffix}

; إغلاق التطبيق بأمان قبل الترقية/الإزالة ثم إعادة فتحه بعد الترقية
CloseApplications=yes
RestartApplications=yes

[Languages]
Name: "ar"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
ar.AppComponent=تطبيق سطح المكتب (الملفات الأساسية)
ar.AppComponentDesc=WERA PRO HD — تطبيق نقاط البيع وإدارة التجزئة (مستقل ذاتيًا، لا يحتاج أي متطلبات مسبقة)
ar.VcRedistComponent=تشغيليات Visual C++ (تُثبَّت صامتًا عند الحاجة فقط)
ar.VcRedistComponentDesc=مكتبات C++ المطلوبة لعرض النافذة على بعض أجهزة ويندوز النظيفة — يُتجاوز تلقائيًا إن كانت مثبتة مسبقًا
ar.LaunchProgram=تشغيل {#AppName} الآن
ar.WindowsTooOld=هذا الإصدار من ويندوز أقدم من الحد الأدنى المدعوم.%n%nيعمل WERA PRO HD على Windows 10 (إصدار 1809 / بناء 17763) و Windows 11 فقط، لأن إطار واجهة WinUI 3 من مايكروسوفت لا يدعم الإصدارات الأقدم.%n%nالرجاء ترقية ويندوز ثم إعادة المحاولة.
ar.DesktopIconDesc=أيقونة على سطح المكتب

[Messages]
; ── معرّب كامل للمعالج (يتجاوز النصوص الإنجليزية الافتراضية) ──
SetupAppTitle=إعداد WERA PRO HD
SetupWindowTitle=إعداد {#AppName}
WelcomeLabel1=مرحبًا بك في معالج إعداد%n{#AppName}
WelcomeLabel2=سيقوم هذا المعالج بتثبيت {#AppName} {#AppVersion} على حاسوبك.%n%nنصيحة: أغلق بقية التطبيقات قبل المتابعة، ثم اضغط «التالي».
WizardSelectDir=اختر مجلد التثبيت
SelectDirDesc=أين تريد تثبيت {#AppName}؟
SelectDirLabel3=سيقوم الإعداد بتثبيت {#AppName} في المجلد التالي.
SelectDirBrowseLabel=اضغط «التالي» للمتابعة، أو «استعراض» لاختيار مجلد آخر.%n%nيحتاج التثبيت إلى %1 من المساحة الحرة على القرص.
DiskSpaceMBLabel=المساحة المطلوبة: %1 ميغابايت على الأقل.
DiskSpaceGBLabel=المساحة المطلوبة: %1 غيغابايت على الأقل.
WizardSelectProgramGroup=اختر مجلد قائمة «ابدأ»
SelectProgramGroupDesc=أين تريد وضع اختصارات {#AppName}؟
SelectStartMenuFolderMsg=سيتم إنشاء اختصارات البرنامج في مجلد قائمة «ابدأ» التالي.
NoProgramGroupCheck2=عدم إنشاء مجلد في قائمة «ابدأ»
WizardReady=جاهز للتثبيت
ReadyLabel1=الإعداد جاهز لتثبيت {#AppName} {#AppVersion} على حاسوبك.
ReadyLabel2b=اضغط «تثبيت» للمتابعة، أو «السابق» لمراجعة أي إعداد.
ButtonNext=&التالي
ButtonBack=< &السابق
ButtonInstall=&تثبيت
ButtonFinish=&إنهاء
ButtonCancel=إلغاء
ButtonBrowse=است&عراض
WizardInstalling=جارٍ التثبيت
InstallingLabel=يرجى الانتظار بينما يُثبَّت {#AppName} على حاسوبك.
FinishedHeadingLabel=اكتمل معالج إعداد {#AppName}
FinishedLabelNoIcons=اكتمل تثبيت {#AppName} {#AppVersion} على حاسوبك. اضغط «إنهاء» للخروج من المعالج.
FinishedLabel=اكتمل تثبيت {#AppName} {#AppVersion} على حاسوبك. يمكن تشغيل التطبيق من اختصار سطح المكتب أو قائمة «ابدأ».%n%nاضغط «إنهاء» للخروج من المعالج.
ExitSetupMessage=الإعداد لم يكتمل بعد. إذا أُغلق الآن لن يُثبَّت {#AppName}.%n%nهل تريد الإنهاء؟
StatusClosingApplications=إغلاق التطبيقات قيد التشغيل…
StatusRestartApplications=إعادة تشغيل التطبيقات…
StatusCreateDirs=إنشاء المجلدات…
StatusExtractFiles=استخراج الملفات…
StatusRegisterFiles=تسجيل الملفات…
StatusCreateIcons=إنشاء الاختصارات…
PreparingDesc=سيتحقق الإعداد من جهازك قبل التثبيت.
WizardPreparing=جارٍ التحضير للتثبيت

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIconDesc}"; GroupDescription: "اختصارات إضافية:"
Name: "desktopicon\common"; Description: "لكل المستخدمين"; Flags: exclusive
Name: "desktopicon\user"; Description: "للمستخدم الحالي فقط"; Flags: exclusive unchecked

[Types]
Name: "full"; Description: "التثبيت الكامل (موصى به)"
Name: "custom"; Description: "تثبيت مخصص"; Flags: iscustom

[Components]
Name: "app"; Description: "{cm:AppComponent}"; Types: full custom; Flags: fixed
Name: "vcredist"; Description: "{cm:VcRedistComponent}"; Types: full; ExtraDiskSpaceRequired: 26214400

[Files]
; ملفات التطبيق المنشورة (Self-Contained — تعمل دون أي متطلبات مسبقة)
Source: "publish\{#ArchSuffix}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: app; Excludes: "*.pdb,*.lastbuild"
; تشغيليات Visual C++ (اختياري: يُتجاوز تلقائيًا إن غاب الملف أو كانت مثبتة مسبقًا)
Source: "redist\vc_redist.x64.exe"; DestDir: "{tmp}"; Flags: ignoreversion deleteafterinstall skipifsourcedoesntexist; Components: vcredist; Check: VCRedistMissing

[Dirs]
; مجلد السجلات داخل بيانات المستخدم — يُنشأ مسبقًا لضمان نجاح الإقلاع الأول
Name: "{localappdata}\WERA_PRO_HD\logs"; Flags: uninsneveruninstall

[Icons]
; ── اختصارات بدليل عمل صحيح ({app}) — حزام أمان إضافي لصحة الإقلاع ──
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#AppExeName}"
Name: "{group}\إلغاء تثبيت {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; بصمة التثبيت للاستعلام الإداري — دون أي كتابة خارج مكانها
Root: HKLM; Subkey: "Software\WERA PRO HD"; ValueType: string; ValueName: "InstallPath"; ValueData: "{app}"; Flags: uninsdeletekey; Check: IsAdminInstallMode
Root: HKCU; Subkey: "Software\WERA PRO HD"; ValueType: string; ValueName: "InstallPath"; ValueData: "{app}"; Flags: uninsdeletekey; Check: not IsAdminInstallMode

[Run]
; تشغيليات C++ عند الحاجة فقط — صامت كليًا
Filename: "{tmp}\vc_redist.x64.exe"; Parameters: "/install /quiet /norestart"; StatusMsg: "تثبيت تشغيليات Visual C++…"; Flags: runhidden waituntilterminated skipifsilent; Components: vcredist; Check: VCRedistMissing
; تشغيل التطبيق بعد اكتمال التثبيت (خيار للمستخدم — يُتجاوز في التثبيت الصامت)
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent unchecked

[UninstallRun]
; إن كان التطبيق يعمل أثناء الإزالة: يُغلق بلطف (CloseApplications يغطي الأغلب)
Filename: "{cmd}"; Parameters: "/C taskkill /IM {#AppExeName} /F /T >NUL 2>&1"; Flags: runhidden

[UninstallDelete]
; نظافة كاملة لمجلد التثبيت (بقايا السجلات المحلية تبقى في بيانات المستخدم عمدًا)
Type: filesandordirs; Name: "{app}\runtimes"

[Code]
function VCRedistMissing(): Boolean;
var
  Installed: Cardinal;
begin
  // تشغيليات VC++ 2015-2022 x64 مثبتة أصلًا؟ (بصمة مايكروسوفت الرسمية)
  Result := not (RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64', 'Installed', Installed) and (Installed = 1));
end;

function InitializeSetup(): Boolean;
var
  Version: TWindowsVersion;
begin
  Result := True;

  // تحقق ودّي من بناء ويندوز: 17763 = Windows 10 1809 (الحد الأدنى لـ WinUI 3)
  GetWindowsVersionEx(Version);
  if Version.Build < 17763 then
  begin
    SuppressibleMsgBox(ExpandConstant('{cm:WindowsTooOld}'), mbCriticalError, MB_OK, IDOK);
    Result := False;
  end;
end;
