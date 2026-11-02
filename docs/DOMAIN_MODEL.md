# DOMAIN MODEL — نموذج المجال المرجعي (G1)

> **الوثيقة:** WERA-DM-001 · **الإصدار:** 1.0 · **التاريخ:** 2026-11-01
> **المرجع الملزم:** GFM-CSharp 2.4.1 (المعتمدة بأمر المالك «اعتمد GFM 2.4.1» — 2026-11-01) §2 · §4 · §5 · §9 · §12–§16 · §18–§19 · §29–§35
> **التنفيذ الفيزيائي:** `database/migrations/V001..V010` · القاموس الكامل: `DATA_DICTIONARY.md` · المخططات: `ERD/`

---

## 1. مبادئ النمذجة الحاكمة

1. **الهوية منفصلة عن صيغة البيع:** `Product` هوية السلعة؛ `SKU` صيغة البيع الصريحة (`sale_mode`)؛ `PackageDefinition` علاقة تحويل بين صيغتين؛ `Barcode` معرف خارجي يقود إلى SKU واحدة — لا يُدمج أي منها في كيان واحد (بوابة G1: «لا خلط بين Product وSKU والعبوة والباركود»).
2. **الرصيد نتيجة حركات:** كل رصيد مخزوني مشتق من `inv.stock_movements` (Append-only). `stock_balances` تخزين تسريعي يُطابَق بمنظر `v_stock_ledger_check`.
3. **المستند المرحل مجمّد:** الترحيل (`POSTED`) يجمّد الحقول المالية؛ التصحيح بمرتجع/إلغاء/عكس مرتبط بالأصل — أبداً بتعديل صامت أو حذف (§0.1 ق6).
4. **اللقطات عند الترحيل:** كل سطر فاتورة يحمل لقطة سعر/ضريبة/تحويل/تكلفة — التاريخ لا يُعاد حسابه بقيم اليوم (§16 ق6).
5. **المصدر الواحد للحقيقة:** الخادم (Application/Domain) يقرر؛ قاعدة البيانات خط حماية أخير بمشغّلات وقيود رصدناها في اختبارات القيود (§0.1 ق4).
6. **القابلية للتهيئة لا الافتراضات الصامتة:** كل قرار تجاري معلق (D-01..D-16) إعداد في `core.business_settings` موسوم `PENDING_OWNER_CONFIRMATION` — البنية تعمل والقيمة قابلة للحسم.
7. **decimal/NUMERIC فقط** للمال والوزن المؤثر في المال؛ عقد الدقة في `DATA_DICTIONARY.md` §اصطلاحات.

## 2. السياقات المحدودة (Bounded Contexts) ومطابقتها للمخططات

| السياق | المخطط | الجذر التجميعي (Aggregates) | مسؤولية القرار |
|---|---|---|---|
| الكتالوج Catalog | `catalog` | Product · SKU · UnitOfMeasure · PackageDefinition · Barcode · PriceList · VWBarcodeProfile · MediaAsset | تعريف ما يُباع وكيف |
| المخزون Inventory | `inv` | StockMovement (دفتر) · Warehouse · CountSession · TransferOrder · Lot · Serial | حقيقة الكميات وأثرها |
| المشتريات Purchasing | `purch` | PurchaseOrder · PurchaseReceipt · SupplierInvoice · SupplierPayment · SupplierReturn | الالتزامات تجاه الموردين |
| المبيعات Sales/POS | `sales` | Invoice · Refund · Shift · HeldCart · DrawerEvent · Customer | البيع اليومي والورديات |
| المالية Finance | `finance` | TreasuryAccount · Voucher · Expense · CapitalEvent · JournalEntry · CostLayer · ProviderSettlement | النقد والمحاسبة والتكلفة |
| الطباعة Printing | `print` | PrinterProfile · DocumentTemplate · PrintJob · AutoPrintRule · DeviceRegistry | وسائط الطباعة §29–35 |
| الحوكمة Governance | `core` | Branch · Station · User · Role · Permission · Approval · BusinessSettings | النطاقات والصلاحيات والإعدادات |
| التدقيق Audit | `audit` | AuditLog · IdempotencyKey · UserSession | الأثر والصدق التشغيلي |
| التشغيل Ops | `ops` | BackupRecord · ImportJob | النسخ والاستيراد الخلفي |

## 3. الكيانات الرئيسية وقواعدها

### 3.1 الكتالوج — `catalog.products / skus / units / unit_conversions / packages / barcodes / plus / vw_barcode_profiles / price_lists / product_prices / media_assets / product_images`

