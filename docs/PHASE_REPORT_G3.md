# PHASE REPORT G3 — تقرير المرحلة: Solution Foundation, Backend & Desktop Shell

> **المرجع:** GFM-CSharp 2.4.1 §22 (بوابة G3) و§11 (API متعدد المحطات) و§18 (الأمان والصلاحيات) و§21 (الاختبارات) — **بدأت البوابة بأمر المالك «اعتمد G3» ثم «اكمل» الصادر 2026-11-02** (تفويض بدء وتنفيذ — الاعتماد النهائي قرار المالك بعد هذا التقرير، وبعد رفع B-05 لشرط الصور).
> **التاريخ:** 2026-11-02 · **الفرع المقترح للتنفيذ:** `feature/g3-solution-foundation`

---

## 1. الملخص — ما اكتمل وما لم يكتمل

**اكتمل (وبُني واختُبر فعلياً — لا ادعاء):**

- **الحل `WeraPro.sln`**: 7 مشاريع مصدر + 3 مشاريع اختبار بإدارة إصدارات مركزية (`Directory.Packages.props` — لا إصدار غير مثبت؛ لقطة خط الأساس: .NET SDK 10.0.403 / حزم 10.0.x مطابقة للقطة ARCHITECTURE_BRIEF بما فيها Windows App SDK 2.6.4) و`global.json` مقفل و`Directory.Build.props` (Nullable + TreatWarningsAsErrors + Analyzers + EnforceCodeStyleInBuild) و`.editorconfig` و`nuget.config` بمصدر واحد ومطابقة مصادر. مشاريع: `Domain · Contracts · Application · Infrastructure · Api · Desktop · Hardware.Abstractions` (بقية مخطط §3 الهيكل — Hardware.Windows وBackgroundServices — تُنشأ في البوابة التي تحتاجها أولاً التزاماً بقاعدة «لا مشاريع فارغة»).
- **عدّاء الترحيلات الموازي لـ`run_migrations.py` (ADR-009):** `SqlMigrationRunner` يضمّن ملفات `database/migrations/V001..V014` **من الملفات نفسها** (مصدر واحد) ويطبقها بنفس بصمات sha256 في `core.schema_migrations` — Idempotent ويرفض البصمة المعدلة (Forward Fix). اختبار تكافؤ البصمات بين المسارين ناجح.
- **ترحيلان جديدان:** `V013` رموز تجديد الجلسة (hash فقط على الخادم + تدوير + إلغاء — §11.2) و`V014` صلاحيتا قراءة الأساس `ops.settings.read`/`ops.branches.read` لكل الأدوار السبعة (فصل القراءة عن `ops.settings.manage` — §18). وكلاهما مع حسم **D-02 (أ) WAC** و**D-03 (ب) موافقة مشرف** في `V012` (أمر المالك 2026-11-02).
- **API (ASP.NET Core 10):** JWT وصول 15 دقيقة + تجديد 12 ساعة **بصيغة hash فقط على الخادم وتدوير إلزامي** (إعادة استخدام رمز مُدار مرفوضة ومُختبرة) وbcrypt (work factor 12) للكلمات؛ تفويض صلاحيات ذرية خادمية عبر سياسات + كاش 60 ثانية + **نطاق فرع مفروض ومُختبر** (كاشير بلا نطاق فرع = 403)؛ `X-Correlation-ID` لكل طلب (تنقية المحارف) يصل إلى `audit.audit_logs` ويُرد في الاستجابة؛ أخطاء RFC 7807 ProblemDetails بالعربية بلا أسرار؛ تحقق FluentValidation (**ADR-007: مسار التحقق الموحد** حُسم بهذا التنفيذ)؛ Rate Limiting بنافذة ثابتة 5/دقيقة لكل IP على الدخول (مضاد brute-force، مُختبر 429) ودلو رموز عام؛ Health Checks (`/health/live` self و`/health/ready` بفحص PostgreSQL حقيقي) بإخراج JSON منظم؛ OpenAPI مدمج على `/openapi/v1.json`؛ نقاط النهاية v1: `auth/login·refresh·logout·me` و`settings` و`branches` و`stations` و`audit/logins` (بصلاحية `security.audit.view` — الكاشير 403).
- **سجل التدقيق الحي:** كل دخول (نجاح/رفض/معطل) يكتب صفاً في `audit.audit_logs` بمعرف الترابط، والجلسات في `audit.user_sessions` — مُتحقق منه في الاختبارات مباشرة من قاعدة البيانات.
- **Desktop (WinUI 3):** كود مكتمل بتصميم G2 المعتمد حرفياً (Tokens/أنماط منسوخة كما هي): إقلاع Generic Host + DI + Serilog، **نافذة دخول حقيقية** (خادم/فرع/محطة/حالة اتصال صادقة)، Shell بشريط علوي وهوية الجلسة ونبضة جاهزية كل 30 ثانية، **فتح POS مستقلة** متصلة تجلب إعدادات D-07 من الخادم، تجديد جلسة شفاف عند 401 عبر `IHttpClientFactory` (عميلان: رئيسي بمعرف ترابط + تجديد مستقل يمنع دورة المعالجة)، وكل I/O غير متزامن.
- **فحص معماري آلي مفروض (شرط البوابة «لا اتصال مباشر للقاعدة»):** Desktop بلا أي حزمة/using/سلسلة اتصال لقاعدة البيانات ويرجع العقود فقط، وDomain صفر حزم، وEndpoints لا تتجاوز Application — **يعمل من Linux فلا يعطّله B-05**.
- **CI (`.github/workflows/ci.yml`):** ubuntu (بناء Release + كل الاختبارات على PostgreSQL خدمة + `dotnet format --verify` + فحص اعتماديات مثقوبة) وwindows (بناء Desktop x64) — مع ملاحظة أن الصور التفاعلية خارج CI (B-05).
- **حساب الوزن المرجعي في المجال (D-07):** `WeightCalculator` بالتنفيذ القانوني الوحيد (Banker's لأقرب قرش + أقرب خطوة معتمدة + حفظ الدقيق والمعروض معاً) — **المثال الملزم 200 EGP/kg × 70 EGP → 350g مُختبر وحدةً ونجح**.

**لم يكتمل (مقيد بالمانع B-05 المعلن — بلا أي ادعاء):**

1. **بناء Desktop Release فعلي ولقطاته الحقيقية** (Shell/الدخول/POS): يتطلبان بيئة Windows — بيئة التطوير Linux. الكود مكتمل ومسلَّم مع README بناء، وفحص «لا اتصال مباشر» و«لا حجب UI» محققان آلياً على الكود؛ الشرح الكامل في §6.
2. لذلك **قرار البوابة أدناه مُقيَّد** بما ينص عليه الشرط الحرفي للبوابة: «تسليم صور حقيقية للـShell والدخول وPOS shell».

## 2. مخرجات الملفات (Files Changed)

| المسار | الغرض |
|---|---|
| `WeraPro.sln` + `WeraPro.Server.slnf` + `global.json` + `Directory.Build.props` + `Directory.Packages.props` + `.editorconfig` + `nuget.config` | هيكل الحل وإدارة الإصدارات المركزية (الخادم يُبنى من الفلتر على Linux/CI؛ الحل الكامل على Windows) |
| `src/WeraPro.Domain/` | النواة المشتركة: Result/DomainError + MoneyRounding (Banker's) + الأمان (User/Role/UserRole بنطاق فرع) + الإعدادات + WeightCalculator (D-07) |
| `src/WeraPro.Contracts/` | عقود v1 (DTOs + مسارات ثابتة) تشاركها Api وDesktop — عقد واحد لا نسخ يدوية |
| `src/WeraPro.Application/` | حالات الاستخدام (Login/Refresh/Logout/Settings/Audit) والعقود المجردة والمدققون (FluentValidation) |
| `src/WeraPro.Infrastructure/` | WeraDbContext (مخططات core/audit بأسماء مطابقة حرفياً + inet/jsonb أصلية) + SqlMigrationRunner (ADR-009) + مستودعات + bcrypt/JWT + فحص جاهزية PostgreSQL |
| `src/WeraPro.Api/` | Program (خط أنابيب ملزم) + Endpoints + Middleware (ترابط/أخطاء) + تفويض الصلاحيات + Rate Limiting + Health + OpenAPI |
| `src/WeraPro.Desktop/` | WinUI 3 كامل (Login/Shell/POS) + Services (ApiClient/Session/Connectivity) + ViewModels + تصميم G2 حرفياً + README بناء |
| `src/WeraPro.Hardware.Abstractions/` | عقود الأجهزة الستة typed + DeviceResult بحالة UNKNOWN صادقة (ADR-005) |
| `tests/WeraPro.{Domain,Infrastructure,Api}.Tests/` | 56 اختباراً (وحدات + عدّاء ترحيلات على PG حقيقي + تكامل/عقد/RBAC/ترابط/معدل/معمارية على PG حقيقي) |
| `database/migrations/V012__g2_owner_settlement_d02_d03.sql` | حسم D-02 (أ) وD-03 (ب) → OWNER_CONFIRMED (أمر المالك 2026-11-02) |
| `database/migrations/V013__g3_refresh_tokens.sql` | رموز تجديد الجلسة hash-only بتدوير وإلغاء (§11.2) |
| `database/migrations/V014__g3_read_permissions.sql` | صلاحيتا قراءة الأساس لكل الأدوار (§18) |
| `database/tests/CONSTRAINT_TEST_RESULTS_G3_V014.md` | أدلة إعادة الـ41 اختبار قيود على رأس V014 (40 PASS · 0 FAIL) |
| `.github/workflows/ci.yml` | CI مصفوفي ubuntu/windows |
| `docs/G3_TWO_CLIENTS_EVIDENCE.md` | دليل «عميلان على خادم الاختبار» عبر TCP حقيقي |
| `docs/PHASE_REPORT_G3.md` | هذا التقرير |

## 3. تتبع المتطلبات (ملخص)

- صفوف جديدة `G3-001..G3-018` (18 متطلباً) في `REQUIREMENTS_TRACEABILITY.csv` (المجموع 187).
- 16 صفاً `COMPLETED_G3` وصرفان `DELIVERED_CODE_PENDING_WINDOWS_BUILD_B05` (G3-011/G3-012 — كود Desktop مكتمل؛ البناء واللقطات على بيئة Windows).
- رُفع الحجب عن متطلبات D-02/D-03 في سجل القرارات وجرى توثيق V012 في T-ENV-02 (المعدل ليتحقق من الحسوم الثلاثة الجديدة).

## 4. Build & Tests (أدلة فعلية — الأوامر قابلة للإعادة)

| الفحص | الأمر | النتيجة |
|---|---|---|
| بناء Release (خادم) | `dotnet build WeraPro.Server.slnf -c Release` | ✅ **نجح — 0 تحذير 0 خطأ** (TreatWarningsAsErrors مفعّل) |
| اختبارات المجال | `dotnet test tests/WeraPro.Domain.Tests` | ✅ **22/22** (منها مثال D-07 المرجعي 350g) |
| اختبارات البنية التحتية | `dotnet test tests/WeraPro.Infrastructure.Tests` | ✅ **7/7** (رأس V14 · Idempotent · رفض بصمة معدلة · تكافؤ بصمات Python/.NET) |
| اختبارات API/عقد | `dotnet test tests/WeraPro.Api.Tests` | ✅ **27/27** (مصادقة/RBAC/نطاق فرع/ترابط/تحقق/صحة/OpenAPI/عقد/تدوير تجديد/خروج/عميلان متزامنان/429/معمارية) |
| قيود قاعدة البيانات (Python) | `run_constraint_tests.py` على رأس V014 | ✅ **40 PASS · 0 FAIL** · 1 ENV_LIMITATION (فهارس trgm — كما في G1 بصدق) |
| عميلان على خادم الاختبار | `G3_TWO_CLIENTS_EVIDENCE.md` (Kestrel حقيقي :5400) | ✅ **PASS** — دورتان كاملتان متزامنتان بجلسات مستقلتين |
| فحص التنسيق | `dotnet format WeraPro.Server.slnf --verify-no-changes` | ✅ نظيف (صفر مخالفات) |
| فحص الاعتماديات | `dotnet list package --vulnerable --include-transitive` | ✅ لا ثغرات عالية/حرجة |

**ملاحظات صدق منهجية:** اختبارات API تعمل على PostgreSQL 16 حقيقي (لا InMemory) عبر WebApplicationFactory والترحيلات تُطبق عند إقلاع الاختبار بنفس مسار الإنتاج؛ «عميلان» إضافية فوق اختبار التكامل أُثبتت عبر **TCP حقيقي على Kestrel** (خيطان مستقلان + نداءات متوازية داخل كل عميل) والتقرير الخام محفوظ.

## 5. الأدلة الوظيفية

- **نطاق الفرع مفروض فعلاً:** كاشير دورُه مقيد بفرع ويدخل بلا نطاق فرع → `403` (اختبار مخصص)؛ وبدخوله بفرعه تمر قراءات الإعدادات.
- **التدوير يكشف السرقة:** بعد refresh واحد، إعادة استخدام رمز التجديد القديم تُرفض 401 (اختبار) — والرموز لا تُخزن إلا SHA-256.
- **الترابط طرفاً لطرف:** `X-Correlation-ID` يولَّد عند العميل (Desktop/اختبار) ويُنقّى إن كان مشوهاً، ويظهر في الاستجابة ويتحقق الاختبار من وجوده في `audit.audit_logs.correlation_id`.
- **حالة الحسم الحوكمي تُقرأ من الخادم:** `GET /api/v1/settings?category=INVENTORY` تعرض `inventory.costing_method=WEIGHTED_AVERAGE · OWNER_CONFIRMED` و`inventory.negative_stock_policy=SUPERVISOR_APPROVAL · OWNER_CONFIRMED` (V012) — و`category=WEIGHT` تعرض D-07 المحسوم (1g/5g) — كلها عبر صلاحية `ops.settings.read` (V014).
- **مضاد brute-force مُثبت:** المصنع ذو الحد 3 يرفض الطلب الرابع 429 مع Retry-After (اختبار معزول).
- **«لا اتصال مباشر بالقاعدة» مفروض بنيوياً:** عقد Desktop ↔ Api فقط؛ أي إدخال لـNpgsql/EF في Desktop يكسر CI فوراً.

## 6. الموانع (التفصيل في BLOCKERS.md)

**B-05 (بيئة Windows)** هو المانع الوحيد النشط لهذه البوابة، وهو **شرط إقفالها النصي**: البوابة تلزم «تسليم صور حقيقية للـShell والدخول وPOS shell» و«Release Build» للعميل — كلاهما يتطلب Windows. ما دُفِع أمام المانع: (1) كود Desktop مكتمل بتصميم G2 المعتمد ومسلَّم مع تعليمات بناء، (2) CI جاهز ببناء windows-latest، (3) كل ما يمكن فحصه من Linux دون Windows مفروض آلياً (البنية، عدم الاتصال المباشر، عدم حجب UI بفحص المحجوبات، العقود). **لا يُدَّعى أي Screenshot ولا بناء Desktop في هذا التقرير.** B-07/B-01 بواباتها لاحقة (G10) ولم تُقفل.

## 7. الأداء

`NOT_MEASURED` في G3 خارج أهداف §21 (المقاسات الملزمة في G13) — ما نُفذ: فهارس exact-match موروثة من خطة G1 (V010) وترحيلات الإقلاع تستغرق <4 ثوان على قاعدة نظيفة، ونبضة العميل كل 30 ثانية أخف طلب ممكن (health/ready). لا أرقام p95 تُدَّعى.

## 8. البيانات والأمان

- صفر بيانات إنتاج؛ قواعد الاختبار محلية (PG 16 على localhost:55432) وكل مستخدمي الاختبار موسومون (`g3.*`/`live.*`) ويُنظفون بعد كل تشغيل.
- كلمات المرور: bcrypt حصراً (المواصفة تسمح Argon2id/bcrypt) — لا نص صريح في أي سجل أو إعداد (اختبار معماري يمنع الأنماط).
- أسرار التوقيع: من البيئة/إعدادات فقط مع تحقق عند الإقلاع (≥32 محرفاً) — مفتاح الاختبار في تكوين المصنع لا في الكود؛ `appsettings.json` بلا أي سر.
- الرموز: وصول قصير (15د) + تجديد hash-only بتدوير وإلغاء عند الخروج (مُختبر)؛ جلسات تُغلق خادمياً (outcome=LOGOUT).
- عدم كشف المعلومات: رسالة فشل دخول واحدة (هل المستخدم موجود أم لا) والتفصيل في التدقيق فقط؛ أخطاء 500 بلا مسارات أو تتبعات في غير بيئة التطوير.

## 9. قرار البوابة

### إقفال البوابة (2026-11-03 · 10:37 بتوقيت القاهرة) — بأمر المالك «اعتمد G3»

> بعد اكتمال G3 بدقة ومراجعة هذا التقرير، أصدر صاحب المنتج الأمر الصريح **«اعتمد G3»** (2026-11-03)، فسُجّل القرار النهائي:
>
> **`APPROVED_BY_OWNER_WITH_B05_CONDITION`** — معتمدة بشرطها المعلق: بناء Release للعميل وصور Shell/الدخول/POS الحقيقية تُسلّم عند أول بيئة Windows (رفع B-05 — التزام مكفول قبل التعميم على بقية الوحدات).
>
> وبالاعتماد **فُتحت G4 (الكتالوج/SKU/الوحدات/العبوات/الباركود) فوراً** بأمر المالك «كمل G4» (2026-11-03).

**الحالة قبل الاعتماد (القسم الأصلي عند العرض):**

**`READY_FOR_OWNER_REVIEW_WITH_B05_CONDITION`** — كل مخرجات G3 المنصوصة بُنيت واختبرت بأدلة قابلة للإعادة (بناء Release نظيف · 56/56 اختباراً · 40/41 قيد قاعدة · عميلان حقيقيان · فحص معماري مفروض)، **وتبقى مسلَّمة شرط البوابة الحرفي «صور حقيقية + Release Build للعميل» معلقة على رفع B-05 (بيئة Windows) الذي طلبه المالك بأمر 2026-11-02 ولم يتوفر بعد في بيئة التطوير الحالية**. لا يُسجَّل «APPROVED» ذاتياً — الاعتماد النهائي قرار صاحب المنتج بعد مراجعة هذا التقرير وتوفير بيئة Windows واستيفاء الشرط المعلق (اتساقاً مع أمر المالك: «ولاتعمدها الى وهي مكتملة بدقة»).

## 10. الطلب

**هل تعتمد G3 بشرطها المعلق (الصور وبناء العميل عند رفع B-05) أم تطلب تعديلات، أم تنتظر رفع B-05 أولاً ثم الاعتماد الكامل؟**

- بالاعتماد (مثل «اعتمد G3»): تُفتح **G4 — الكتالوج/SKU/الوحدات/العبوات/الباركود** فوراً (D-02/D-03 محسومان لها بترحيل V012)، ويبقى التزام صور G3 مكفولاً عند أول بيئة Windows (الترتيب الملزم: قبل التعميم على بقية الوحدات).
- بالتعديل: يبقى العمل داخل G3 (Revision) على أساس الحل القائم.
- يُستحسن مع الاعتماد: **توفير جهاز/VM Windows** (رفع B-05 — يفتح أيضاً لقطات G2 المعلقة)، و(اختياري) حسم **D-13 نهائياً** (التغليف — البنية جاهزة للخيارين).

**الجواب المستلم (2026-11-03 · 10:37 بتوقيت القاهرة):** «اعتمد G3» — اعتماد بالخيار الأول بشرطه المعلق (`APPROVED_BY_OWNER_WITH_B05_CONDITION`)؛ فأُغلقت G3 وفُتحت G4 فوراً بأمر «كمل G4». بيئة Windows (رفع B-05) لم تُوفّر بعد — الالتزام قائم.
