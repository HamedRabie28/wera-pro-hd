# ARCHITECTURE BRIEF — المعمارية المرجعية وقرارات ADR

> **الوثيقة:** WERA-AB-001 · **الإصدار:** 1.0 · **التاريخ:** 2026-11-01
> المخطط المرجعي: `assets/wera_architecture_diagram.png`

---

## 1. المعمارية المعتمدة

**Clean Architecture + Modular Monolith** في الخادم، وتطبيق Desktop مستقل يتصل عبر API — لا Microservices قبل حاجة مثبتة ومبرر تشغيلي موثق.

### قواعد الحدود (Non-negotiable)

1. **Domain** (قواعد المنتجات والوحدات والعبوات والأسعار والوزن والمخزون والمبيعات والمدفوعات والمرتجعات والورديات والخزينة والمحاسبة): لا يعتمد على WinUI أو HTTP أو EF Core إطلاقًا.
2. **Application** (Use Cases / Services / DTOs / Policies / Validation / واجهات المستودعات): بوابة إلزامية — **ممنوع أن تتجاوز الواجهة هذه الطبقة إلى قاعدة البيانات**.
3. **Infrastructure** (EF Core/Npgsql · PostgreSQL · تخزين الصور · الطباعة · الباركود · الموازين · بوابات الدفع · النسخ الاحتياطي · السجلات): تنفيذ تفاصيل خلف عقود.
4. **API** (ASP.NET Core): مصادقة/تفويض لكل endpoint · تحقق · Idempotency · Rate Limiting · Health Checks · OpenAPI · معالجة أخطاء موحدة.
5. **Desktop Presentation** (WinUI 3/XAML/MVVM) + **Desktop Client Services** (اتصال API/جلسة/Cache) + **Hardware Integration** (عقود typed مستقلة) + **Background Services** + **Observability & Tests**.

### المصدر الوحيد للحقيقة (Multi-Station)

- ASP.NET Core هو مصدر الحقيقة للأسعار والضرائب والمخزون والرصيد والصلاحيات والفواتير والمدفوعات والمؤشرات.
- **لا يتصل تطبيق الكاشير بـ PostgreSQL مباشرة** (ممنوع منعًا باتًا — §0.1 ق13).
- لا تُقبل قيمة إجمالي من العميل دون إعادة حساب خادمية.
- Cache قراءة بصلاحية معلومة، ولا يُتخذ به قرار مالي حساس دون تحقق.
- كل عملية حساسة: Use Case + عقد API + Transaction + Idempotency عند الحاجة + Audit Trail + اختبار.

## 2. خط الأساس التقني (يُعاد التحقق عند كل بوابة)

| المكوّن | القرار | ملاحظات |
|---|---|---|
| اللغة / Runtime | C# / **.NET 10 LTS** | اللقطة المرجعية 2026-11-01: آخر Patch ‏10.0.14 (2026-10-25)، نهاية الدعم 2028-11-14 |
| واجهة Windows | **WinUI 3 + Windows App SDK** | اللقطة: 2.6.4 Stable (2026-10-27) — يُثبت الفعلي بالبناء عند G3 |
| MVVM | CommunityToolkit.Mvvm | بعد التحقق من التوافق والترخيص والصيانة |
| Backend | ASP.NET Core 10 · REST + OpenAPI versioned | |
| قاعدة الإنتاج | PostgreSQL + EF Core/Npgsql | Dapper للقراءات المقاسة فقط |
| التخزين المحلي | SQLite اختياري (Cache/Outbox) | ليس قاعدة شبكية مشتركة |
| Validation / Logging | مسار واحد موحد (يحسم في G3) + Serilog | إخفاء الأسرار إلزامي |
| الاختبارات | xUnit + Testcontainers + UI Automation متوافق | لا Preview/Experimental في الإنتاج |
| الإصدارات المركزية | `global.json` + `Directory.Packages.props` | ممنوع `latest` غير المقيد |

## 3. هيكل الحل (Solution Layout)