- **Product**: الهوية (اسم عربي/إنجليزي، فئة، علامة، خصائص تتبع `track_lots/track_serials`). لا سعر ولا باركود هنا.
- **SKU**: صيغة بيع صريحة `sale_mode ∈ {COUNT, WEIGHT, PACK, SERIALIZED}` + وحدة مخزون أساسية `base_uom` + وحدة بيع `sale_uom`. لصيغة WEIGHT: `weight_precision_g/step_g/min/max/tare` وإذنا `allow_manual_weight/allow_amount_target` (D-07).
- **UnitOfMeasure / UnitConversion**: الوحدة منضبطة ببُعدها (COUNT/MASS/VOLUME/LENGTH)؛ التحويل داخل البعد فقط ودون دورات — ممنوعان بمشغّلي حماية (اختبارا T-DDL-03/04).
- **PackageDefinition**: يربط SKU العبوة (PACK) بـSKU المحتوى بعامل `units_per_package` صريح مؤرخ (`valid_from/to`) — مثال §4.3: علبة 12 كيساً.
- **Barcode / PLU / VWBarcodeProfile**: الباركود النشط فريد على القيمة المنمّطة (فهرس فريد جزئي)؛ ملفات الوزن المتغير تعرّف البادئة/الطول/المواضع ورقم التحقق — الصيغة المجهولة تُرفض لا تُخمَّن.
- **ProductPrice**: سعر فعالية زمنية لكل (SKU × قائمة × وحدة) بمعاملة ضريبية؛ `price > 0` إلزامي (لا سعر صفر غير مقصود).
- **MediaAsset/ProductImage**: metadata + `sha256` + مسار تخزين فقط — لا BLOB داخل الصفوف؛ الربط على SKU ثم Product بأولوية، وصورة رئيسية واحدة.

### 3.2 المخزون — `inv.*`

- **StockMovement**: الصف الوحيد للحقيقة. كل حركة: نوع (`OPENING/RECEIVE/SALE/SALE_RETURN/SUPPLIER_RETURN/TRANSFER_OUT/TRANSFER_IN/ADJUSTMENT_IN/ADJUSTMENT_OUT/DAMAGE/EXPIRY`)، اتجاه مشتق مطابق للنوع (CHECK)، كمية أساسية موجبة، **لقطة عامل تحويل**، مرجع مستند مصدر `(source_doc_type, source_doc_id)`، مستخدم وفرع وسبب.
- بيع العلبة (PACK) = حركة واحدة بكمية `units_per_package × الكمية` على SKU المحتوى — أثر واحد مهما تعددت صيغ البيع (اختبار T-FLW-01 أثبت: حركة واحدة بكمية 12).
- **سياسة السالب (D-03)** تُطبَّق في مشغل الإدخال: `BLOCK` يرفض؛ `SUPERVISOR_APPROVAL` يتطلب `approval_id` معتمداً من شخص مختلف؛ `ALLOW` يسمح (اختبارات T-DDL-05/06/07).
- **CountSession/CountLines/StockAdjustment**: الجرد بفارق محسوب، والتسوية باعتماد ثنائي إلزامي وسبب — التسوية السريعة محمية من POS بمستند واعتماد.
- **TransferOrder**: مرحلتا إرسال/استلام بحالتين مستقلتين — حركتان متقابلتان لا حركة واحدة مزدوجة الاتجاه.
- **Lot/Serial**: تفعل فقط لصيغ `track_lots/track_serials` — لا نموذج دفعات مفروض على كل الأصناف.

### 3.3 المبيعات — `sales.*`

- **Invoice**: رقم فريد لكل فرع، وردية ومحطة وكاشير، `tax_mode_snapshot` (D-01)، مجاميع (إجمالي/خصومات سطور/خصم فاتورة/ضريبة/صافي/مدفوع/مرتجع)، `idempotency_key` فريد.
- **InvoiceLine**: لقطات كاملة (سعر/قائمة/معاملة ضريبية/نسبة/تحويل/تكلفة وطريتها) + حقول الوزن الثلاثة: `weight_kg` و`weight_source` و`amount_target` + `invoice_discount_share` (التوزيع الحتمي المُحفوظ لخصم الفاتورة §16 ق5).
- **مصادر الوزن الستة** (§5.3) مذكورة في `weight_source` بالضبط: `SCALE_READING · QUICK_PICK · MANUAL_WEIGHT · AMOUNT_DERIVED · WEIGHT_LABEL · PACK_PREWEIGHED` — والقيد يمنع `amount_target` بغير مصدر `AMOUNT_DERIVED` (T-DDL-27).
- **Payment**: تفويض ≠ تحصيل (`AUTHORIZED/CAPTURED/SETTLED...`)، مرجع مزود بلا PIN/CVV/PAN كامل، ربط بخزينة.
- **Refund**: مرتبط بالفاتورة الأصل إلزامياً، باعتماد مشرف وسبب، وسيلة رد = الأصلية (D-04)، وسطر المرتجع لا يتجاوز المباع (T-DDL-15).
- **Shift**: وردية واحدة مفتوحة لكل محطة (فهرس فريد جزئي — T-DDL-21)، رصيد افتتاحي، متوقع/معدود/فرق محسوب، إغلاق نهائي.
- **HeldCart**: لقطة JSONB قابلة للاستعادة دون تكرار.