```
WERA_PRO_HD/
├── WeraPro.sln · global.json · Directory.Build.props · Directory.Packages.props
├── src/
│   ├── WeraPro.Domain/            (Catalog/ Units/ Pricing/ Inventory/ Sales/ Weighing/
│   │                               Payments/ Returns/ Purchasing/ Customers/ Suppliers/
│   │                               Shifts/ CashManagement/ Accounting/ Reporting/ Security/)
│   ├── WeraPro.Application/       (Abstractions/ UseCases/ Services/ DTOs/ Validators/ Policies/)
│   ├── WeraPro.Contracts/         (عقود API المشتركة versioned)
│   ├── WeraPro.Infrastructure/    (Persistence/ Repositories/ MediaStorage/ Printing/ Barcode/
│   │                               Scales/ PaymentProviders/ Backup/ Logging/)
│   ├── WeraPro.Api/               (Endpoints/ Middleware/ Authorization/ HealthChecks/)
│   ├── WeraPro.Desktop/           (Views/ ViewModels/ Controls/ Dialogs/ Navigation/
│   │                               Themes/ Resources/ Converters/ Localization/)
│   ├── WeraPro.Hardware.Abstractions/   (IBarcodeScanner · IScaleDevice · IReceiptPrinter ·
│   │                                     ILabelPrinter · ICashDrawer · IPaymentTerminal)
│   ├── WeraPro.Hardware.Windows/  (عزل P/Invoke وCOM داخل adapters)
│   └── WeraPro.BackgroundServices/(استيراد/تقارير/نسخ احتياطي/طباعة/مزامنة)
├── tests/ (Domain · Application · Infrastructure · Api · Desktop · Hardware · Performance · Migration)
├── database/ (migrations/ scripts/ seeds/) · deployment/ · docs/ · assets/ · tools/
```

قواعد: لا مشاريع فارغة، لا خلط Domain/Infrastructure/UI، تعديل الهيكل بعد G0 يتطلب ADR.

## 4. سجل قرارات ADR المبدئي

| ADR | القرار | المبرر المختصر | بدائل مرفوضة |
|---|---|---|---|
| ADR-001 | WinUI 3 لطبقة العرض | واجهة Windows أصلية حديثة بـXAML/MVVM وأداء سطح مكتب وRTL مدعوم — يحقق هوية «تطبيق مكتبي قوي لا صفحة ويب» | WPF (أقدم إطارًا) · Electron/WebView (وزن وذاكرة وسلوك ويب) |
| ADR-002 | ASP.NET Core 10 مركزيًا | مصدر حقيقة واحد لمحطات متعددة + عقود موثقة + صلاحيات خادمية | اتصال مباشر للقاعدة (ممنوع — أمن وتضارب) · قاعدة لكل محطة (تزامن مستحيل) |
| ADR-003 | PostgreSQL للإنتاج | أداء فهارس exact-match عند 5M منتج و10M باركود · NUMERIC/DECIMAL دقيق · migrations ناضجة عبر Npgsql | SQLite للشبكة (غير مصمم للتزامن متعدد المحطات) |
| ADR-004 | Modular Monolith | سرعة بناء واختبار وحدة نشر واحدة مع حدود وحدات صارمة قابلة للتقسيم لاحقًا عند حاجة مثبتة | Microservices مبكرًا (تعقيد تشغيل بلا مبرر) |
| ADR-005 | عقود أجهزة typed خلف interfaces | عزل P/Invoke/COM وقابلية اختبار وسجل تشخيص ونتيجة حقيقية أو UNKNOWN | أجهزة داخل ViewModels (غير قابل للاختبار ويسبب تجمد الواجهة) |
| ADR-006 | النشر MSIX مبدئيًا (D-13 نهائي) | ترقية نظيفة وإدارة حديثة مع Windows App SDK — يُختبر التثبيت والترقية والإزالة فعليًا | Installer يدوي (إدارة ترقية أثقل) — يظل خيارًا مفتوحًا بالقرار |

## 5. عقد POS المعماري (من البرومت §9.0)

نافذة مستقلة بلا تنقل إداري · لا شبكة منتجات · السلة العنصر المركزي · حقل الباركود أولوية تركيز · الإجمالي والمدفوع والمتبقي وزر الدفع ظاهرة دائمًا · كل الأوامر عبر Use Cases وخدمات API — **لا حسابات مالية أو مخزنية في XAML أو ViewModel أو Code-Behind**.

## 6. قواعد قاعدة البيانات والوصول

- decimal في C# ↔ NUMERIC/DECIMAL محددة الدقة في PostgreSQL للمال والوزن المؤثر في المال.
- migrations خطية مراجَعة؛ لا تدميرية دون خطة ونسخة احتياطية وRollback/Forward Fix.
- فهارس السعة حسب خطة القسم 7 من التقرير الرئيسي (انظر BASELINE + تقرير PDF).
- UTC للتخزين والمراجع الزمنية، والعرض بتوقيت الفرع المعتمد.