### 3.4 المالية — `finance.*`

- **TreasuryAccount/Voucher**: درج/خزينة/بنك/مزود دفع أرصدة منفصلة؛ سند التحويل يلزم بحساب مقابل — أثران متقابلان صافيهما صفر (T-FLW-04) لا إيراد ولا مصروف (§14.2).
- **CostLayer**: حالة WAC لكل (SKU × مخزن) تتحدث مع كل استلام؛ الفاتورة تحفظ `cost_snapshot + cost_method_snapshot` وقت الترحيل (T-FLW-03 أثبت 4.5000 بعد استلامين 4.00/5.00).
- **JournalEntry/Lines**: قيد مزدوج جاهز يعمل بحسب نطاق D-12: توازن مفروض عند الترحيل (T-DDL-18)، فترة مفتوحة فقط (T-DDL-19)، أسطر مجمّدة بعد الترحيل (T-DDL-20)، والإصلاح بعكس مرتبط.
- **Expense/CapitalEvent**: مصروف باعتماد وأثر واحد؛ ورأس مال بأحداث موثقة (مساهمة/سحب/قرض/افتتاحي معتمد — D-14) لا يساوي المبيعات أو الصندوق.
- **ProviderSettlement**: مطابقة مزود الدفع (إجمالي/عمولات/صافي) — فصل التحصيل عن التسوية.

### 3.5 الطباعة (مستحدث GFM 2.4.1) — `print.*`

- **PrinterProfile**: كل حقول §29.7.1 (طابعة/طابور/محطة/فرع/وسيط/عرض اسمي/قابل للطباعة/معاير/DPI/هوامش/محول/آخر فحص/إصدار) + `is_calibrated` شرط التوجيه التلقائي.
- **PrinterDocumentRoles**: تعيين لكل (محطة × دور مستند) — إيصال/مالية/مؤمنة رواتب/A4 تفصيلي/ملصقات.
- **DocumentTemplate**: قالب مستقل لكل (نوع مستند × وسيط) بإصدار محفوظ مع كل مهمة.
- **PrintJob**: مهمة مستقلة لكل مستند بـ`idempotency_key` فريد وحالات §29.9 العشر؛ إعادة الطباعة مهمة جديدة مرتبطة بالأصل بعلامة REPRINT؛ عدم تطابق الوسيط → `HELD_CONFIGURATION_MISMATCH` دون تحويل (T-DDL-32).
- **AutoPrintRule**: سياسة لكل نوع مستند (§29.10) — **والقيد يمنع A4 تلقائياً على مستوى القاعدة** (T-DDL-22).
- **DeviceRegistry**: كل جهاز `NOT_CONFIGURED` حتى فحص فعلي موثق (صدق الحالة §0.1 ق11).

## 4. سجل الثوابت (Invariants) وتغطيتها

| المعرف | الثابت | خط الحماية | الاختبار |
|---|---|---|---|
| INV-CAT-01 | لكل SKU صيغة بيع صريحة لا تُستنتج | CHECK `sale_mode` | T-ENV-01 |
| INV-CAT-02 | لا تحويل بين أبعاد مختلفة | مشغل `tf_unit_conversion_dimension` | T-DDL-03 |
| INV-CAT-03 | لا دورات تحويل | مشغل `tf_unit_conversion_no_cycle` | T-DDL-04 |
| INV-CAT-04 | باركود نشط فريد (Exact Match) | فهرس فريد جزئي | T-DDL-01 |
| INV-CAT-05 | لا سعر صفر لسعر نشط | CHECK `price_positive` | T-DDL-17 |
| INV-CAT-06 | العبوة صيغة PACK/COUNT فقط | مشغل `tf_package_mode_guard` | T-ENV-01 |
| INV-INV-01 | الدفتر Append-only | مشغل `tf_stock_movement_immutable` | T-DDL-08/09 |
| INV-INV-02 | الرصيد = مجموع الدفتر | منظر `v_stock_ledger_check` | T-FLW-05 |
| INV-INV-03 | أثر مخزون واحد لكل بيع | نمذجة السطر/الحركة | T-FLW-01 |
| INV-INV-04 | السالب وفق السياسة (D-03) | مشغل `tf_stock_balance_apply` | T-DDL-05/06/07 |
| INV-SAL-01 | مجاميع الفاتورة = مجموع سطورها | مشغل `tf_invoice_post_validate` | T-DDL-12 |
| INV-SAL-02 | المرحل مجمّد والتصحيح موثق | مشغل `tf_invoice_immutable` | T-DDL-10/11 |
| INV-SAL-03 | المرتجع ≤ المباع ومرتبط بالأصل | مشغل `tf_refund_line_guard` + CHECK | T-DDL-15/28 |
| INV-SAL-04 | مصدر الوزن مسجل ولا يزوَّر | CHECK `line_weight_fields/amount_target_source` | T-DDL-16/27 |
| INV-SAL-05 | لقطة تكلفة عند الترحيل | مشغل `tf_invoice_post_validate` | T-DDL-13 |
| INV-SAL-06 | Idempotency فريد للفاتورة | فهرس فريد جزئي | T-DDL-26/31 |
| INV-SHF-01 | وردية مفتوحة واحدة لكل محطة | فهرس فريد جزئي | T-DDL-21 |
| INV-FIN-01 | القيد متوازن قبل الترحيل | مشغلا `tf_journal_balanced/post_validate` | T-DDL-18 |
| INV-FIN-02 | لا ترحيل في فترة مغلقة | مشغل `tf_journal_period_open` | T-DDL-19 |
| INV-FIN-03 | أسطر القيد المرحل مجمّدة | مشغل `tf_journal_lines_frozen` | T-DDL-20 |
| INV-FIN-04 | التحويل الداخلي لا يولد إيراداً | CHECK `voucher_transfer_accounts` | T-DDL-29/FLW-04 |
| INV-FIN-05 | لا تخصيص سداد فوق قيمته | مشغل مؤجل `tf_payment_allocation_guard` | T-DDL-23 |
| INV-FIN-06 | لا موافقة ذاتية (فصل واجبات) | CHECK `approvals_no_self` | T-DDL-24 |
| INV-PRN-01 | A4 تفصيلي بإجراء صريح فقط | CHECK `a4_never_auto` | T-DDL-22 |
| INV-PRN-02 | لا تحويل وسيط عند الفشل | حالة `HELD_CONFIGURATION_MISMATCH` | T-DDL-32 |
| INV-PRN-03 | عرض قابل للطباعة ≤ الوسيط | CHECK `prn_widths_sane` | T-DDL-25 |
| INV-OPS-01 | نجاح النسخ لا يعلن دون تحقق | عمود `verified_readable` | T-DDL-33 |

## 5. أحداث المجال المقترحة (تُستخدم في G3+ عبر Outbox داخلي إن لزم)

`SalePosted {invoice_id, branch, totals, movements[]}` · `StockMovementRecorded` · `CostLayerUpdated` · `ShiftClosed {variance}` · `RefundPosted` · `ReceiptPosted` · `PrintJobStatusChanged` · `BackupCompletedVerified`.
القاعدة: الحدث يصدر **بعد** نجاح الالتزام، والمستهلكات (التحليلات/الطباعة/الإشعارات) لا تعطل المسار التجاري.

## 6. ترجمة إلى EF Core (تمهيد G3)

- كل مخطط PostgreSQL ↔ وحدة Domain/Application مستقلة؛ لا DbSet عابر للحدود بلا عقد.
- `decimal` C# ↔ `NUMERIC(p,s)` بمطابقة الدقة حرفياً (منع HasPrecision المتساهل).
- المفاتيح `bigint identity` ↔ `long`؛ التوقيت `timestamptz` ↔ `DateTimeOffset` (UTC).
- المشغّلات خط حماية لا بديل عن منطق المجال: EF يكتب، والمشغّلات ترفض المخالفة — لا منطق مزدوج في الطرفين.
- `jsonb` (cart_snapshot/old_values/new_values) ↔ `System.Text.Json` مع تسلسل مغلق.
