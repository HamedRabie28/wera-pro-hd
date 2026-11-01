# DATA DICTIONARY — قاموس البيانات المرجعي (مولّد آلياً)

> **الوثيقة:** WERA-DD-001 · **الإصدار:** 1.0 · **التاريخ:** 2026-11-01
> **المصدر:** مولّد آلياً من قاعدة اختبار حية (`wera_g1_a` — PostgreSQL 16.2 on x86_64-pc-linux-gnu) بعد تطبيق V001..V010
> **القاعدة الحاكمة:** هذا الملف ناتج فحص مباشر لـ pg_catalog — أي تعديل مستقبلي على المخطط يعيد توليده ولا يُحرر يدوياً.

## اصطلاحات الأنواع (عقد الدقة)

| الاستخدام | النوع | الدقة | ملاحظة |
|---|---|---|---|
| المال (أسعار/مبالغ/تكاليف/سداد) | `NUMERIC(19,4)` | 4 منازل | `decimal` في طبقة المجال — ممنوع float (§0.1 ق5) |
| الكميات بالوحدة الأساسية | `NUMERIC(18,6)` | 6 منازل | تغطي العددي الصحيح والوزني الكسري معاً |
| الأوزان (كجم) | `NUMERIC(14,3)` | 3 منازل | دقة 1g وفق توصية D-07 |
| عوامل التحويل | `NUMERIC(18,6)` | 6 منازل | لقطة محفوظة في كل حركة/سطر |
| المعرفات | `BIGINT IDENTITY` | — | معرفات داخلية ثابتة — لا أسماء ولا باركود كمفاتيح (§4.1) |
| التوقيت | `TIMESTAMPTZ` | — | تخزين UTC والعرض بتوقيت الفرع (§16 ق8) |
| النسب الضريبية | `NUMERIC(7,4)` | 4 منازل | مثل 0.1400 |

## المخطط `core` (10 جدولاً)

### core.approvals

> سجل الاعتمادات الثنائية: يمنع القيد موافقة منفذ العملية على نفسه — يخدم D-03/D-04/D-06

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `approval_id` | `bigint` | لا |  |  |
| `entity_type` | `text` | لا |  |  |
| `entity_id` | `bigint` | لا |  |  |
| `requested_by` | `bigint` | لا |  |  |
| `decided_by` | `bigint` | نعم |  |  |
| `decision` | `text` | لا | 'PENDING'::text |  |
| `reason` | `text` | نعم |  |  |
| `requested_at` | `timestamp with time zone` | لا | now() |  |
| `decided_at` | `timestamp with time zone` | نعم |  |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `approvals_decision_check`: CHECK (decision = ANY (ARRAY['PENDING'::text, 'APPROVED'::text, 'REJECTED'::text]))
- `approvals_no_self`: CHECK (decided_by IS NULL OR decided_by <> requested_by)
- `approvals_decided_by_fkey`: FOREIGN KEY (decided_by) REFERENCES core.users(user_id)
- `approvals_requested_by_fkey`: FOREIGN KEY (requested_by) REFERENCES core.users(user_id)
- `approvals_pkey`: PRIMARY KEY (approval_id)

**الفهارس:**

- `idx_approvals_entity`: CREATE INDEX idx_approvals_entity ON core.approvals USING btree (entity_type, entity_id)
- `idx_approvals_pending`: CREATE INDEX idx_approvals_pending ON core.approvals USING btree (decision, requested_at) WHERE (decision = 'PENDING'::text)

</details>

### core.branches

> الفروع: النطاق التنظيمي للصلاحيات والتقارير — العرض الزمني بتوقيت الفرع المعتمد والتخزين UTC

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `branch_id` | `bigint` | لا |  |  |
| `code` | `text` | لا |  |  |
| `name_ar` | `text` | لا |  |  |
| `name_en` | `text` | نعم |  |  |
| `timezone` | `text` | لا | 'Africa/Cairo'::text | معرف منطقة زمنية IANA (افتراضي Africa/Cairo) — يُستخدم للعرض والتقارير لا للتخزين |
| `is_active` | `boolean` | لا | true |  |
| `created_at` | `timestamp with time zone` | لا | now() |  |
| `updated_at` | `timestamp with time zone` | لا | now() |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `branches_timezone_nonempty`: CHECK (length(TRIM(BOTH FROM timezone)) > 0)
- `branches_pkey`: PRIMARY KEY (branch_id)
- `branches_code_key`: UNIQUE (code)

**الفهارس:**

- `branches_code_key`: CREATE UNIQUE INDEX branches_code_key ON core.branches USING btree (code)

</details>

### core.business_settings

> سجل الإعدادات الموحد: القرارات التجارية (ضريبة/تكلفة/سالب/وزن/خصومات/احتفاظ) إعدادات نشاط — لا تُكتب في الكود

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `setting_id` | `bigint` | لا |  |  |
| `setting_key` | `text` | لا |  |  |
| `scope_type` | `text` | لا | 'BUSINESS'::text |  |
| `branch_id` | `bigint` | نعم |  |  |
| `value_text` | `text` | لا |  |  |
| `value_type` | `text` | لا | 'string'::text |  |
| `category` | `text` | لا |  |  |
| `decision_ref` | `text` | نعم |  | مرجع القرار في DECISIONS_REQUIRED (D-xx) — يربط الإعداد بمصدره الحوكمي |
| `confirmation_status` | `text` | لا | 'APPROVED_WITH_SPEC'::text | PENDING_OWNER_CONFIRMATION: قيمة توصية بانتظار حسم المالك (البنية تعمل والقيمة قابلة للتغيير) |
| `description_ar` | `text` | لا |  |  |
| `updated_at` | `timestamp with time zone` | لا | now() |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `business_settings_category_check`: CHECK (category = ANY (ARRAY['TAX'::text, 'INVENTORY'::text, 'PRICING'::text, 'WEIGHT'::text, 'RETURNS'::text, 'CREDIT'::text, 'PAYMENTS'::text, 'OFFLINE'::text, 'RETENTION'::text, 'ACCOUNTING'::text, 'PACKAGING'::text, 'PRINTING'::text, 'GENERAL'::text]))
- `business_settings_confirmation_status_check`: CHECK (confirmation_status = ANY (ARRAY['APPROVED_WITH_SPEC'::text, 'PENDING_OWNER_CONFIRMATION'::text, 'OWNER_CONFIRMED'::text]))
- `business_settings_scope_type_check`: CHECK (scope_type = ANY (ARRAY['BUSINESS'::text, 'BRANCH'::text]))
- `business_settings_value_type_check`: CHECK (value_type = ANY (ARRAY['string'::text, 'integer'::text, 'decimal'::text, 'boolean'::text, 'enum'::text, 'json'::text]))
- `settings_branch_scope`: CHECK ((scope_type = 'BRANCH'::text) = (branch_id IS NOT NULL))
- `business_settings_branch_id_fkey`: FOREIGN KEY (branch_id) REFERENCES core.branches(branch_id)
- `business_settings_pkey`: PRIMARY KEY (setting_id)

**الفهارس:**

- `uq_settings_scope`: CREATE UNIQUE INDEX uq_settings_scope ON core.business_settings USING btree (setting_key, scope_type, COALESCE(branch_id, (0)::bigint))

</details>

### core.permissions

> فهرس الصلاحيات الذري: صلاحيات منفصلة لتعديل السعر والخصم والمرتجع ورد الأموال وإلغاء مستند وفتح الدرج وإغلاق الوردية (§18)

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `permission_id` | `bigint` | لا |  |  |
| `code` | `text` | لا |  |  |
| `domain_area` | `text` | لا |  |  |
| `description_ar` | `text` | لا |  |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `permissions_pkey`: PRIMARY KEY (permission_id)
- `permissions_code_key`: UNIQUE (code)

**الفهارس:**

- `permissions_code_key`: CREATE UNIQUE INDEX permissions_code_key ON core.permissions USING btree (code)

</details>

### core.role_permissions

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `role_id` | `bigint` | لا |  |  |
| `permission_id` | `bigint` | لا |  |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `role_permissions_permission_id_fkey`: FOREIGN KEY (permission_id) REFERENCES core.permissions(permission_id) ON DELETE CASCADE
- `role_permissions_role_id_fkey`: FOREIGN KEY (role_id) REFERENCES core.roles(role_id) ON DELETE CASCADE
- `role_permissions_pkey`: PRIMARY KEY (role_id, permission_id)

</details>

### core.roles

> الأدوار المرجعية: OWNER، MANAGER، SUPERVISOR، CASHIER، STOREKEEPER، ACCOUNTANT، AUDITOR

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `role_id` | `bigint` | لا |  |  |
| `code` | `text` | لا |  |  |
| `name_ar` | `text` | لا |  |  |
| `description` | `text` | نعم |  |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `roles_pkey`: PRIMARY KEY (role_id)
- `roles_code_key`: UNIQUE (code)

**الفهارس:**

- `roles_code_key`: CREATE UNIQUE INDEX roles_code_key ON core.roles USING btree (code)

</details>

### core.schema_migrations

> سجل الترحيلات المطبقة: كل migration يسجل نسخته وبصمته — إعادة تطبيق نسخة معدلة مرفوضة

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `version` | `integer` | لا |  |  |
| `name` | `text` | لا |  |  |
| `checksum` | `text` | لا |  |  |
| `applied_at` | `timestamp with time zone` | لا | now() |  |
| `applied_by` | `text` | لا | CURRENT_USER |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `schema_migrations_pkey`: PRIMARY KEY (version)

</details>

### core.stations

> محطات العمل: كل محطة POS تنتمي لفرع واحد — طابعة الإيصال تُعيَّن لها عبر print.printer_profiles

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `station_id` | `bigint` | لا |  |  |
| `branch_id` | `bigint` | لا |  |  |
| `code` | `text` | لا |  |  |
| `name_ar` | `text` | لا |  |  |
| `is_pos` | `boolean` | لا | true |  |
| `is_active` | `boolean` | لا | true |  |
| `created_at` | `timestamp with time zone` | لا | now() |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `stations_branch_id_fkey`: FOREIGN KEY (branch_id) REFERENCES core.branches(branch_id)
- `stations_pkey`: PRIMARY KEY (station_id)
- `stations_branch_id_code_key`: UNIQUE (branch_id, code)

**الفهارس:**

- `stations_branch_id_code_key`: CREATE UNIQUE INDEX stations_branch_id_code_key ON core.stations USING btree (branch_id, code)

</details>

### core.user_roles

> إسناد الأدوار نطاقه فرع أو كل الفروع — تحقق الخادم من النطاق في كل عملية

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `user_role_id` | `bigint` | لا |  |  |
| `user_id` | `bigint` | لا |  |  |
| `role_id` | `bigint` | لا |  |  |
| `branch_id` | `bigint` | نعم |  |  |
| `granted_by` | `bigint` | نعم |  |  |
| `granted_at` | `timestamp with time zone` | لا | now() |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `user_roles_branch_id_fkey`: FOREIGN KEY (branch_id) REFERENCES core.branches(branch_id)
- `user_roles_granted_by_fkey`: FOREIGN KEY (granted_by) REFERENCES core.users(user_id)
- `user_roles_role_id_fkey`: FOREIGN KEY (role_id) REFERENCES core.roles(role_id) ON DELETE CASCADE
- `user_roles_user_id_fkey`: FOREIGN KEY (user_id) REFERENCES core.users(user_id) ON DELETE CASCADE
- `user_roles_pkey`: PRIMARY KEY (user_role_id)

**الفهارس:**

- `uq_user_roles_scope`: CREATE UNIQUE INDEX uq_user_roles_scope ON core.user_roles USING btree (user_id, role_id, COALESCE(branch_id, (0)::bigint))

</details>

### core.users

> المستخدمون: تخزين hash فقط — ممنوع كتابة كلمات المرور أو الأسرار في السجلات أو الإعدادات (§18)

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `user_id` | `bigint` | لا |  |  |
| `username` | `text` | لا |  |  |
| `display_name_ar` | `text` | لا |  |  |
| `email` | `text` | نعم |  |  |
| `password_hash` | `text` | لا |  |  |
| `is_active` | `boolean` | لا | true |  |
| `created_at` | `timestamp with time zone` | لا | now() |  |
| `updated_at` | `timestamp with time zone` | لا | now() |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `users_pkey`: PRIMARY KEY (user_id)
- `users_username_key`: UNIQUE (username)

**الفهارس:**

- `users_username_key`: CREATE UNIQUE INDEX users_username_key ON core.users USING btree (username)

</details>

## المخطط `catalog` (14 جدولاً)

### catalog.barcodes

> كل باركود يقود إلى SKU واحدة فحسب — البحث عنه Exact Match بفهرس فريد على القيمة المنمّطة النشطة، لا بحث نصي عام

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `barcode_id` | `bigint` | لا |  |  |
| `sku_id` | `bigint` | لا |  |  |
| `barcode_value` | `text` | لا |  |  |
| `barcode_normalized` | `text` | لا |  | قيمة موحّدة (trim + أرقام فقط للـ EAN/UPC) — فهرس فريد جزئي على الحالة النشطة يفرض نطاق التفرد |
| `barcode_type` | `text` | لا |  |  |
| `status` | `text` | لا | 'ACTIVE'::text |  |
| `is_printed_label` | `boolean` | لا | false |  |
| `created_at` | `timestamp with time zone` | لا | now() |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `barcodes_barcode_type_check`: CHECK (barcode_type = ANY (ARRAY['EAN13'::text, 'EAN8'::text, 'UPC_A'::text, 'UPC_E'::text, 'CODE128'::text, 'CODE39'::text, 'GS1_128'::text, 'INTERNAL'::text]))
- `barcodes_status_check`: CHECK (status = ANY (ARRAY['ACTIVE'::text, 'INACTIVE'::text, 'RETIRED'::text]))
- `barcodes_sku_id_fkey`: FOREIGN KEY (sku_id) REFERENCES catalog.skus(sku_id)
- `barcodes_pkey`: PRIMARY KEY (barcode_id)

**الفهارس:**

- `idx_barcodes_scanner_covering`: CREATE INDEX idx_barcodes_scanner_covering ON catalog.barcodes USING btree (barcode_normalized) INCLUDE (sku_id, barcode_type) WHERE (status = 'ACTIVE'::text)
- `idx_barcodes_sku`: CREATE INDEX idx_barcodes_sku ON catalog.barcodes USING btree (sku_id)
- `uq_barcodes_normalized_active`: CREATE UNIQUE INDEX uq_barcodes_normalized_active ON catalog.barcodes USING btree (barcode_normalized) WHERE (status = 'ACTIVE'::text)

</details>

### catalog.brands

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `brand_id` | `bigint` | لا |  |  |
| `code` | `text` | لا |  |  |
| `name_ar` | `text` | لا |  |  |
| `name_en` | `text` | نعم |  |  |
| `is_active` | `boolean` | لا | true |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `brands_pkey`: PRIMARY KEY (brand_id)
- `brands_code_key`: UNIQUE (code)

**الفهارس:**

- `brands_code_key`: CREATE UNIQUE INDEX brands_code_key ON catalog.brands USING btree (code)

</details>

### catalog.categories

> شجرة الفئات: تعيين ذاتي الأب ممنوع — الدورة العميقة تُمنع في طبقة المجال وتُختبر في T-CAT-006

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `category_id` | `bigint` | لا |  |  |
| `parent_id` | `bigint` | نعم |  |  |
| `code` | `text` | لا |  |  |
| `name_ar` | `text` | لا |  |  |
| `name_en` | `text` | نعم |  |  |
| `is_active` | `boolean` | لا | true |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `categories_not_self_parent`: CHECK (parent_id IS NULL OR parent_id <> category_id)
- `categories_parent_id_fkey`: FOREIGN KEY (parent_id) REFERENCES catalog.categories(category_id)
- `categories_pkey`: PRIMARY KEY (category_id)
- `categories_code_key`: UNIQUE (code)

**الفهارس:**

- `categories_code_key`: CREATE UNIQUE INDEX categories_code_key ON catalog.categories USING btree (code)

</details>

### catalog.media_assets

> أصول الوسائط: بيانات وصفية وبصمة ومسار تخزين فقط — الصورة الأصلية ليست BLOB داخل صف المنتج (§3.1)

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `media_id` | `bigint` | لا |  |  |
| `storage_key` | `text` | لا |  |  |
| `sha256` | `text` | لا |  |  |
| `mime_type` | `text` | لا |  |  |
| `width_px` | `integer` | نعم |  |  |
| `height_px` | `integer` | نعم |  |  |
| `size_bytes` | `bigint` | لا |  |  |
| `thumb_key` | `text` | نعم |  |  |
| `uploaded_by` | `bigint` | نعم |  |  |
| `created_at` | `timestamp with time zone` | لا | now() |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `media_assets_size_bytes_check`: CHECK (size_bytes > 0)
- `media_assets_uploaded_by_fkey`: FOREIGN KEY (uploaded_by) REFERENCES core.users(user_id)
- `media_assets_pkey`: PRIMARY KEY (media_id)
- `media_assets_storage_key_key`: UNIQUE (storage_key)

**الفهارس:**

- `idx_media_sha256`: CREATE INDEX idx_media_sha256 ON catalog.media_assets USING btree (sha256)
- `media_assets_storage_key_key`: CREATE UNIQUE INDEX media_assets_storage_key_key ON catalog.media_assets USING btree (storage_key)

</details>

### catalog.packages

> تعريف العبوة: بيع العلبة يخصم units_per_package من رصيد المحتوى — أثر مخزون واحد فقط مهما اختلف شكل البيع (§4.3 ق5)

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `package_id` | `bigint` | لا |  |  |
| `package_sku_id` | `bigint` | لا |  |  |
| `content_sku_id` | `bigint` | لا |  |  |
| `units_per_package` | `numeric(18,6)` | لا |  |  |
| `open_package_allowed` | `boolean` | لا | true |  |
| `valid_from` | `timestamp with time zone` | لا | now() |  |
| `valid_to` | `timestamp with time zone` | نعم |  | إغلاق التعريف المؤرخ لا يغيّر المستندات التاريخية — التعديل = إصدار جديد يؤثر في المعاملات الجديدة فقط |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `pkg_not_self`: CHECK (package_sku_id <> content_sku_id)
- `pkg_units_positive`: CHECK (units_per_package > 0::numeric)
- `packages_content_sku_id_fkey`: FOREIGN KEY (content_sku_id) REFERENCES catalog.skus(sku_id)
- `packages_package_sku_id_fkey`: FOREIGN KEY (package_sku_id) REFERENCES catalog.skus(sku_id)
- `packages_pkey`: PRIMARY KEY (package_id)
- `packages_package_sku_id_key`: UNIQUE (package_sku_id)

**الفهارس:**

- `idx_packages_content`: CREATE INDEX idx_packages_content ON catalog.packages USING btree (content_sku_id)
- `packages_package_sku_id_key`: CREATE UNIQUE INDEX packages_package_sku_id_key ON catalog.packages USING btree (package_sku_id)

</details>

### catalog.plus

> أكواد PLU للميزان والملصقات — تربط الكود بالـSKU الوزنية مباشرة

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `plu_id` | `bigint` | لا |  |  |
| `plu_code` | `text` | لا |  |  |
| `sku_id` | `bigint` | لا |  |  |
| `is_active` | `boolean` | لا | true |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `plus_sku_id_fkey`: FOREIGN KEY (sku_id) REFERENCES catalog.skus(sku_id)
- `plus_pkey`: PRIMARY KEY (plu_id)
- `plus_plu_code_key`: UNIQUE (plu_code)

**الفهارس:**

- `plus_plu_code_key`: CREATE UNIQUE INDEX plus_plu_code_key ON catalog.plus USING btree (plu_code)

</details>

### catalog.price_lists

> قوائم الأسعار: العملة إعداد لكل قائمة (EGP افتراضياً) — لا تجميع بين عملات دون تحويل موثق (§15.4)

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `price_list_id` | `bigint` | لا |  |  |
| `code` | `text` | لا |  |  |
| `name_ar` | `text` | لا |  |  |
| `currency` | `text` | لا | 'EGP'::text |  |
| `is_default` | `boolean` | لا | false |  |
| `is_active` | `boolean` | لا | true |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `price_lists_pkey`: PRIMARY KEY (price_list_id)
- `price_lists_code_key`: UNIQUE (code)

**الفهارس:**

- `price_lists_code_key`: CREATE UNIQUE INDEX price_lists_code_key ON catalog.price_lists USING btree (code)

</details>

### catalog.product_images

> ربط الصور: أولوية الربط على مستوى SKU ثم المنتج — إزالة الربط لا تحذف الأصل إن كان مستخدماً في موضع آخر (§4.4 ح)

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `link_id` | `bigint` | لا |  |  |
| `product_id` | `bigint` | نعم |  |  |
| `sku_id` | `bigint` | نعم |  |  |
| `media_id` | `bigint` | لا |  |  |
| `sort_order` | `integer` | لا | 0 |  |
| `is_primary` | `boolean` | لا | false |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `img_one_target`: CHECK (num_nonnulls(product_id, sku_id) = 1)
- `product_images_media_id_fkey`: FOREIGN KEY (media_id) REFERENCES catalog.media_assets(media_id)
- `product_images_product_id_fkey`: FOREIGN KEY (product_id) REFERENCES catalog.products(product_id)
- `product_images_sku_id_fkey`: FOREIGN KEY (sku_id) REFERENCES catalog.skus(sku_id)
- `product_images_pkey`: PRIMARY KEY (link_id)

**الفهارس:**

- `uq_product_images_primary_product`: CREATE UNIQUE INDEX uq_product_images_primary_product ON catalog.product_images USING btree (product_id) WHERE (is_primary AND (product_id IS NOT NULL))
- `uq_product_images_primary_sku`: CREATE UNIQUE INDEX uq_product_images_primary_sku ON catalog.product_images USING btree (sku_id) WHERE (is_primary AND (sku_id IS NOT NULL))

</details>

### catalog.product_prices

> السعر فعالية زمنية: التطبيق يقرأ السعر الساري في لحظة البيع ويحفظ لقطة السعر في سطر الفاتورة (§16 ق6)

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `price_id` | `bigint` | لا |  |  |
| `sku_id` | `bigint` | لا |  |  |
| `price_list_id` | `bigint` | لا |  |  |
| `unit_id` | `bigint` | لا |  |  |
| `unit_price` | `numeric(19,4)` | لا |  |  |
| `tax_treatment` | `text` | لا | 'TAXABLE'::text |  |
| `valid_from` | `timestamp with time zone` | لا | now() |  |
| `valid_to` | `timestamp with time zone` | نعم |  |  |
| `created_at` | `timestamp with time zone` | لا | now() |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `price_positive`: CHECK (unit_price > 0::numeric)
- `price_validity`: CHECK (valid_to IS NULL OR valid_to > valid_from)
- `product_prices_tax_treatment_check`: CHECK (tax_treatment = ANY (ARRAY['TAXABLE'::text, 'ZERO_RATED'::text, 'EXEMPT'::text]))
- `product_prices_price_list_id_fkey`: FOREIGN KEY (price_list_id) REFERENCES catalog.price_lists(price_list_id)
- `product_prices_sku_id_fkey`: FOREIGN KEY (sku_id) REFERENCES catalog.skus(sku_id)
- `product_prices_unit_id_fkey`: FOREIGN KEY (unit_id) REFERENCES catalog.units(uom_id)
- `product_prices_pkey`: PRIMARY KEY (price_id)

**الفهارس:**

- `idx_prices_lookup`: CREATE INDEX idx_prices_lookup ON catalog.product_prices USING btree (sku_id, price_list_id, valid_from) WHERE (valid_to IS NULL)
- `uq_product_prices_sku_list_from`: CREATE UNIQUE INDEX uq_product_prices_sku_list_from ON catalog.product_prices USING btree (sku_id, price_list_id, valid_from)

</details>

### catalog.products

> المنتج الأساسي = الهوية فقط (اسم/فئة/علامة/خصائص تتبع) — الأسعار والباركود وصيغ البيع كلها على SKUs

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `product_id` | `bigint` | لا |  |  |
| `code` | `text` | لا |  |  |
| `name_ar` | `text` | لا |  |  |
| `name_en` | `text` | نعم |  |  |
| `category_id` | `bigint` | لا |  |  |
| `brand_id` | `bigint` | نعم |  |  |
| `description` | `text` | نعم |  |  |
| `status` | `text` | لا | 'ACTIVE'::text |  |
| `track_lots` | `boolean` | لا | false | تشغيل تتبع الدفعة/الصلاحية — عند TRUE تصبح حركات الاستلام ملزمة بـ inv.lots |
| `track_serials` | `boolean` | لا | false |  |
| `created_at` | `timestamp with time zone` | لا | now() |  |
| `updated_at` | `timestamp with time zone` | لا | now() |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `products_status_check`: CHECK (status = ANY (ARRAY['ACTIVE'::text, 'INACTIVE'::text, 'DISCONTINUED'::text]))
- `products_brand_id_fkey`: FOREIGN KEY (brand_id) REFERENCES catalog.brands(brand_id)
- `products_category_id_fkey`: FOREIGN KEY (category_id) REFERENCES catalog.categories(category_id)
- `products_pkey`: PRIMARY KEY (product_id)
- `products_code_key`: UNIQUE (code)

**الفهارس:**

- `idx_products_brand`: CREATE INDEX idx_products_brand ON catalog.products USING btree (brand_id)
- `idx_products_category`: CREATE INDEX idx_products_category ON catalog.products USING btree (category_id) WHERE (status = 'ACTIVE'::text)
- `idx_products_name_ar`: CREATE INDEX idx_products_name_ar ON catalog.products USING btree (lower(name_ar))
- `idx_products_status`: CREATE INDEX idx_products_status ON catalog.products USING btree (status)
- `idx_products_updated`: CREATE INDEX idx_products_updated ON catalog.products USING btree (updated_at)
- `products_code_key`: CREATE UNIQUE INDEX products_code_key ON catalog.products USING btree (code)

</details>

### catalog.skus

> SKU = صيغة قابلة للبيع بsale_mode صريح: فرد/كيس/علبة/كرتونة/وزن — للمنتج الواحد عدة SKUs لكل منها باركود وسعر مستقلان

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `sku_id` | `bigint` | لا |  |  |
| `product_id` | `bigint` | لا |  |  |
| `sku_code` | `text` | لا |  |  |
| `sale_mode` | `text` | لا |  | COUNT عددي · WEIGHT وزني متغير · PACK عبوة بعامل تحويل معروف · SERIALIZED يتطلب رقم تسلسلي لكل وحدة |
| `base_uom_id` | `bigint` | لا |  |  |
| `sale_uom_id` | `bigint` | لا |  |  |
| `status` | `text` | لا | 'ACTIVE'::text |  |
| `weight_precision_g` | `integer` | نعم |  |  |
| `weight_step_g` | `integer` | نعم |  |  |
| `allow_manual_weight` | `boolean` | لا | true |  |
| `allow_amount_target` | `boolean` | لا | true |  |
| `min_weight_kg` | `numeric(14,3)` | نعم |  |  |
| `max_weight_kg` | `numeric(14,3)` | نعم |  |  |
| `default_tare_kg` | `numeric(14,3)` | لا | 0 |  |
| `reorder_level` | `numeric(18,6)` | لا | 0 |  |
| `allow_negative` | `boolean` | نعم |  | تفريد سياسة السالب لهذا الصنف (NULL = يتبع إعداد النشاط inventory.negative_stock_policy — D-03) |
| `created_at` | `timestamp with time zone` | لا | now() |  |
| `updated_at` | `timestamp with time zone` | لا | now() |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `sku_tare_nonnegative`: CHECK (default_tare_kg >= 0::numeric)
- `sku_weight_bounds`: CHECK (min_weight_kg IS NULL OR max_weight_kg IS NULL OR min_weight_kg < max_weight_kg)
- `sku_weight_mode_fields`: CHECK (sale_mode <> 'WEIGHT'::text OR weight_precision_g IS NOT NULL AND weight_step_g IS NOT NULL AND weight_precision_g > 0 AND weight_step_g > 0 AND weight_step_g >= weight_precision_g)
- `sku_weight_positive`: CHECK (min_weight_kg IS NULL OR min_weight_kg > 0::numeric)
- `skus_sale_mode_check`: CHECK (sale_mode = ANY (ARRAY['COUNT'::text, 'WEIGHT'::text, 'PACK'::text, 'SERIALIZED'::text]))
- `skus_status_check`: CHECK (status = ANY (ARRAY['ACTIVE'::text, 'INACTIVE'::text, 'RETIRED'::text]))
- `skus_base_uom_id_fkey`: FOREIGN KEY (base_uom_id) REFERENCES catalog.units(uom_id)
- `skus_product_id_fkey`: FOREIGN KEY (product_id) REFERENCES catalog.products(product_id)
- `skus_sale_uom_id_fkey`: FOREIGN KEY (sale_uom_id) REFERENCES catalog.units(uom_id)
- `skus_pkey`: PRIMARY KEY (sku_id)
- `skus_sku_code_key`: UNIQUE (sku_code)

**الفهارس:**

- `idx_skus_mode`: CREATE INDEX idx_skus_mode ON catalog.skus USING btree (sale_mode) WHERE (status = 'ACTIVE'::text)
- `idx_skus_product`: CREATE INDEX idx_skus_product ON catalog.skus USING btree (product_id)
- `idx_skus_status`: CREATE INDEX idx_skus_status ON catalog.skus USING btree (status)
- `skus_sku_code_key`: CREATE UNIQUE INDEX skus_sku_code_key ON catalog.skus USING btree (sku_code)

</details>

### catalog.unit_conversions

> التحويلات الصريحة: الكمية في وحدة المصدر × factor = الكمية بالوحدة الأساسية — عاملة التحويل دائماً موجبة

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `conversion_id` | `bigint` | لا |  |  |
| `from_uom_id` | `bigint` | لا |  |  |
| `to_uom_id` | `bigint` | لا |  |  |
| `factor` | `numeric(18,6)` | لا |  |  |
| `is_fixed` | `boolean` | لا | true |  |
| `created_at` | `timestamp with time zone` | لا | now() |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `conv_factor_positive`: CHECK (factor > 0::numeric)
- `conv_from_neq_to`: CHECK (from_uom_id <> to_uom_id)
- `unit_conversions_from_uom_id_fkey`: FOREIGN KEY (from_uom_id) REFERENCES catalog.units(uom_id)
- `unit_conversions_to_uom_id_fkey`: FOREIGN KEY (to_uom_id) REFERENCES catalog.units(uom_id)
- `unit_conversions_pkey`: PRIMARY KEY (conversion_id)

</details>

### catalog.units

> وحدات القياس: كل وحدة منضبطة ببعدها — التحويل مسموح فقط داخل البعد نفسه (كجم→جرام نعم، كجم→لتر لا)

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `uom_id` | `bigint` | لا |  |  |
| `code` | `text` | لا |  |  |
| `name_ar` | `text` | لا |  |  |
| `dimension` | `text` | لا |  |  |
| `is_base` | `boolean` | لا | false |  |
| `decimals` | `smallint` | لا | 3 |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `units_decimals_check`: CHECK (decimals >= 0 AND decimals <= 6)
- `units_dimension_check`: CHECK (dimension = ANY (ARRAY['COUNT'::text, 'MASS'::text, 'VOLUME'::text, 'LENGTH'::text]))
- `units_pkey`: PRIMARY KEY (uom_id)
- `units_code_key`: UNIQUE (code)

**الفهارس:**

- `units_code_key`: CREATE UNIQUE INDEX units_code_key ON catalog.units USING btree (code)

</details>

### catalog.vw_barcode_profiles

> ملف باركود الوزن/السعر المتغير: البادئة والطول ومواضع PLU والقيمة ورقم التحقق — الصيغة المجهولة تُرفض لا تُخمَّن (§5.3 ق6)

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `profile_id` | `bigint` | لا |  |  |
| `name` | `text` | لا |  |  |
| `prefix` | `text` | لا |  |  |
| `total_length` | `smallint` | لا |  |  |
| `plu_start_pos` | `smallint` | لا |  |  |
| `plu_length` | `smallint` | لا |  |  |
| `value_type` | `text` | لا |  |  |
| `value_start_pos` | `smallint` | لا |  |  |
| `value_length` | `smallint` | لا |  |  |
| `value_decimals` | `smallint` | لا | 3 |  |
| `value_unit_id` | `bigint` | نعم |  |  |
| `check_digit_rule` | `text` | لا | 'NONE'::text |  |
| `is_active` | `boolean` | لا | true |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `vw_barcode_profiles_check_digit_rule_check`: CHECK (check_digit_rule = ANY (ARRAY['NONE'::text, 'MOD10'::text, 'MOD10_WEIGHTED'::text]))
- `vw_barcode_profiles_value_decimals_check`: CHECK (value_decimals >= 0 AND value_decimals <= 3)
- `vw_barcode_profiles_value_type_check`: CHECK (value_type = ANY (ARRAY['WEIGHT'::text, 'PRICE'::text]))
- `vw_segments_valid`: CHECK (plu_start_pos > 0 AND value_start_pos > 0 AND plu_length > 0 AND value_length > 0 AND (plu_start_pos + plu_length) <= total_length AND (value_start_pos + value_length) <= total_length AND total_length >= 8 AND total_length <= 20)
- `vw_barcode_profiles_value_unit_id_fkey`: FOREIGN KEY (value_unit_id) REFERENCES catalog.units(uom_id)
- `vw_barcode_profiles_pkey`: PRIMARY KEY (profile_id)

</details>

## المخطط `inv` (11 جدولاً)

### inv.count_lines

> أسطر الجرد: الفرق محسوب عمودياً — لا يُدخل يدوياً

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `line_id` | `bigint` | لا |  |  |
| `count_id` | `bigint` | لا |  |  |
| `sku_id` | `bigint` | لا |  |  |
| `system_qty` | `numeric(18,6)` | لا |  |  |
| `counted_qty` | `numeric(18,6)` | لا |  |  |
| `variance_qty` | `numeric(18,6)` | نعم | (counted_qty - system_qty) |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `count_lines_count_id_fkey`: FOREIGN KEY (count_id) REFERENCES inv.count_sessions(count_id) ON DELETE CASCADE
- `count_lines_sku_id_fkey`: FOREIGN KEY (sku_id) REFERENCES catalog.skus(sku_id)
- `count_lines_pkey`: PRIMARY KEY (line_id)
- `count_lines_count_id_sku_id_key`: UNIQUE (count_id, sku_id)

**الفهارس:**

- `count_lines_count_id_sku_id_key`: CREATE UNIQUE INDEX count_lines_count_id_sku_id_key ON inv.count_lines USING btree (count_id, sku_id)

</details>

### inv.count_sessions

> جرد فعلي بمقارنة الرصيد الفعلي والمتوقع — الاعتماد من غير الجارّ (فصل واجبات)

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `count_id` | `bigint` | لا |  |  |
| `warehouse_id` | `bigint` | لا |  |  |
| `status` | `text` | لا | 'OPEN'::text |  |
| `counted_by` | `bigint` | لا |  |  |
| `approved_by` | `bigint` | نعم |  |  |
| `opened_at` | `timestamp with time zone` | لا | now() |  |
| `closed_at` | `timestamp with time zone` | نعم |  |  |
| `notes` | `text` | نعم |  |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `count_approval_distinct`: CHECK (approved_by IS NULL OR approved_by <> counted_by)
- `count_sessions_status_check`: CHECK (status = ANY (ARRAY['OPEN'::text, 'COUNTED'::text, 'RECONCILED'::text, 'APPROVED'::text, 'CANCELLED'::text]))
- `count_sessions_approved_by_fkey`: FOREIGN KEY (approved_by) REFERENCES core.users(user_id)
- `count_sessions_counted_by_fkey`: FOREIGN KEY (counted_by) REFERENCES core.users(user_id)
- `count_sessions_warehouse_id_fkey`: FOREIGN KEY (warehouse_id) REFERENCES inv.warehouses(warehouse_id)
- `count_sessions_pkey`: PRIMARY KEY (count_id)

</details>

### inv.lots

> الدفعات: تُستخدم فقط للأصناف المفعّل لديها track_lots — الدفعات الأقدم تباع أولًا عند اعتماد FIFO لاحقاً (D-02)

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `lot_id` | `bigint` | لا |  |  |
| `sku_id` | `bigint` | لا |  |  |
| `lot_code` | `text` | لا |  |  |
| `expiry_date` | `date` | نعم |  |  |
| `received_at` | `timestamp with time zone` | لا | now() |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `lot_expiry_sane`: CHECK (expiry_date IS NULL OR expiry_date > '2000-01-01'::date)
- `lots_sku_id_fkey`: FOREIGN KEY (sku_id) REFERENCES catalog.skus(sku_id)
- `lots_pkey`: PRIMARY KEY (lot_id)
- `lots_sku_id_lot_code_key`: UNIQUE (sku_id, lot_code)

**الفهارس:**

- `lots_sku_id_lot_code_key`: CREATE UNIQUE INDEX lots_sku_id_lot_code_key ON inv.lots USING btree (sku_id, lot_code)

</details>

### inv.serials

> أرقام تسلسلية للأصناف SERIALIZED — كل وحدة سجل مستقل بحالة دورة حياتها

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `serial_id` | `bigint` | لا |  |  |
| `sku_id` | `bigint` | لا |  |  |
| `serial_no` | `text` | لا |  |  |
| `status` | `text` | لا | 'IN_STOCK'::text |  |
| `current_warehouse_id` | `bigint` | نعم |  |  |
| `last_movement_id` | `bigint` | نعم |  |  |
| `created_at` | `timestamp with time zone` | لا | now() |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `serials_status_check`: CHECK (status = ANY (ARRAY['IN_STOCK'::text, 'SOLD'::text, 'RETURNED'::text, 'SCRAPPED'::text, 'LOST'::text]))
- `serials_current_warehouse_id_fkey`: FOREIGN KEY (current_warehouse_id) REFERENCES inv.warehouses(warehouse_id)
- `serials_sku_id_fkey`: FOREIGN KEY (sku_id) REFERENCES catalog.skus(sku_id)
- `serials_pkey`: PRIMARY KEY (serial_id)
- `serials_sku_id_serial_no_key`: UNIQUE (sku_id, serial_no)

**الفهارس:**

- `serials_sku_id_serial_no_key`: CREATE UNIQUE INDEX serials_sku_id_serial_no_key ON inv.serials USING btree (sku_id, serial_no)

</details>

### inv.stock_adjustments

> التسويات: اعتماد ثنائي إلزامي وسبب مكتوب — الترحيل ينشئ حركتي ADJUSTMENT في الدفتر ويطابق الرصيد

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `adjustment_id` | `bigint` | لا |  |  |
| `count_id` | `bigint` | نعم |  |  |
| `warehouse_id` | `bigint` | لا |  |  |
| `sku_id` | `bigint` | لا |  |  |
| `new_qty` | `numeric(18,6)` | لا |  |  |
| `reason` | `text` | لا |  |  |
| `approval_id` | `bigint` | نعم |  |  |
| `posted_at` | `timestamp with time zone` | نعم |  |  |
| `created_by` | `bigint` | لا |  |  |
| `created_at` | `timestamp with time zone` | لا | now() |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `adjust_approval_required`: CHECK (approval_id IS NOT NULL)
- `stock_adjustments_reason_check`: CHECK (length(TRIM(BOTH FROM reason)) >= 3)
- `stock_adjustments_approval_id_fkey`: FOREIGN KEY (approval_id) REFERENCES core.approvals(approval_id)
- `stock_adjustments_count_id_fkey`: FOREIGN KEY (count_id) REFERENCES inv.count_sessions(count_id)
- `stock_adjustments_created_by_fkey`: FOREIGN KEY (created_by) REFERENCES core.users(user_id)
- `stock_adjustments_sku_id_fkey`: FOREIGN KEY (sku_id) REFERENCES catalog.skus(sku_id)
- `stock_adjustments_warehouse_id_fkey`: FOREIGN KEY (warehouse_id) REFERENCES inv.warehouses(warehouse_id)
- `stock_adjustments_pkey`: PRIMARY KEY (adjustment_id)

</details>

### inv.stock_balances

> الرصيد المخزن للاستعلام السريع — مصدر الحقيقة هو الدفتر، والمطابقة بينهما تُختبر (T-INV-002)

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `balance_id` | `bigint` | لا |  |  |
| `sku_id` | `bigint` | لا |  |  |
| `warehouse_id` | `bigint` | لا |  |  |
| `base_qty` | `numeric(18,6)` | لا | 0 |  |
| `reserved_qty` | `numeric(18,6)` | لا | 0 | المحجوز لطلبات/سلال مفتوحة — المتاح = base_qty - reserved_qty |
| `last_movement_at` | `timestamp with time zone` | نعم |  |  |
| `updated_at` | `timestamp with time zone` | لا | now() |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `stock_balances_reserved_qty_check`: CHECK (reserved_qty >= 0::numeric)
- `stock_balances_sku_id_fkey`: FOREIGN KEY (sku_id) REFERENCES catalog.skus(sku_id)
- `stock_balances_warehouse_id_fkey`: FOREIGN KEY (warehouse_id) REFERENCES inv.warehouses(warehouse_id)
- `stock_balances_pkey`: PRIMARY KEY (balance_id)
- `stock_balances_sku_id_warehouse_id_key`: UNIQUE (sku_id, warehouse_id)

**الفهارس:**

- `idx_balances_sku`: CREATE INDEX idx_balances_sku ON inv.stock_balances USING btree (sku_id)
- `idx_balances_wh`: CREATE INDEX idx_balances_wh ON inv.stock_balances USING btree (warehouse_id)
- `stock_balances_sku_id_warehouse_id_key`: CREATE UNIQUE INDEX stock_balances_sku_id_warehouse_id_key ON inv.stock_balances USING btree (sku_id, warehouse_id)

</details>

### inv.stock_movements

> دفتر حركة المخزون: كل صف حقيقة موثقة بمصدرها (مستند مرجعي) — الإدخال فقط؛ التعديل والحذف ممنوعان بالمشغّل أدناه

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `movement_id` | `bigint` | لا |  |  |
| `sku_id` | `bigint` | لا |  |  |
| `warehouse_id` | `bigint` | لا |  |  |
| `movement_type` | `text` | لا |  |  |
| `direction` | `smallint` | لا |  |  |
| `base_qty` | `numeric(18,6)` | لا |  | الكمية بالوحدة الأساسية موجبة دائماً والاتجاه في direction — بيع علبة 12 كيساً = صف واحد بكمية 12 (أثر واحد §4.3 ق5) |
| `source_uom_id` | `bigint` | لا |  |  |
| `conversion_factor` | `numeric(18,6)` | لا |  | لقطة عامل التحويل وقت الحركة — تعديل تعريف العبوة لاحقاً لا يعيد حساب التاريخ (§4.3 ق9) |
| `source_doc_type` | `text` | لا |  |  |
| `source_doc_id` | `bigint` | لا |  |  |
| `source_line_ref` | `text` | نعم |  |  |
| `lot_id` | `bigint` | نعم |  |  |
| `approval_id` | `bigint` | نعم |  |  |
| `reason` | `text` | نعم |  |  |
| `branch_id` | `bigint` | لا |  |  |
| `user_id` | `bigint` | لا |  |  |
| `occurred_at` | `timestamp with time zone` | لا | now() |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `mov_adjustment_reason`: CHECK ((movement_type <> ALL (ARRAY['ADJUSTMENT_IN'::text, 'ADJUSTMENT_OUT'::text, 'DAMAGE'::text, 'EXPIRY'::text])) OR length(TRIM(BOTH FROM COALESCE(reason, ''::text))) >= 3)
- `mov_direction_type`: CHECK (direction = 1 AND (movement_type = ANY (ARRAY['OPENING'::text, 'RECEIVE'::text, 'SALE_RETURN'::text, 'TRANSFER_IN'::text, 'ADJUSTMENT_IN'::text])) OR direction = '-1'::integer AND (movement_type = ANY (ARRAY['SALE'::text, 'SUPPLIER_RETURN'::text, 'TRANSFER_OUT'::text, 'ADJUSTMENT_OUT'::text, 'DAMAGE'::text, 'EXPIRY'::text])))
- `stock_movements_base_qty_check`: CHECK (base_qty > 0::numeric)
- `stock_movements_direction_check`: CHECK (direction = ANY (ARRAY[1, '-1'::integer]))
- `stock_movements_movement_type_check`: CHECK (movement_type = ANY (ARRAY['OPENING'::text, 'RECEIVE'::text, 'SALE'::text, 'SALE_RETURN'::text, 'SUPPLIER_RETURN'::text, 'TRANSFER_OUT'::text, 'TRANSFER_IN'::text, 'ADJUSTMENT_IN'::text, 'ADJUSTMENT_OUT'::text, 'DAMAGE'::text, 'EXPIRY'::text]))
- `stock_movements_approval_id_fkey`: FOREIGN KEY (approval_id) REFERENCES core.approvals(approval_id)
- `stock_movements_branch_id_fkey`: FOREIGN KEY (branch_id) REFERENCES core.branches(branch_id)
- `stock_movements_lot_fk`: FOREIGN KEY (lot_id) REFERENCES inv.lots(lot_id)
- `stock_movements_sku_id_fkey`: FOREIGN KEY (sku_id) REFERENCES catalog.skus(sku_id)
- `stock_movements_source_uom_id_fkey`: FOREIGN KEY (source_uom_id) REFERENCES catalog.units(uom_id)
- `stock_movements_user_id_fkey`: FOREIGN KEY (user_id) REFERENCES core.users(user_id)
- `stock_movements_warehouse_id_fkey`: FOREIGN KEY (warehouse_id) REFERENCES inv.warehouses(warehouse_id)
- `stock_movements_pkey`: PRIMARY KEY (movement_id)

**الفهارس:**

- `idx_movements_lot`: CREATE INDEX idx_movements_lot ON inv.stock_movements USING btree (lot_id) WHERE (lot_id IS NOT NULL)
- `idx_movements_sku_wh_time`: CREATE INDEX idx_movements_sku_wh_time ON inv.stock_movements USING btree (sku_id, warehouse_id, occurred_at DESC)
- `idx_movements_source`: CREATE INDEX idx_movements_source ON inv.stock_movements USING btree (source_doc_type, source_doc_id)

</details>

### inv.stock_reservations

> حجز كميات لمستندات مفتوحة — يُدفع المحجوز عند الترحيل أو يُحرَّر عند الإلغاء

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `reservation_id` | `bigint` | لا |  |  |
| `sku_id` | `bigint` | لا |  |  |
| `warehouse_id` | `bigint` | لا |  |  |
| `qty_base` | `numeric(18,6)` | لا |  |  |
| `source_doc_type` | `text` | لا |  |  |
| `source_doc_id` | `bigint` | لا |  |  |
| `reserved_at` | `timestamp with time zone` | لا | now() |  |
| `released_at` | `timestamp with time zone` | نعم |  |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `stock_reservations_qty_base_check`: CHECK (qty_base > 0::numeric)
- `stock_reservations_sku_id_fkey`: FOREIGN KEY (sku_id) REFERENCES catalog.skus(sku_id)
- `stock_reservations_warehouse_id_fkey`: FOREIGN KEY (warehouse_id) REFERENCES inv.warehouses(warehouse_id)
- `stock_reservations_pkey`: PRIMARY KEY (reservation_id)

**الفهارس:**

- `idx_reservations_doc`: CREATE INDEX idx_reservations_doc ON inv.stock_reservations USING btree (source_doc_type, source_doc_id) WHERE (released_at IS NULL)

</details>

### inv.transfer_lines

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `line_id` | `bigint` | لا |  |  |
| `transfer_id` | `bigint` | لا |  |  |
| `sku_id` | `bigint` | لا |  |  |
| `qty_base` | `numeric(18,6)` | لا |  |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `transfer_lines_qty_base_check`: CHECK (qty_base > 0::numeric)
- `transfer_lines_sku_id_fkey`: FOREIGN KEY (sku_id) REFERENCES catalog.skus(sku_id)
- `transfer_lines_transfer_id_fkey`: FOREIGN KEY (transfer_id) REFERENCES inv.transfer_orders(transfer_id) ON DELETE CASCADE
- `transfer_lines_pkey`: PRIMARY KEY (line_id)
- `transfer_lines_transfer_id_sku_id_key`: UNIQUE (transfer_id, sku_id)

**الفهارس:**

- `transfer_lines_transfer_id_sku_id_key`: CREATE UNIQUE INDEX transfer_lines_transfer_id_sku_id_key ON inv.transfer_lines USING btree (transfer_id, sku_id)

</details>

### inv.transfer_orders

> التحويل بين مخزنين بمرحلتين منفصلتين: خروج SENT ثم دخول RECEIVED — كل مرحلة حركة مستقلة في الدفتر

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `transfer_id` | `bigint` | لا |  |  |
| `from_warehouse_id` | `bigint` | لا |  |  |
| `to_warehouse_id` | `bigint` | لا |  |  |
| `status` | `text` | لا | 'DRAFT'::text |  |
| `sent_at` | `timestamp with time zone` | نعم |  |  |
| `received_at` | `timestamp with time zone` | نعم |  |  |
| `created_by` | `bigint` | لا |  |  |
| `created_at` | `timestamp with time zone` | لا | now() |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `transfer_orders_status_check`: CHECK (status = ANY (ARRAY['DRAFT'::text, 'SENT'::text, 'RECEIVED'::text, 'CANCELLED'::text]))
- `transfer_wh_distinct`: CHECK (from_warehouse_id <> to_warehouse_id)
- `transfer_orders_created_by_fkey`: FOREIGN KEY (created_by) REFERENCES core.users(user_id)
- `transfer_orders_from_warehouse_id_fkey`: FOREIGN KEY (from_warehouse_id) REFERENCES inv.warehouses(warehouse_id)
- `transfer_orders_to_warehouse_id_fkey`: FOREIGN KEY (to_warehouse_id) REFERENCES inv.warehouses(warehouse_id)
- `transfer_orders_pkey`: PRIMARY KEY (transfer_id)

</details>

### inv.warehouses

> المخازن: رئيسي/فرعي/خارجي — كل حركة مخزون مرتبطة بمخزن محدد

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `warehouse_id` | `bigint` | لا |  |  |
| `branch_id` | `bigint` | لا |  |  |
| `code` | `text` | لا |  |  |
| `name_ar` | `text` | لا |  |  |
| `wh_type` | `text` | لا | 'BRANCH'::text |  |
| `is_active` | `boolean` | لا | true |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `warehouses_wh_type_check`: CHECK (wh_type = ANY (ARRAY['MAIN'::text, 'BRANCH'::text, 'SUB'::text, 'EXTERNAL'::text]))
- `warehouses_branch_id_fkey`: FOREIGN KEY (branch_id) REFERENCES core.branches(branch_id)
- `warehouses_pkey`: PRIMARY KEY (warehouse_id)
- `warehouses_code_key`: UNIQUE (code)

**الفهارس:**

- `warehouses_code_key`: CREATE UNIQUE INDEX warehouses_code_key ON inv.warehouses USING btree (code)

</details>

## المخطط `purch` (11 جدولاً)

### purch.purchase_order_lines

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `line_id` | `bigint` | لا |  |  |
| `po_id` | `bigint` | لا |  |  |
| `sku_id` | `bigint` | لا |  |  |
| `qty` | `numeric(18,6)` | لا |  |  |
| `uom_id` | `bigint` | لا |  |  |
| `unit_cost` | `numeric(19,4)` | لا |  |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `purchase_order_lines_qty_check`: CHECK (qty > 0::numeric)
- `purchase_order_lines_unit_cost_check`: CHECK (unit_cost >= 0::numeric)
- `purchase_order_lines_po_id_fkey`: FOREIGN KEY (po_id) REFERENCES purch.purchase_orders(po_id) ON DELETE CASCADE
- `purchase_order_lines_sku_id_fkey`: FOREIGN KEY (sku_id) REFERENCES catalog.skus(sku_id)
- `purchase_order_lines_uom_id_fkey`: FOREIGN KEY (uom_id) REFERENCES catalog.units(uom_id)
- `purchase_order_lines_pkey`: PRIMARY KEY (line_id)
- `purchase_order_lines_po_id_sku_id_key`: UNIQUE (po_id, sku_id)

**الفهارس:**

- `purchase_order_lines_po_id_sku_id_key`: CREATE UNIQUE INDEX purchase_order_lines_po_id_sku_id_key ON purch.purchase_order_lines USING btree (po_id, sku_id)

</details>

### purch.purchase_orders

> أمر الشراء مسودة قابلة للاستعادة — لا حركة مخزون ولا مال قبل الاستلام والسداد

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `po_id` | `bigint` | لا |  |  |
| `po_number` | `text` | لا |  |  |
| `supplier_id` | `bigint` | لا |  |  |
| `warehouse_id` | `bigint` | لا |  |  |
| `status` | `text` | لا | 'DRAFT'::text |  |
| `expected_date` | `date` | نعم |  |  |
| `created_by` | `bigint` | لا |  |  |
| `created_at` | `timestamp with time zone` | لا | now() |  |
| `approved_at` | `timestamp with time zone` | نعم |  |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `purchase_orders_status_check`: CHECK (status = ANY (ARRAY['DRAFT'::text, 'ORDERED'::text, 'PARTIAL'::text, 'RECEIVED'::text, 'CLOSED'::text, 'CANCELLED'::text]))
- `purchase_orders_created_by_fkey`: FOREIGN KEY (created_by) REFERENCES core.users(user_id)
- `purchase_orders_supplier_id_fkey`: FOREIGN KEY (supplier_id) REFERENCES purch.suppliers(supplier_id)
- `purchase_orders_warehouse_id_fkey`: FOREIGN KEY (warehouse_id) REFERENCES inv.warehouses(warehouse_id)
- `purchase_orders_pkey`: PRIMARY KEY (po_id)
- `purchase_orders_po_number_key`: UNIQUE (po_number)

**الفهارس:**

- `purchase_orders_po_number_key`: CREATE UNIQUE INDEX purchase_orders_po_number_key ON purch.purchase_orders USING btree (po_number)

</details>

### purch.purchase_receipt_lines

> سطر الاستلام يحمل لقطة عامل التحويل وتكلفة الوحدة — الاستلام يغذي طبقة التكلفة (finance.cost_layers)

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `line_id` | `bigint` | لا |  |  |
| `receipt_id` | `bigint` | لا |  |  |
| `sku_id` | `bigint` | لا |  |  |
| `qty` | `numeric(18,6)` | لا |  |  |
| `uom_id` | `bigint` | لا |  |  |
| `conversion_factor` | `numeric(18,6)` | لا |  |  |
| `unit_cost` | `numeric(19,4)` | لا |  |  |
| `lot_id` | `bigint` | نعم |  |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `purchase_receipt_lines_qty_check`: CHECK (qty > 0::numeric)
- `purchase_receipt_lines_unit_cost_check`: CHECK (unit_cost >= 0::numeric)
- `purchase_receipt_lines_lot_id_fkey`: FOREIGN KEY (lot_id) REFERENCES inv.lots(lot_id)
- `purchase_receipt_lines_receipt_id_fkey`: FOREIGN KEY (receipt_id) REFERENCES purch.purchase_receipts(receipt_id) ON DELETE CASCADE
- `purchase_receipt_lines_sku_id_fkey`: FOREIGN KEY (sku_id) REFERENCES catalog.skus(sku_id)
- `purchase_receipt_lines_uom_id_fkey`: FOREIGN KEY (uom_id) REFERENCES catalog.units(uom_id)
- `purchase_receipt_lines_pkey`: PRIMARY KEY (line_id)
- `purchase_receipt_lines_receipt_id_sku_id_key`: UNIQUE (receipt_id, sku_id)

**الفهارس:**

- `purchase_receipt_lines_receipt_id_sku_id_key`: CREATE UNIQUE INDEX purchase_receipt_lines_receipt_id_sku_id_key ON purch.purchase_receipt_lines USING btree (receipt_id, sku_id)

</details>

### purch.purchase_receipts

> الاستلام هو المستند الذي ينشئ حركة RECEIVE في دفتر المخزون — لا من فاتورة المورد وحدها

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `receipt_id` | `bigint` | لا |  |  |
| `receipt_number` | `text` | لا |  |  |
| `supplier_id` | `bigint` | لا |  |  |
| `po_id` | `bigint` | نعم |  |  |
| `warehouse_id` | `bigint` | لا |  |  |
| `status` | `text` | لا | 'DRAFT'::text |  |
| `received_by` | `bigint` | لا |  |  |
| `received_at` | `timestamp with time zone` | نعم |  |  |
| `posted_at` | `timestamp with time zone` | نعم |  |  |
| `notes` | `text` | نعم |  |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `purchase_receipts_status_check`: CHECK (status = ANY (ARRAY['DRAFT'::text, 'POSTED'::text, 'CANCELLED'::text]))
- `purchase_receipts_po_id_fkey`: FOREIGN KEY (po_id) REFERENCES purch.purchase_orders(po_id)
- `purchase_receipts_received_by_fkey`: FOREIGN KEY (received_by) REFERENCES core.users(user_id)
- `purchase_receipts_supplier_id_fkey`: FOREIGN KEY (supplier_id) REFERENCES purch.suppliers(supplier_id)
- `purchase_receipts_warehouse_id_fkey`: FOREIGN KEY (warehouse_id) REFERENCES inv.warehouses(warehouse_id)
- `purchase_receipts_pkey`: PRIMARY KEY (receipt_id)
- `purchase_receipts_receipt_number_key`: UNIQUE (receipt_number)

**الفهارس:**

- `purchase_receipts_receipt_number_key`: CREATE UNIQUE INDEX purchase_receipts_receipt_number_key ON purch.purchase_receipts USING btree (receipt_number)

</details>

### purch.supplier_invoice_lines

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `line_id` | `bigint` | لا |  |  |
| `invoice_id` | `bigint` | لا |  |  |
| `sku_id` | `bigint` | لا |  |  |
| `qty` | `numeric(18,6)` | لا |  |  |
| `uom_id` | `bigint` | لا |  |  |
| `unit_cost` | `numeric(19,4)` | لا |  |  |
| `line_total` | `numeric(19,4)` | لا |  |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `supplier_invoice_lines_line_total_check`: CHECK (line_total >= 0::numeric)
- `supplier_invoice_lines_qty_check`: CHECK (qty > 0::numeric)
- `supplier_invoice_lines_unit_cost_check`: CHECK (unit_cost >= 0::numeric)
- `supplier_invoice_lines_invoice_id_fkey`: FOREIGN KEY (invoice_id) REFERENCES purch.supplier_invoices(invoice_id) ON DELETE CASCADE
- `supplier_invoice_lines_sku_id_fkey`: FOREIGN KEY (sku_id) REFERENCES catalog.skus(sku_id)
- `supplier_invoice_lines_uom_id_fkey`: FOREIGN KEY (uom_id) REFERENCES catalog.units(uom_id)
- `supplier_invoice_lines_pkey`: PRIMARY KEY (line_id)

</details>

### purch.supplier_invoices

> فاتورة المورد التزام مالي مرتبط بالاستلام — السداد لا يُسجل مرة كسداد ومرة كمصروف (§12.3 ق3)

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `invoice_id` | `bigint` | لا |  |  |
| `invoice_number` | `text` | لا |  |  |
| `supplier_id` | `bigint` | لا |  |  |
| `receipt_id` | `bigint` | نعم |  |  |
| `subtotal` | `numeric(19,4)` | لا | 0 |  |
| `tax_total` | `numeric(19,4)` | لا | 0 |  |
| `grand_total` | `numeric(19,4)` | لا | 0 |  |
| `status` | `text` | لا | 'DRAFT'::text |  |
| `invoice_date` | `date` | لا |  |  |
| `due_date` | `date` | نعم |  |  |
| `posted_at` | `timestamp with time zone` | نعم |  |  |
| `created_by` | `bigint` | لا |  |  |
| `created_at` | `timestamp with time zone` | لا | now() |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `supplier_invoices_status_check`: CHECK (status = ANY (ARRAY['DRAFT'::text, 'POSTED'::text, 'PARTIALLY_PAID'::text, 'PAID'::text, 'CANCELLED'::text]))
- `supplier_invoices_created_by_fkey`: FOREIGN KEY (created_by) REFERENCES core.users(user_id)
- `supplier_invoices_receipt_id_fkey`: FOREIGN KEY (receipt_id) REFERENCES purch.purchase_receipts(receipt_id)
- `supplier_invoices_supplier_id_fkey`: FOREIGN KEY (supplier_id) REFERENCES purch.suppliers(supplier_id)
- `supplier_invoices_pkey`: PRIMARY KEY (invoice_id)
- `supplier_invoices_supplier_id_invoice_number_key`: UNIQUE (supplier_id, invoice_number)

**الفهارس:**

- `supplier_invoices_supplier_id_invoice_number_key`: CREATE UNIQUE INDEX supplier_invoices_supplier_id_invoice_number_key ON purch.supplier_invoices USING btree (supplier_id, invoice_number)

</details>

### purch.supplier_payment_allocations

> توزيع السداد على فواتير محددة — مشغّل الحماية يمنع تخصيص أكثر من المتبقي

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `allocation_id` | `bigint` | لا |  |  |
| `payment_id` | `bigint` | لا |  |  |
| `invoice_id` | `bigint` | لا |  |  |
| `amount` | `numeric(19,4)` | لا |  |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `supplier_payment_allocations_amount_check`: CHECK (amount > 0::numeric)
- `supplier_payment_allocations_invoice_id_fkey`: FOREIGN KEY (invoice_id) REFERENCES purch.supplier_invoices(invoice_id)
- `supplier_payment_allocations_payment_id_fkey`: FOREIGN KEY (payment_id) REFERENCES purch.supplier_payments(payment_id) ON DELETE CASCADE
- `supplier_payment_allocations_pkey`: PRIMARY KEY (allocation_id)
- `supplier_payment_allocations_payment_id_invoice_id_key`: UNIQUE (payment_id, invoice_id)

**الفهارس:**

- `supplier_payment_allocations_payment_id_invoice_id_key`: CREATE UNIQUE INDEX supplier_payment_allocations_payment_id_invoice_id_key ON purch.supplier_payment_allocations USING btree (payment_id, invoice_id)

</details>

### purch.supplier_payments

> سداد المورد: أثر مالي واحد على الخزينة/البنك مهما وُزّع على فواتير — لا ازدواج سداد/مصروف

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `payment_id` | `bigint` | لا |  |  |
| `voucher_number` | `text` | لا |  |  |
| `supplier_id` | `bigint` | لا |  |  |
| `treasury_account_id` | `bigint` | لا |  |  |
| `amount` | `numeric(19,4)` | لا |  |  |
| `payment_method` | `text` | لا |  |  |
| `payment_date` | `date` | لا |  |  |
| `status` | `text` | لا | 'DRAFT'::text |  |
| `posted_at` | `timestamp with time zone` | نعم |  |  |
| `created_by` | `bigint` | لا |  |  |
| `created_at` | `timestamp with time zone` | لا | now() |  |
| `notes` | `text` | نعم |  |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `supplier_payments_amount_check`: CHECK (amount > 0::numeric)
- `supplier_payments_payment_method_check`: CHECK (payment_method = ANY (ARRAY['CASH'::text, 'BANK_TRANSFER'::text, 'CHEQUE'::text, 'CARD'::text]))
- `supplier_payments_status_check`: CHECK (status = ANY (ARRAY['DRAFT'::text, 'POSTED'::text, 'CANCELLED'::text]))
- `suppay_treasury_fk`: FOREIGN KEY (treasury_account_id) REFERENCES finance.treasury_accounts(treasury_account_id)
- `supplier_payments_created_by_fkey`: FOREIGN KEY (created_by) REFERENCES core.users(user_id)
- `supplier_payments_supplier_id_fkey`: FOREIGN KEY (supplier_id) REFERENCES purch.suppliers(supplier_id)
- `supplier_payments_pkey`: PRIMARY KEY (payment_id)
- `supplier_payments_voucher_number_key`: UNIQUE (voucher_number)

**الفهارس:**

- `supplier_payments_voucher_number_key`: CREATE UNIQUE INDEX supplier_payments_voucher_number_key ON purch.supplier_payments USING btree (voucher_number)

</details>

### purch.supplier_return_lines

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `line_id` | `bigint` | لا |  |  |
| `return_id` | `bigint` | لا |  |  |
| `sku_id` | `bigint` | لا |  |  |
| `qty` | `numeric(18,6)` | لا |  |  |
| `uom_id` | `bigint` | لا |  |  |
| `conversion_factor` | `numeric(18,6)` | لا |  |  |
| `unit_cost` | `numeric(19,4)` | لا |  |  |
| `line_total` | `numeric(19,4)` | لا | 0 |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `supplier_return_lines_qty_check`: CHECK (qty > 0::numeric)
- `supplier_return_lines_unit_cost_check`: CHECK (unit_cost >= 0::numeric)
- `supplier_return_lines_return_id_fkey`: FOREIGN KEY (return_id) REFERENCES purch.supplier_returns(return_id) ON DELETE CASCADE
- `supplier_return_lines_sku_id_fkey`: FOREIGN KEY (sku_id) REFERENCES catalog.skus(sku_id)
- `supplier_return_lines_uom_id_fkey`: FOREIGN KEY (uom_id) REFERENCES catalog.units(uom_id)
- `supplier_return_lines_pkey`: PRIMARY KEY (line_id)
- `supplier_return_lines_return_id_sku_id_key`: UNIQUE (return_id, sku_id)

**الفهارس:**

- `supplier_return_lines_return_id_sku_id_key`: CREATE UNIQUE INDEX supplier_return_lines_return_id_sku_id_key ON purch.supplier_return_lines USING btree (return_id, sku_id)

</details>

### purch.supplier_returns

> مرتجع المورد يرتبط بالاستلام/الفاتورة الأصلية — الترحيل ينشئ حركة SUPPLIER_RETURN ولا يخصم المورد مرتين

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `return_id` | `bigint` | لا |  |  |
| `return_number` | `text` | لا |  |  |
| `supplier_id` | `bigint` | لا |  |  |
| `receipt_id` | `bigint` | نعم |  |  |
| `invoice_id` | `bigint` | نعم |  |  |
| `warehouse_id` | `bigint` | لا |  |  |
| `status` | `text` | لا | 'DRAFT'::text |  |
| `refund_method` | `text` | نعم |  |  |
| `total` | `numeric(19,4)` | لا | 0 |  |
| `posted_at` | `timestamp with time zone` | نعم |  |  |
| `created_by` | `bigint` | لا |  |  |
| `created_at` | `timestamp with time zone` | لا | now() |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `sup_return_has_origin`: CHECK (receipt_id IS NOT NULL OR invoice_id IS NOT NULL)
- `supplier_returns_refund_method_check`: CHECK (refund_method = ANY (ARRAY['CASH'::text, 'CREDIT_NOTE'::text, 'BANK_TRANSFER'::text]))
- `supplier_returns_status_check`: CHECK (status = ANY (ARRAY['DRAFT'::text, 'POSTED'::text, 'CANCELLED'::text]))
- `supplier_returns_created_by_fkey`: FOREIGN KEY (created_by) REFERENCES core.users(user_id)
- `supplier_returns_invoice_id_fkey`: FOREIGN KEY (invoice_id) REFERENCES purch.supplier_invoices(invoice_id)
- `supplier_returns_receipt_id_fkey`: FOREIGN KEY (receipt_id) REFERENCES purch.purchase_receipts(receipt_id)
- `supplier_returns_supplier_id_fkey`: FOREIGN KEY (supplier_id) REFERENCES purch.suppliers(supplier_id)
- `supplier_returns_warehouse_id_fkey`: FOREIGN KEY (warehouse_id) REFERENCES inv.warehouses(warehouse_id)
- `supplier_returns_pkey`: PRIMARY KEY (return_id)
- `supplier_returns_return_number_key`: UNIQUE (return_number)

**الفهارس:**

- `supplier_returns_return_number_key`: CREATE UNIQUE INDEX supplier_returns_return_number_key ON purch.supplier_returns USING btree (return_number)

</details>

### purch.suppliers

> الموردون: الأرشفة تعطيل لا حذف — الطرف له مستندات تاريخية تبقى مرجعية (§13)

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `supplier_id` | `bigint` | لا |  |  |
| `code` | `text` | لا |  |  |
| `name_ar` | `text` | لا |  |  |
| `name_en` | `text` | نعم |  |  |
| `phone` | `text` | نعم |  |  |
| `email` | `text` | نعم |  |  |
| `tax_id` | `text` | نعم |  |  |
| `payment_terms_days` | `integer` | لا | 0 |  |
| `credit_limit` | `numeric(19,4)` | نعم |  |  |
| `account_status` | `text` | لا | 'ACTIVE'::text |  |
| `notes` | `text` | نعم |  |  |
| `created_at` | `timestamp with time zone` | لا | now() |  |
| `updated_at` | `timestamp with time zone` | لا | now() |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `suppliers_account_status_check`: CHECK (account_status = ANY (ARRAY['ACTIVE'::text, 'SUSPENDED'::text, 'ARCHIVED'::text]))
- `suppliers_payment_terms_days_check`: CHECK (payment_terms_days >= 0)
- `suppliers_pkey`: PRIMARY KEY (supplier_id)
- `suppliers_code_key`: UNIQUE (code)

**الفهارس:**

- `suppliers_code_key`: CREATE UNIQUE INDEX suppliers_code_key ON purch.suppliers USING btree (code)

</details>

## المخطط `sales` (9 جدولاً)

### sales.customers

> العملاء: رصيد الحساب مشتق من الفواتير الآجلة والتحصيلات (لا يُخزن رقماً حراً) — الآجل معطل افتراضياً بانتظار D-05

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `customer_id` | `bigint` | لا |  |  |
| `code` | `text` | لا |  |  |
| `name_ar` | `text` | لا |  |  |
| `phone` | `text` | نعم |  |  |
| `email` | `text` | نعم |  |  |
| `tax_id` | `text` | نعم |  |  |
| `credit_limit` | `numeric(19,4)` | لا | 0 |  |
| `credit_enabled` | `boolean` | لا | false |  |
| `payment_terms_days` | `integer` | لا | 0 |  |
| `account_status` | `text` | لا | 'ACTIVE'::text |  |
| `notes` | `text` | نعم |  |  |
| `created_at` | `timestamp with time zone` | لا | now() |  |
| `updated_at` | `timestamp with time zone` | لا | now() |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `cust_credit_needs_limit`: CHECK (NOT credit_enabled OR credit_limit > 0::numeric)
- `customers_account_status_check`: CHECK (account_status = ANY (ARRAY['ACTIVE'::text, 'SUSPENDED'::text, 'ARCHIVED'::text]))
- `customers_credit_limit_check`: CHECK (credit_limit >= 0::numeric)
- `customers_pkey`: PRIMARY KEY (customer_id)
- `customers_code_key`: UNIQUE (code)

**الفهارس:**

- `customers_code_key`: CREATE UNIQUE INDEX customers_code_key ON sales.customers USING btree (code)

</details>

### sales.drawer_events

> أحداث الدرج: أزمنة الطلب/الاستجابة والنتيجة — حالة UNKNOWN تُسجَّل بلا استجابة مفترضة (صدق الحالة §0.1 ق11)

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `event_id` | `bigint` | لا |  |  |
| `station_id` | `bigint` | لا |  |  |
| `shift_id` | `bigint` | نعم |  |  |
| `event_type` | `text` | لا |  |  |
| `reason` | `text` | نعم |  |  |
| `requested_at` | `timestamp with time zone` | لا |  |  |
| `responded_at` | `timestamp with time zone` | نعم |  |  |
| `result_status` | `text` | لا | 'OPENED'::text |  |
| `triggered_by` | `bigint` | لا |  |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `drawer_events_event_type_check`: CHECK (event_type = ANY (ARRAY['OPENED_BY_SALE'::text, 'MANUAL_OPEN'::text, 'FAILED'::text, 'UNKNOWN'::text]))
- `drawer_events_result_status_check`: CHECK (result_status = ANY (ARRAY['OPENED'::text, 'DENIED'::text, 'FAILED'::text, 'UNKNOWN'::text]))
- `drawer_events_shift_id_fkey`: FOREIGN KEY (shift_id) REFERENCES sales.shifts(shift_id)
- `drawer_events_station_id_fkey`: FOREIGN KEY (station_id) REFERENCES core.stations(station_id)
- `drawer_events_triggered_by_fkey`: FOREIGN KEY (triggered_by) REFERENCES core.users(user_id)
- `drawer_events_pkey`: PRIMARY KEY (event_id)

</details>

### sales.held_carts

> سلة معلقة بلقطة JSONB كاملة — الاستعادة لا تنشئ فاتورة مكررة ولا تفقد مسودة (اختبار G7)

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `cart_id` | `bigint` | لا |  |  |
| `station_id` | `bigint` | لا |  |  |
| `held_by` | `bigint` | لا |  |  |
| `customer_id` | `bigint` | نعم |  |  |
| `cart_snapshot` | `jsonb` | لا |  |  |
| `held_at` | `timestamp with time zone` | لا | now() |  |
| `restored_at` | `timestamp with time zone` | نعم |  |  |
| `restored_by` | `bigint` | نعم |  |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `held_carts_customer_id_fkey`: FOREIGN KEY (customer_id) REFERENCES sales.customers(customer_id)
- `held_carts_held_by_fkey`: FOREIGN KEY (held_by) REFERENCES core.users(user_id)
- `held_carts_restored_by_fkey`: FOREIGN KEY (restored_by) REFERENCES core.users(user_id)
- `held_carts_station_id_fkey`: FOREIGN KEY (station_id) REFERENCES core.stations(station_id)
- `held_carts_pkey`: PRIMARY KEY (cart_id)

**الفهارس:**

- `idx_held_carts_station`: CREATE INDEX idx_held_carts_station ON sales.held_carts USING btree (station_id) WHERE (restored_at IS NULL)

</details>

### sales.invoice_lines

> سطر الفاتورة يحمل لقطات كاملة (سعر/ضريبة/تحويل/تكلفة) — ربح الفواتير التاريخية لا يُحسب بتكلفة اليوم أبداً

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `line_id` | `bigint` | لا |  |  |
| `invoice_id` | `bigint` | لا |  |  |
| `line_no` | `integer` | لا |  |  |
| `sku_id` | `bigint` | لا |  |  |
| `sale_mode` | `text` | لا |  |  |
| `package_id` | `bigint` | نعم |  |  |
| `qty` | `numeric(18,6)` | لا |  |  |
| `uom_id` | `bigint` | لا |  |  |
| `conversion_factor` | `numeric(18,6)` | لا |  |  |
| `base_qty` | `numeric(18,6)` | نعم | (qty * conversion_factor) |  |
| `weight_kg` | `numeric(14,3)` | نعم |  |  |
| `weight_source` | `text` | نعم |  | SCALE_READING ميزان · QUICK_PICK وزن سريع · MANUAL_WEIGHT يدوي · AMOUNT_DERIVED مشتق من مبلغ (لا يعرض كقراءة ميزان) · WEIGHT_LABEL ملصق موزون · PACK_PREWEIGHED عبوة معدة |
| `amount_target` | `numeric(19,4)` | نعم |  |  |
| `tare_kg` | `numeric(14,3)` | نعم |  |  |
| `unit_price` | `numeric(19,4)` | لا |  |  |
| `price_list_id` | `bigint` | نعم |  |  |
| `tax_treatment` | `text` | لا | 'TAXABLE'::text |  |
| `tax_rate_snapshot` | `numeric(7,4)` | لا | 0 |  |
| `line_discount` | `numeric(19,4)` | لا | 0 |  |
| `invoice_discount_share` | `numeric(19,4)` | لا | 0 |  |
| `tax_amount` | `numeric(19,4)` | لا | 0 |  |
| `net_amount` | `numeric(19,4)` | لا |  |  |
| `cost_snapshot` | `numeric(19,4)` | نعم |  |  |
| `cost_method_snapshot` | `text` | نعم |  |  |
| `serial_id` | `bigint` | نعم |  |  |
| `notes` | `text` | نعم |  |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `invoice_lines_cost_method_snapshot_check`: CHECK (cost_method_snapshot = ANY (ARRAY['WAC'::text, 'FIFO'::text]))
- `invoice_lines_cost_snapshot_check`: CHECK (cost_snapshot IS NULL OR cost_snapshot >= 0::numeric)
- `invoice_lines_line_discount_check`: CHECK (line_discount >= 0::numeric)
- `invoice_lines_qty_check`: CHECK (qty > 0::numeric)
- `invoice_lines_sale_mode_check`: CHECK (sale_mode = ANY (ARRAY['COUNT'::text, 'WEIGHT'::text, 'PACK'::text, 'SERIALIZED'::text]))
- `invoice_lines_tax_rate_snapshot_check`: CHECK (tax_rate_snapshot >= 0::numeric)
- `invoice_lines_tax_treatment_check`: CHECK (tax_treatment = ANY (ARRAY['TAXABLE'::text, 'ZERO_RATED'::text, 'EXEMPT'::text]))
- `invoice_lines_unit_price_check`: CHECK (unit_price > 0::numeric)
- `invoice_lines_weight_source_check`: CHECK (weight_source = ANY (ARRAY['SCALE_READING'::text, 'QUICK_PICK'::text, 'MANUAL_WEIGHT'::text, 'AMOUNT_DERIVED'::text, 'WEIGHT_LABEL'::text, 'PACK_PREWEIGHED'::text]))
- `line_amount_target_source`: CHECK (amount_target IS NULL OR weight_source = 'AMOUNT_DERIVED'::text)
- `line_tare_nonneg`: CHECK (tare_kg IS NULL OR tare_kg >= 0::numeric)
- `line_weight_fields`: CHECK (sale_mode <> 'WEIGHT'::text OR weight_kg IS NOT NULL AND weight_source IS NOT NULL AND weight_kg > 0::numeric)
- `line_weight_only_for_weight`: CHECK (sale_mode = 'WEIGHT'::text OR weight_kg IS NULL AND weight_source IS NULL)
- `invoice_lines_invoice_id_fkey`: FOREIGN KEY (invoice_id) REFERENCES sales.invoices(invoice_id) ON DELETE CASCADE
- `invoice_lines_package_id_fkey`: FOREIGN KEY (package_id) REFERENCES catalog.packages(package_id)
- `invoice_lines_price_list_id_fkey`: FOREIGN KEY (price_list_id) REFERENCES catalog.price_lists(price_list_id)
- `invoice_lines_serial_id_fkey`: FOREIGN KEY (serial_id) REFERENCES inv.serials(serial_id)
- `invoice_lines_sku_id_fkey`: FOREIGN KEY (sku_id) REFERENCES catalog.skus(sku_id)
- `invoice_lines_uom_id_fkey`: FOREIGN KEY (uom_id) REFERENCES catalog.units(uom_id)
- `invoice_lines_pkey`: PRIMARY KEY (line_id)
- `invoice_lines_invoice_id_line_no_key`: UNIQUE (invoice_id, line_no)

**الفهارس:**

- `idx_invoice_lines_invoice`: CREATE INDEX idx_invoice_lines_invoice ON sales.invoice_lines USING btree (invoice_id)
- `idx_invoice_lines_sku`: CREATE INDEX idx_invoice_lines_sku ON sales.invoice_lines USING btree (sku_id)
- `invoice_lines_invoice_id_line_no_key`: CREATE UNIQUE INDEX invoice_lines_invoice_id_line_no_key ON sales.invoice_lines USING btree (invoice_id, line_no)

</details>

### sales.invoices

> فاتورة البيع: الحالة DRAFT قابلة للتحرير، وPOSTED مجمّدة بالكامل — الإلغاء VOID والمرتجع مستندان لا حذف (§0.1 ق6)

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `invoice_id` | `bigint` | لا |  |  |
| `invoice_number` | `text` | لا |  |  |
| `branch_id` | `bigint` | لا |  |  |
| `station_id` | `bigint` | لا |  |  |
| `shift_id` | `bigint` | نعم |  |  |
| `cashier_user_id` | `bigint` | لا |  |  |
| `customer_id` | `bigint` | نعم |  |  |
| `status` | `text` | لا | 'DRAFT'::text |  |
| `currency` | `text` | لا | 'EGP'::text |  |
| `tax_mode_snapshot` | `text` | لا | 'VAT_INCLUSIVE'::text | لقطة سياسة الضريبة وقت الترحيل (D-01) — تغيير السياسة لاحقاً لا يعيد حساب التاريخ |
| `gross_total` | `numeric(19,4)` | لا | 0 |  |
| `line_discount_total` | `numeric(19,4)` | لا | 0 |  |
| `invoice_discount` | `numeric(19,4)` | لا | 0 |  |
| `tax_total` | `numeric(19,4)` | لا | 0 |  |
| `net_total` | `numeric(19,4)` | لا | 0 |  |
| `paid_total` | `numeric(19,4)` | لا | 0 |  |
| `returned_total` | `numeric(19,4)` | لا | 0 |  |
| `idempotency_key` | `text` | نعم |  | مفتاح Idempotency لعملية إنشاء الفاتورة — إعادة الإرسال بعد timeout لا تنشئ فاتورة ثانية |
| `created_at` | `timestamp with time zone` | لا | now() |  |
| `posted_at` | `timestamp with time zone` | نعم |  |  |
| `voided_at` | `timestamp with time zone` | نعم |  |  |
| `void_reason` | `text` | نعم |  |  |
| `updated_at` | `timestamp with time zone` | لا | now() |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `invoices_status_check`: CHECK (status = ANY (ARRAY['DRAFT'::text, 'POSTED'::text, 'VOID'::text, 'PARTIAL_RETURN'::text, 'FULL_RETURN'::text]))
- `invoices_tax_mode_snapshot_check`: CHECK (tax_mode_snapshot = ANY (ARRAY['VAT_INCLUSIVE'::text, 'VAT_EXCLUSIVE'::text, 'ZERO_TAX'::text]))
- `invoices_branch_id_fkey`: FOREIGN KEY (branch_id) REFERENCES core.branches(branch_id)
- `invoices_cashier_user_id_fkey`: FOREIGN KEY (cashier_user_id) REFERENCES core.users(user_id)
- `invoices_customer_id_fkey`: FOREIGN KEY (customer_id) REFERENCES sales.customers(customer_id)
- `invoices_shift_fk`: FOREIGN KEY (shift_id) REFERENCES sales.shifts(shift_id)
- `invoices_station_id_fkey`: FOREIGN KEY (station_id) REFERENCES core.stations(station_id)
- `invoices_pkey`: PRIMARY KEY (invoice_id)
- `invoices_branch_id_invoice_number_key`: UNIQUE (branch_id, invoice_number)

**الفهارس:**

- `idx_invoices_branch_time`: CREATE INDEX idx_invoices_branch_time ON sales.invoices USING btree (branch_id, created_at DESC)
- `idx_invoices_customer`: CREATE INDEX idx_invoices_customer ON sales.invoices USING btree (customer_id) WHERE (customer_id IS NOT NULL)
- `idx_invoices_posted_reporting`: CREATE INDEX idx_invoices_posted_reporting ON sales.invoices USING btree (branch_id, posted_at DESC) WHERE (status = ANY (ARRAY['POSTED'::text, 'PARTIAL_RETURN'::text, 'FULL_RETURN'::text]))
- `idx_invoices_shift`: CREATE INDEX idx_invoices_shift ON sales.invoices USING btree (shift_id)
- `idx_invoices_status`: CREATE INDEX idx_invoices_status ON sales.invoices USING btree (status)
- `invoices_branch_id_invoice_number_key`: CREATE UNIQUE INDEX invoices_branch_id_invoice_number_key ON sales.invoices USING btree (branch_id, invoice_number)
- `uq_invoices_idem`: CREATE UNIQUE INDEX uq_invoices_idem ON sales.invoices USING btree (idempotency_key) WHERE (idempotency_key IS NOT NULL)

</details>

### sales.payments

> المدفوعات: تصريح البطاقة AUTHORIZED ليس تحصيلاً نهائياً — التحصيل CAPTURED/SETTLED حالة مستقلة تفصل في التقارير (§16 ق2)

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `payment_id` | `bigint` | لا |  |  |
| `invoice_id` | `bigint` | لا |  |  |
| `payment_method` | `text` | لا |  |  |
| `amount` | `numeric(19,4)` | لا |  |  |
| `status` | `text` | لا | 'CAPTURED'::text |  |
| `provider_name` | `text` | نعم |  |  |
| `provider_reference` | `text` | نعم |  | مرجع المزود فقط — تخزين PIN/CVV/PAN الكامل ممنوع منعاً باتاً (T-PAY-003) |
| `treasury_account_id` | `bigint` | نعم |  |  |
| `received_at` | `timestamp with time zone` | لا | now() |  |
| `settled_at` | `timestamp with time zone` | نعم |  |  |
| `created_by` | `bigint` | لا |  |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `pay_credit_no_provider`: CHECK (payment_method <> 'CREDIT'::text OR provider_name IS NULL AND provider_reference IS NULL)
- `payments_amount_check`: CHECK (amount > 0::numeric)
- `payments_payment_method_check`: CHECK (payment_method = ANY (ARRAY['CASH'::text, 'CARD'::text, 'SPLIT_CASH'::text, 'SPLIT_CARD'::text, 'CREDIT'::text, 'OTHER'::text]))
- `payments_status_check`: CHECK (status = ANY (ARRAY['AUTHORIZED'::text, 'CAPTURED'::text, 'SETTLED'::text, 'FAILED'::text, 'REFUNDED'::text, 'CANCELLED'::text]))
- `payments_created_by_fkey`: FOREIGN KEY (created_by) REFERENCES core.users(user_id)
- `payments_invoice_id_fkey`: FOREIGN KEY (invoice_id) REFERENCES sales.invoices(invoice_id)
- `payments_treasury_fk`: FOREIGN KEY (treasury_account_id) REFERENCES finance.treasury_accounts(treasury_account_id)
- `payments_pkey`: PRIMARY KEY (payment_id)

**الفهارس:**

- `idx_payments_invoice`: CREATE INDEX idx_payments_invoice ON sales.payments USING btree (invoice_id)
- `idx_payments_method_status`: CREATE INDEX idx_payments_method_status ON sales.payments USING btree (payment_method, status)

</details>

### sales.refund_lines

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `line_id` | `bigint` | لا |  |  |
| `refund_id` | `bigint` | لا |  |  |
| `original_line_id` | `bigint` | لا |  |  |
| `qty` | `numeric(18,6)` | لا |  |  |
| `unit_refund` | `numeric(19,4)` | لا |  |  |
| `line_total` | `numeric(19,4)` | لا |  |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `refund_lines_line_total_check`: CHECK (line_total >= 0::numeric)
- `refund_lines_qty_check`: CHECK (qty > 0::numeric)
- `refund_lines_unit_refund_check`: CHECK (unit_refund >= 0::numeric)
- `refund_lines_original_line_id_fkey`: FOREIGN KEY (original_line_id) REFERENCES sales.invoice_lines(line_id)
- `refund_lines_refund_id_fkey`: FOREIGN KEY (refund_id) REFERENCES sales.refunds(refund_id) ON DELETE CASCADE
- `refund_lines_pkey`: PRIMARY KEY (line_id)
- `refund_lines_refund_id_original_line_id_key`: UNIQUE (refund_id, original_line_id)

**الفهارس:**

- `refund_lines_refund_id_original_line_id_key`: CREATE UNIQUE INDEX refund_lines_refund_id_original_line_id_key ON sales.refund_lines USING btree (refund_id, original_line_id)

</details>

### sales.refunds

> المرتجع: اعتماد مشرف + سبب + رد بوسيلة الدفع الأصلية (البطاقة للبطاقة — D-04)؛ الترحيل يعكس المخزون والمال مرة واحدة مرتبطة بالأصل

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `refund_id` | `bigint` | لا |  |  |
| `refund_number` | `text` | لا |  |  |
| `original_invoice_id` | `bigint` | لا |  |  |
| `branch_id` | `bigint` | لا |  |  |
| `approval_id` | `bigint` | نعم |  |  |
| `status` | `text` | لا | 'DRAFT'::text |  |
| `refund_method` | `text` | لا |  |  |
| `restock` | `boolean` | لا | true |  |
| `total` | `numeric(19,4)` | لا | 0 |  |
| `reason` | `text` | لا |  |  |
| `posted_at` | `timestamp with time zone` | نعم |  |  |
| `created_by` | `bigint` | لا |  |  |
| `created_at` | `timestamp with time zone` | لا | now() |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `refund_approval_required`: CHECK (approval_id IS NOT NULL)
- `refunds_reason_check`: CHECK (length(TRIM(BOTH FROM reason)) >= 3)
- `refunds_refund_method_check`: CHECK (refund_method = ANY (ARRAY['CASH'::text, 'CARD'::text, 'CREDIT_REVERSAL'::text]))
- `refunds_status_check`: CHECK (status = ANY (ARRAY['DRAFT'::text, 'POSTED'::text, 'CANCELLED'::text]))
- `refunds_total_check`: CHECK (total >= 0::numeric)
- `refunds_approval_id_fkey`: FOREIGN KEY (approval_id) REFERENCES core.approvals(approval_id)
- `refunds_branch_id_fkey`: FOREIGN KEY (branch_id) REFERENCES core.branches(branch_id)
- `refunds_created_by_fkey`: FOREIGN KEY (created_by) REFERENCES core.users(user_id)
- `refunds_original_invoice_id_fkey`: FOREIGN KEY (original_invoice_id) REFERENCES sales.invoices(invoice_id)
- `refunds_pkey`: PRIMARY KEY (refund_id)
- `refunds_refund_number_key`: UNIQUE (refund_number)

**الفهارس:**

- `refunds_refund_number_key`: CREATE UNIQUE INDEX refunds_refund_number_key ON sales.refunds USING btree (refund_number)

</details>

### sales.shifts

> الوردية: وردية واحدة مفتوحة لكل محطة (فهرس فريد جزئي) — الفرق محسوب عمودياً والإغلاق نهائي لا يعدل بلا إجراء موثق

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `shift_id` | `bigint` | لا |  |  |
| `station_id` | `bigint` | لا |  |  |
| `opened_by` | `bigint` | لا |  |  |
| `closed_by` | `bigint` | نعم |  |  |
| `status` | `text` | لا | 'OPEN'::text |  |
| `opening_float` | `numeric(19,4)` | لا | 0 |  |
| `expected_cash` | `numeric(19,4)` | نعم |  |  |
| `counted_cash` | `numeric(19,4)` | نعم |  |  |
| `variance` | `numeric(19,4)` | نعم | (COALESCE(counted_cash, (0)::numeric) - COALESCE(expected_cash, (0)::numeric)) |  |
| `variance_reason` | `text` | نعم |  |  |
| `opened_at` | `timestamp with time zone` | لا | now() |  |
| `closed_at` | `timestamp with time zone` | نعم |  |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `shifts_opening_float_check`: CHECK (opening_float >= 0::numeric)
- `shifts_status_check`: CHECK (status = ANY (ARRAY['OPEN'::text, 'CLOSED'::text]))
- `shifts_closed_by_fkey`: FOREIGN KEY (closed_by) REFERENCES core.users(user_id)
- `shifts_opened_by_fkey`: FOREIGN KEY (opened_by) REFERENCES core.users(user_id)
- `shifts_station_id_fkey`: FOREIGN KEY (station_id) REFERENCES core.stations(station_id)
- `shifts_pkey`: PRIMARY KEY (shift_id)

**الفهارس:**

- `uq_shifts_open_per_station`: CREATE UNIQUE INDEX uq_shifts_open_per_station ON sales.shifts USING btree (station_id) WHERE (status = 'OPEN'::text)

</details>

## المخطط `finance` (12 جدولاً)

### finance.capital_events

> رأس المال بحدث موثق (مساهمة/سحب/قرض/رصيد افتتاحي معتمد) — لا يُساوى بالمبيعات أو الصندوق أو قيمة المخزون (§14.3، D-14)

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `capital_id` | `bigint` | لا |  |  |
| `event_number` | `text` | لا |  |  |
| `event_type` | `text` | لا |  |  |
| `amount` | `numeric(19,4)` | لا |  |  |
| `treasury_account_id` | `bigint` | نعم |  |  |
| `event_date` | `date` | لا |  |  |
| `description` | `text` | لا |  |  |
| `status` | `text` | لا | 'DRAFT'::text |  |
| `approval_id` | `bigint` | نعم |  |  |
| `posted_at` | `timestamp with time zone` | نعم |  |  |
| `created_by` | `bigint` | لا |  |  |
| `created_at` | `timestamp with time zone` | لا | now() |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `capital_events_amount_check`: CHECK (amount > 0::numeric)
- `capital_events_event_type_check`: CHECK (event_type = ANY (ARRAY['OWNER_CONTRIBUTION'::text, 'OWNER_WITHDRAWAL'::text, 'LOAN_RECEIVED'::text, 'LOAN_REPAYMENT'::text, 'OPENING_BALANCE'::text]))
- `capital_events_status_check`: CHECK (status = ANY (ARRAY['DRAFT'::text, 'POSTED'::text, 'CANCELLED'::text]))
- `capital_opening_needs_approval`: CHECK (event_type <> 'OPENING_BALANCE'::text OR approval_id IS NOT NULL)
- `capital_events_approval_id_fkey`: FOREIGN KEY (approval_id) REFERENCES core.approvals(approval_id)
- `capital_events_created_by_fkey`: FOREIGN KEY (created_by) REFERENCES core.users(user_id)
- `capital_events_treasury_account_id_fkey`: FOREIGN KEY (treasury_account_id) REFERENCES finance.treasury_accounts(treasury_account_id)
- `capital_events_pkey`: PRIMARY KEY (capital_id)
- `capital_events_event_number_key`: UNIQUE (event_number)

**الفهارس:**

- `capital_events_event_number_key`: CREATE UNIQUE INDEX capital_events_event_number_key ON finance.capital_events USING btree (event_number)

</details>

### finance.cost_layers

> حالة التكلفة الحالية (WAC افتراضياً بانتظار D-02): متوسط متحرك يُحدَّث مع كل استلام — الفاتورة تحفظ لقطة التكلفة وقت ترحيلها لا تكلفة اليوم

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `layer_id` | `bigint` | لا |  |  |
| `sku_id` | `bigint` | لا |  |  |
| `warehouse_id` | `bigint` | لا |  |  |
| `costing_method` | `text` | لا | 'WAC'::text |  |
| `avg_unit_cost` | `numeric(19,4)` | لا |  |  |
| `qty_on_layer` | `numeric(18,6)` | لا | 0 |  |
| `last_receipt_at` | `timestamp with time zone` | نعم |  |  |
| `updated_at` | `timestamp with time zone` | لا | now() |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `cost_layers_avg_unit_cost_check`: CHECK (avg_unit_cost >= 0::numeric)
- `cost_layers_costing_method_check`: CHECK (costing_method = ANY (ARRAY['WAC'::text, 'FIFO'::text]))
- `cost_layers_sku_id_fkey`: FOREIGN KEY (sku_id) REFERENCES catalog.skus(sku_id)
- `cost_layers_warehouse_id_fkey`: FOREIGN KEY (warehouse_id) REFERENCES inv.warehouses(warehouse_id)
- `cost_layers_pkey`: PRIMARY KEY (layer_id)
- `cost_layers_sku_id_warehouse_id_key`: UNIQUE (sku_id, warehouse_id)

**الفهارس:**

- `cost_layers_sku_id_warehouse_id_key`: CREATE UNIQUE INDEX cost_layers_sku_id_warehouse_id_key ON finance.cost_layers USING btree (sku_id, warehouse_id)

</details>

### finance.expense_categories

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `category_id` | `bigint` | لا |  |  |
| `code` | `text` | لا |  |  |
| `name_ar` | `text` | لا |  |  |
| `parent_id` | `bigint` | نعم |  |  |
| `is_active` | `boolean` | لا | true |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `expense_categories_parent_id_fkey`: FOREIGN KEY (parent_id) REFERENCES finance.expense_categories(category_id)
- `expense_categories_pkey`: PRIMARY KEY (category_id)
- `expense_categories_code_key`: UNIQUE (code)

**الفهارس:**

- `expense_categories_code_key`: CREATE UNIQUE INDEX expense_categories_code_key ON finance.expense_categories USING btree (code)

</details>

### finance.expenses

> المصروف: اعتماد ثم سداد — أثر مالي واحد على الخزينة/البنك والإلغاء بعكس موثق لا حذف (§14.4)

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `expense_id` | `bigint` | لا |  |  |
| `expense_number` | `text` | لا |  |  |
| `category_id` | `bigint` | لا |  |  |
| `branch_id` | `bigint` | لا |  |  |
| `expense_date` | `date` | لا |  |  |
| `amount` | `numeric(19,4)` | لا |  |  |
| `tax_amount` | `numeric(19,4)` | لا | 0 |  |
| `payment_method` | `text` | لا |  |  |
| `treasury_account_id` | `bigint` | نعم |  |  |
| `beneficiary` | `text` | نعم |  |  |
| `cost_center` | `text` | نعم |  |  |
| `status` | `text` | لا | 'DRAFT'::text |  |
| `approval_id` | `bigint` | نعم |  |  |
| `attachment_media_id` | `bigint` | نعم |  |  |
| `notes` | `text` | نعم |  |  |
| `posted_at` | `timestamp with time zone` | نعم |  |  |
| `created_by` | `bigint` | لا |  |  |
| `created_at` | `timestamp with time zone` | لا | now() |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `expenses_amount_check`: CHECK (amount > 0::numeric)
- `expenses_payment_method_check`: CHECK (payment_method = ANY (ARRAY['CASH'::text, 'BANK_TRANSFER'::text, 'CHEQUE'::text, 'CARD'::text]))
- `expenses_status_check`: CHECK (status = ANY (ARRAY['DRAFT'::text, 'APPROVED'::text, 'POSTED'::text, 'CANCELLED'::text]))
- `expenses_tax_amount_check`: CHECK (tax_amount >= 0::numeric)
- `expenses_approval_id_fkey`: FOREIGN KEY (approval_id) REFERENCES core.approvals(approval_id)
- `expenses_attachment_media_id_fkey`: FOREIGN KEY (attachment_media_id) REFERENCES catalog.media_assets(media_id)
- `expenses_branch_id_fkey`: FOREIGN KEY (branch_id) REFERENCES core.branches(branch_id)
- `expenses_category_id_fkey`: FOREIGN KEY (category_id) REFERENCES finance.expense_categories(category_id)
- `expenses_created_by_fkey`: FOREIGN KEY (created_by) REFERENCES core.users(user_id)
- `expenses_treasury_account_id_fkey`: FOREIGN KEY (treasury_account_id) REFERENCES finance.treasury_accounts(treasury_account_id)
- `expenses_pkey`: PRIMARY KEY (expense_id)
- `expenses_expense_number_key`: UNIQUE (expense_number)

**الفهارس:**

- `expenses_expense_number_key`: CREATE UNIQUE INDEX expenses_expense_number_key ON finance.expenses USING btree (expense_number)

</details>

### finance.fiscal_periods

> الفترة المالية: الترحيل في فترة CLOSED ممنوع — الاستثناء الإجرائي موثق بموافقة (§15.2)

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `period_id` | `bigint` | لا |  |  |
| `name` | `text` | لا |  |  |
| `period_start` | `date` | لا |  |  |
| `period_end` | `date` | لا |  |  |
| `status` | `text` | لا | 'OPEN'::text |  |
| `closed_at` | `timestamp with time zone` | نعم |  |  |
| `closed_by` | `bigint` | نعم |  |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `fiscal_periods_status_check`: CHECK (status = ANY (ARRAY['OPEN'::text, 'CLOSED'::text]))
- `period_range`: CHECK (period_end >= period_start)
- `fiscal_periods_closed_by_fkey`: FOREIGN KEY (closed_by) REFERENCES core.users(user_id)
- `fiscal_periods_pkey`: PRIMARY KEY (period_id)

**الفهارس:**

- `uq_fiscal_periods_range`: CREATE UNIQUE INDEX uq_fiscal_periods_range ON finance.fiscal_periods USING btree (period_start, period_end)

</details>

### finance.gl_accounts

> دليل الحسابات: يُشغَّل بالكامل عند اعتماد D-12 (أ) — البنية جاهزة منذ G1 والتقارير تعلن نطاقها

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `gl_account_id` | `bigint` | لا |  |  |
| `code` | `text` | لا |  |  |
| `name_ar` | `text` | لا |  |  |
| `account_type` | `text` | لا |  |  |
| `parent_id` | `bigint` | نعم |  |  |
| `is_postable` | `boolean` | لا | true |  |
| `is_active` | `boolean` | لا | true |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `gl_accounts_account_type_check`: CHECK (account_type = ANY (ARRAY['ASSET'::text, 'LIABILITY'::text, 'EQUITY'::text, 'REVENUE'::text, 'EXPENSE'::text]))
- `gl_not_self_parent`: CHECK (parent_id IS NULL OR parent_id <> gl_account_id)
- `gl_accounts_parent_id_fkey`: FOREIGN KEY (parent_id) REFERENCES finance.gl_accounts(gl_account_id)
- `gl_accounts_pkey`: PRIMARY KEY (gl_account_id)
- `gl_accounts_code_key`: UNIQUE (code)

**الفهارس:**

- `gl_accounts_code_key`: CREATE UNIQUE INDEX gl_accounts_code_key ON finance.gl_accounts USING btree (code)

</details>

### finance.journal_entries

> القيد يرتبط بمصدره التجاري دائماً — القيد المرحل لا يعدل بل يُعكس بقيد مرتبط (§15.2)

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `entry_id` | `bigint` | لا |  |  |
| `entry_number` | `text` | لا |  |  |
| `period_id` | `bigint` | لا |  |  |
| `source_doc_type` | `text` | لا |  |  |
| `source_doc_id` | `bigint` | لا |  |  |
| `description` | `text` | نعم |  |  |
| `status` | `text` | لا | 'DRAFT'::text |  |
| `posted_at` | `timestamp with time zone` | نعم |  |  |
| `reversed_by_entry_id` | `bigint` | نعم |  |  |
| `created_by` | `bigint` | لا |  |  |
| `created_at` | `timestamp with time zone` | لا | now() |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `journal_entries_status_check`: CHECK (status = ANY (ARRAY['DRAFT'::text, 'POSTED'::text, 'REVERSED'::text]))
- `journal_entries_created_by_fkey`: FOREIGN KEY (created_by) REFERENCES core.users(user_id)
- `journal_entries_period_id_fkey`: FOREIGN KEY (period_id) REFERENCES finance.fiscal_periods(period_id)
- `journal_entries_reversed_by_entry_id_fkey`: FOREIGN KEY (reversed_by_entry_id) REFERENCES finance.journal_entries(entry_id)
- `journal_entries_pkey`: PRIMARY KEY (entry_id)
- `journal_entries_entry_number_key`: UNIQUE (entry_number)

**الفهارس:**

- `idx_journal_source`: CREATE INDEX idx_journal_source ON finance.journal_entries USING btree (source_doc_type, source_doc_id)
- `journal_entries_entry_number_key`: CREATE UNIQUE INDEX journal_entries_entry_number_key ON finance.journal_entries USING btree (entry_number)

</details>

### finance.journal_lines

> سطر القيد: مدين أو دائن — لا الاثنان معاً ولا صفر

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `line_id` | `bigint` | لا |  |  |
| `entry_id` | `bigint` | لا |  |  |
| `gl_account_id` | `bigint` | لا |  |  |
| `debit` | `numeric(19,4)` | لا | 0 |  |
| `credit` | `numeric(19,4)` | لا | 0 |  |
| `line_no` | `integer` | لا |  |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `jl_debit_xor_credit`: CHECK (debit > 0::numeric AND credit = 0::numeric OR credit > 0::numeric AND debit = 0::numeric)
- `journal_lines_credit_check`: CHECK (credit >= 0::numeric)
- `journal_lines_debit_check`: CHECK (debit >= 0::numeric)
- `journal_lines_entry_id_fkey`: FOREIGN KEY (entry_id) REFERENCES finance.journal_entries(entry_id) ON DELETE CASCADE
- `journal_lines_gl_account_id_fkey`: FOREIGN KEY (gl_account_id) REFERENCES finance.gl_accounts(gl_account_id)
- `journal_lines_pkey`: PRIMARY KEY (line_id)

</details>

### finance.provider_settlements

> تسوية مزود الدفع: مطابقة ما صُرّح به مع ما وصل البنك فعلاً بعد العمولات — التقارير تفصل التحصيل عن التسوية (§16 ق2)

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `settlement_id` | `bigint` | لا |  |  |
| `provider_name` | `text` | لا |  |  |
| `settlement_date` | `date` | لا |  |  |
| `gross_amount` | `numeric(19,4)` | لا |  |  |
| `fees` | `numeric(19,4)` | لا | 0 |  |
| `net_amount` | `numeric(19,4)` | لا |  |  |
| `bank_treasury_account_id` | `bigint` | نعم |  |  |
| `reference_no` | `text` | نعم |  |  |
| `reconciled` | `boolean` | لا | false |  |
| `reconciled_at` | `timestamp with time zone` | نعم |  |  |
| `created_at` | `timestamp with time zone` | لا | now() |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `provider_settlements_fees_check`: CHECK (fees >= 0::numeric)
- `provider_settlements_gross_amount_check`: CHECK (gross_amount >= 0::numeric)
- `settlement_math`: CHECK (abs(net_amount + fees - gross_amount) < 0.005)
- `provider_settlements_bank_treasury_account_id_fkey`: FOREIGN KEY (bank_treasury_account_id) REFERENCES finance.treasury_accounts(treasury_account_id)
- `provider_settlements_pkey`: PRIMARY KEY (settlement_id)

</details>

### finance.treasury_accounts

> درج الكاشير والخزينة والبنك ومزود الدفع أرصدة منفصلة — ليست رصيداً واحداً (§14.2)

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `treasury_account_id` | `bigint` | لا |  |  |
| `code` | `text` | لا |  |  |
| `name_ar` | `text` | لا |  |  |
| `account_kind` | `text` | لا |  |  |
| `branch_id` | `bigint` | نعم |  |  |
| `currency` | `text` | لا | 'EGP'::text |  |
| `is_active` | `boolean` | لا | true |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `treasury_accounts_account_kind_check`: CHECK (account_kind = ANY (ARRAY['CASH_DRAWER'::text, 'SAFE'::text, 'BANK'::text, 'PAYMENT_PROVIDER'::text]))
- `treasury_accounts_branch_id_fkey`: FOREIGN KEY (branch_id) REFERENCES core.branches(branch_id)
- `treasury_accounts_pkey`: PRIMARY KEY (treasury_account_id)
- `treasury_accounts_code_key`: UNIQUE (code)

**الفهارس:**

- `treasury_accounts_code_key`: CREATE UNIQUE INDEX treasury_accounts_code_key ON finance.treasury_accounts USING btree (code)

</details>

### finance.treasury_transactions

> دفتر الخزينة: كل حدث مالي ينشئ أثراً واحداً هنا — المرتجع لا يخصم النقدية مرتين (§15.4)

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `txn_id` | `bigint` | لا |  |  |
| `treasury_account_id` | `bigint` | لا |  |  |
| `direction` | `smallint` | لا |  |  |
| `amount` | `numeric(19,4)` | لا |  |  |
| `source_doc_type` | `text` | لا |  |  |
| `source_doc_id` | `bigint` | لا |  |  |
| `reason` | `text` | نعم |  |  |
| `occurred_at` | `timestamp with time zone` | لا | now() |  |
| `created_by` | `bigint` | لا |  |  |
| `branch_id` | `bigint` | لا |  |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `treasury_transactions_amount_check`: CHECK (amount > 0::numeric)
- `treasury_transactions_direction_check`: CHECK (direction = ANY (ARRAY[1, '-1'::integer]))
- `treasury_transactions_branch_id_fkey`: FOREIGN KEY (branch_id) REFERENCES core.branches(branch_id)
- `treasury_transactions_created_by_fkey`: FOREIGN KEY (created_by) REFERENCES core.users(user_id)
- `treasury_transactions_treasury_account_id_fkey`: FOREIGN KEY (treasury_account_id) REFERENCES finance.treasury_accounts(treasury_account_id)
- `treasury_transactions_pkey`: PRIMARY KEY (txn_id)

**الفهارس:**

- `idx_treasury_txn_account_time`: CREATE INDEX idx_treasury_txn_account_time ON finance.treasury_transactions USING btree (treasury_account_id, occurred_at DESC)
- `idx_treasury_txn_source`: CREATE INDEX idx_treasury_txn_source ON finance.treasury_transactions USING btree (source_doc_type, source_doc_id)

</details>

### finance.vouchers

> سندات القبض والصرف والتحويل: التحويل الداخلي بين خزينتين لا يولد إيراداً ولا مصروفاً (§14.2) — الترحيل ينشئ حركتي خزينة متقابلتين

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `voucher_id` | `bigint` | لا |  |  |
| `voucher_number` | `text` | لا |  |  |
| `voucher_type` | `text` | لا |  |  |
| `treasury_account_id` | `bigint` | لا |  |  |
| `counter_treasury_account_id` | `bigint` | نعم |  |  |
| `amount` | `numeric(19,4)` | لا |  |  |
| `counterparty_type` | `text` | نعم |  |  |
| `counterparty_id` | `bigint` | نعم |  |  |
| `reason` | `text` | لا |  |  |
| `status` | `text` | لا | 'DRAFT'::text |  |
| `posted_at` | `timestamp with time zone` | نعم |  |  |
| `created_by` | `bigint` | لا |  |  |
| `created_at` | `timestamp with time zone` | لا | now() |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `voucher_transfer_accounts`: CHECK (voucher_type <> 'TRANSFER'::text OR counter_treasury_account_id IS NOT NULL AND counter_treasury_account_id <> treasury_account_id)
- `voucher_transfer_no_counterparty`: CHECK (voucher_type <> 'TRANSFER'::text OR counterparty_type IS NULL)
- `vouchers_amount_check`: CHECK (amount > 0::numeric)
- `vouchers_counterparty_type_check`: CHECK (counterparty_type = ANY (ARRAY['CUSTOMER'::text, 'SUPPLIER'::text, 'EMPLOYEE'::text, 'OWNER'::text, 'OTHER'::text]))
- `vouchers_reason_check`: CHECK (length(TRIM(BOTH FROM reason)) >= 3)
- `vouchers_status_check`: CHECK (status = ANY (ARRAY['DRAFT'::text, 'POSTED'::text, 'CANCELLED'::text]))
- `vouchers_voucher_type_check`: CHECK (voucher_type = ANY (ARRAY['RECEIPT'::text, 'DISBURSEMENT'::text, 'TRANSFER'::text]))
- `vouchers_counter_treasury_account_id_fkey`: FOREIGN KEY (counter_treasury_account_id) REFERENCES finance.treasury_accounts(treasury_account_id)
- `vouchers_created_by_fkey`: FOREIGN KEY (created_by) REFERENCES core.users(user_id)
- `vouchers_treasury_account_id_fkey`: FOREIGN KEY (treasury_account_id) REFERENCES finance.treasury_accounts(treasury_account_id)
- `vouchers_pkey`: PRIMARY KEY (voucher_id)
- `vouchers_voucher_number_key`: UNIQUE (voucher_number)

**الفهارس:**

- `vouchers_voucher_number_key`: CREATE UNIQUE INDEX vouchers_voucher_number_key ON finance.vouchers USING btree (voucher_number)

</details>

## المخطط `print` (6 جدولاً)

### print.auto_print_rules

> سياسة طباعة مستقلة لكل نوع مستند — نسخة الإدارة إلكترونية والطباعة بإعداد إداري، وA4 التفصيلي بإجراء صريح فقط (§29.10/§29.11)

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `rule_id` | `bigint` | لا |  |  |
| `document_type` | `text` | لا |  |  |
| `policy` | `text` | لا |  |  |
| `printer_role` | `text` | نعم |  |  |
| `notes` | `text` | نعم |  |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `a4_never_auto`: CHECK (document_type <> 'A4_DETAILED_REPORT'::text OR policy = 'EXPLICIT_A4_DETAILED'::text)
- `auto_print_rules_policy_check`: CHECK (policy = ANY (ARRAY['AUTO_AFTER_POST'::text, 'ELECTRONIC_ONLY'::text, 'EXPLICIT_USER'::text, 'EXPLICIT_A4_DETAILED'::text]))
- `auto_print_rules_pkey`: PRIMARY KEY (rule_id)
- `auto_print_rules_document_type_key`: UNIQUE (document_type)

**الفهارس:**

- `auto_print_rules_document_type_key`: CREATE UNIQUE INDEX auto_print_rules_document_type_key ON print.auto_print_rules USING btree (document_type)

</details>

### print.device_registry

> سجل الأجهزة: افتراض كل جهاز NOT_CONFIGURED حتى فحص فعلي موثق — فصل حالة التنفيذ عن حالة الاتصال (§0.1 ق11)

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `device_id` | `bigint` | لا |  |  |
| `station_id` | `bigint` | نعم |  |  |
| `device_type` | `text` | لا |  |  |
| `device_model` | `text` | نعم |  |  |
| `connection_mode` | `text` | نعم |  |  |
| `status` | `text` | لا | 'NOT_CONFIGURED'::text |  |
| `last_checked_at` | `timestamp with time zone` | نعم |  |  |
| `last_check_result` | `text` | نعم |  |  |
| `notes` | `text` | نعم |  |  |
| `updated_at` | `timestamp with time zone` | لا | now() |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `device_registry_device_type_check`: CHECK (device_type = ANY (ARRAY['BARCODE_SCANNER'::text, 'SCALE'::text, 'RECEIPT_PRINTER'::text, 'LABEL_PRINTER'::text, 'CASH_DRAWER'::text, 'PAYMENT_TERMINAL'::text, 'A4_PRINTER'::text]))
- `device_registry_status_check`: CHECK (status = ANY (ARRAY['CONNECTED'::text, 'DISCONNECTED'::text, 'UNKNOWN'::text, 'NOT_CONFIGURED'::text, 'ERROR'::text]))
- `device_registry_station_id_fkey`: FOREIGN KEY (station_id) REFERENCES core.stations(station_id)
- `device_registry_pkey`: PRIMARY KEY (device_id)

**الفهارس:**

- `idx_devices_station`: CREATE INDEX idx_devices_station ON print.device_registry USING btree (station_id)

</details>

### print.document_templates

> قالب مستقل لكل نوع مستند × وسيط — ممنوع تكبير/تصغير قالب وسيط لآخر؛ نسخة القالب تحفظ مع كل مهمة فلا يتغير مستند مرحل بتغيير القالب

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `template_id` | `bigint` | لا |  |  |
| `template_code` | `text` | لا |  |  |
| `document_type` | `text` | لا |  |  |
| `media_class` | `text` | لا |  |  |
| `version` | `integer` | لا | 1 |  |
| `body_definition` | `jsonb` | لا |  |  |
| `is_active` | `boolean` | لا | true |  |
| `created_at` | `timestamp with time zone` | لا | now() |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `document_templates_media_class_check`: CHECK (media_class = ANY (ARRAY['ROLL_80'::text, 'ROLL_58'::text, 'A4_DETAILED'::text, 'LABEL'::text]))
- `document_templates_pkey`: PRIMARY KEY (template_id)
- `document_templates_template_code_document_type_media_class__key`: UNIQUE (template_code, document_type, media_class, version)

**الفهارس:**

- `document_templates_template_code_document_type_media_class__key`: CREATE UNIQUE INDEX document_templates_template_code_document_type_media_class__key ON print.document_templates USING btree (template_code, document_type, media_class, version)

</details>

### print.print_jobs

> مهمة طباعة مستقلة لكل مستند: فشلها لا يلغي البيع — لا ينشأ مستند مالي عند إعادة الطباعة، ولا يتحول الوسيط عند الفشل (§29.9)

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `job_id` | `bigint` | لا |  |  |
| `document_id` | `bigint` | لا |  |  |
| `document_type` | `text` | لا |  |  |
| `template_id` | `bigint` | لا |  |  |
| `template_version` | `integer` | لا |  |  |
| `printer_profile_id` | `bigint` | لا |  |  |
| `printer_profile_version` | `integer` | لا |  |  |
| `media_class` | `text` | لا |  |  |
| `printable_width_mm` | `numeric(6,2)` | لا |  |  |
| `dpi` | `integer` | لا |  |  |
| `copy_type` | `text` | لا |  |  |
| `status` | `text` | لا | 'QUEUED'::text |  |
| `attempt_count` | `integer` | لا | 0 |  |
| `max_attempts` | `integer` | لا | 3 |  |
| `failure_reason` | `text` | نعم |  |  |
| `idempotency_key` | `text` | لا |  |  |
| `requested_by` | `bigint` | لا |  |  |
| `correlation_id` | `text` | نعم |  |  |
| `related_reprint_of` | `bigint` | نعم |  | إعادة الطباعة الصريحة مهمة جديدة مرتبطة بالأصل بعلامة نسخة إعادة طباعة — لا نسخة مالية جديدة |
| `created_at` | `timestamp with time zone` | لا | now() |  |
| `completed_at` | `timestamp with time zone` | نعم |  |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `print_jobs_attempt_count_check`: CHECK (attempt_count >= 0)
- `print_jobs_copy_type_check`: CHECK (copy_type = ANY (ARRAY['CUSTOMER'::text, 'ADMIN'::text, 'REPRINT'::text, 'ORIGINAL'::text, 'PAYMENT_VOUCHER'::text, 'STATEMENT'::text]))
- `print_jobs_media_class_check`: CHECK (media_class = ANY (ARRAY['ROLL_80'::text, 'ROLL_58'::text, 'A4_DETAILED'::text, 'LABEL'::text]))
- `print_jobs_status_check`: CHECK (status = ANY (ARRAY['QUEUED'::text, 'PROCESSING'::text, 'SUBMITTED_TO_SPOOLER'::text, 'COMPLETED_CONFIRMED'::text, 'FAILED'::text, 'UNKNOWN'::text, 'HELD_CONFIGURATION_MISMATCH'::text, 'BLOCKED_PRINTER_UNAVAILABLE'::text, 'CANCELLED'::text, 'REPRINT_REQUESTED'::text]))
- `print_jobs_printer_profile_id_fkey`: FOREIGN KEY (printer_profile_id) REFERENCES print.printer_profiles(printer_profile_id)
- `print_jobs_related_reprint_of_fkey`: FOREIGN KEY (related_reprint_of) REFERENCES print.print_jobs(job_id)
- `print_jobs_requested_by_fkey`: FOREIGN KEY (requested_by) REFERENCES core.users(user_id)
- `print_jobs_template_id_fkey`: FOREIGN KEY (template_id) REFERENCES print.document_templates(template_id)
- `print_jobs_pkey`: PRIMARY KEY (job_id)
- `print_jobs_idempotency_key_key`: UNIQUE (idempotency_key)

**الفهارس:**

- `idx_print_jobs_correlation`: CREATE INDEX idx_print_jobs_correlation ON print.print_jobs USING btree (correlation_id) WHERE (correlation_id IS NOT NULL)
- `idx_print_jobs_doc`: CREATE INDEX idx_print_jobs_doc ON print.print_jobs USING btree (document_type, document_id)
- `idx_print_jobs_status`: CREATE INDEX idx_print_jobs_status ON print.print_jobs USING btree (status, created_at)
- `print_jobs_idempotency_key_key`: CREATE UNIQUE INDEX print_jobs_idempotency_key_key ON print.print_jobs USING btree (idempotency_key)

</details>

### print.printer_document_roles

> دور الطابعة لكل محطة ونوع مستند — مستند حساس لا يذهب لطابعة مشتركة دون سياسة وصول (اختبار 21)

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `role_assignment_id` | `bigint` | لا |  |  |
| `printer_profile_id` | `bigint` | لا |  |  |
| `station_id` | `bigint` | لا |  |  |
| `document_role` | `text` | لا |  |  |
| `is_auto_routing_enabled` | `boolean` | لا | false |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `printer_document_roles_document_role_check`: CHECK (document_role = ANY (ARRAY['CUSTOMER_RECEIPT'::text, 'ADMIN_COPY'::text, 'SUPPLIER_DOCUMENTS'::text, 'FINANCE_VOUCHERS'::text, 'CONFIDENTIAL_PAYROLL'::text, 'A4_DETAILED_REPORTS'::text, 'BARCODE_LABELS'::text]))
- `printer_document_roles_printer_profile_id_fkey`: FOREIGN KEY (printer_profile_id) REFERENCES print.printer_profiles(printer_profile_id)
- `printer_document_roles_station_id_fkey`: FOREIGN KEY (station_id) REFERENCES core.stations(station_id)
- `printer_document_roles_pkey`: PRIMARY KEY (role_assignment_id)
- `printer_document_roles_station_id_document_role_key`: UNIQUE (station_id, document_role)

**الفهارس:**

- `printer_document_roles_station_id_document_role_key`: CREATE UNIQUE INDEX printer_document_roles_station_id_document_role_key ON print.printer_document_roles USING btree (station_id, document_role)

</details>

### print.printer_profiles

> ملف الطابعة المعتمد: تغيير الجهاز/الوسيط يوقف التوجيه ويعاير ويعتمد من مسؤول قبل استئناف AUTO (§29.7.3) — لا يعني 80mm أن القابل للطباعة 80mm

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `printer_profile_id` | `bigint` | لا |  |  |
| `printer_id` | `text` | لا |  |  |
| `windows_queue_name` | `text` | لا |  |  |
| `device_model` | `text` | لا |  |  |
| `driver_name` | `text` | لا |  |  |
| `station_id` | `bigint` | نعم |  |  |
| `branch_id` | `bigint` | نعم |  |  |
| `media_class` | `text` | لا |  |  |
| `media_width_mm` | `numeric(6,2)` | لا |  |  |
| `printable_width_mm` | `numeric(6,2)` | لا |  |  |
| `verified_media_width_mm` | `numeric(6,2)` | نعم |  |  |
| `dpi_x` | `integer` | لا |  |  |
| `dpi_y` | `integer` | لا |  |  |
| `left_margin_mm` | `numeric(6,2)` | لا | 0 |  |
| `right_margin_mm` | `numeric(6,2)` | لا | 0 |  |
| `roll_mode` | `text` | نعم |  |  |
| `max_continuous_length_mm` | `numeric(10,2)` | نعم |  |  |
| `adapter_kind` | `text` | لا | 'ESC_POS'::text |  |
| `is_calibrated` | `boolean` | لا | false | التوجيه التلقائي معطل حتى نجاح معايرة الوسيط وTest Receipt موسوم (اختبار 25) |
| `last_test_at` | `timestamp with time zone` | نعم |  |  |
| `last_test_result` | `text` | نعم |  |  |
| `last_tested_by` | `bigint` | نعم |  |  |
| `profile_version` | `integer` | لا | 1 |  |
| `is_active` | `boolean` | لا | true |  |
| `created_at` | `timestamp with time zone` | لا | now() |  |
| `updated_at` | `timestamp with time zone` | لا | now() |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `printer_profiles_adapter_kind_check`: CHECK (adapter_kind = ANY (ARRAY['ESC_POS'::text, 'WINDOWS_DRIVER'::text, 'ZPL'::text, 'TSPL'::text]))
- `printer_profiles_dpi_x_check`: CHECK (dpi_x > 0)
- `printer_profiles_dpi_y_check`: CHECK (dpi_y > 0)
- `printer_profiles_last_test_result_check`: CHECK (last_test_result = ANY (ARRAY['PASS'::text, 'FAIL'::text, 'UNKNOWN'::text]))
- `printer_profiles_left_margin_mm_check`: CHECK (left_margin_mm >= 0::numeric)
- `printer_profiles_media_class_check`: CHECK (media_class = ANY (ARRAY['ROLL_80'::text, 'ROLL_58'::text, 'A4_DETAILED'::text, 'LABEL'::text]))
- `printer_profiles_media_width_mm_check`: CHECK (media_width_mm > 0::numeric)
- `printer_profiles_printable_width_mm_check`: CHECK (printable_width_mm > 0::numeric)
- `printer_profiles_right_margin_mm_check`: CHECK (right_margin_mm >= 0::numeric)
- `printer_profiles_roll_mode_check`: CHECK (roll_mode = ANY (ARRAY['CONTINUOUS'::text, 'AUTO_CUT'::text, 'TEAR_OFF'::text]))
- `printer_profiles_verified_media_width_mm_check`: CHECK (verified_media_width_mm > 0::numeric)
- `prn_verified_sane`: CHECK (verified_media_width_mm IS NULL OR abs(verified_media_width_mm - printable_width_mm) <= 2.0)
- `prn_widths_sane`: CHECK (printable_width_mm <= media_width_mm)
- `printer_profiles_branch_id_fkey`: FOREIGN KEY (branch_id) REFERENCES core.branches(branch_id)
- `printer_profiles_last_tested_by_fkey`: FOREIGN KEY (last_tested_by) REFERENCES core.users(user_id)
- `printer_profiles_station_id_fkey`: FOREIGN KEY (station_id) REFERENCES core.stations(station_id)
- `printer_profiles_pkey`: PRIMARY KEY (printer_profile_id)
- `printer_profiles_printer_id_key`: UNIQUE (printer_id)

**الفهارس:**

- `printer_profiles_printer_id_key`: CREATE UNIQUE INDEX printer_profiles_printer_id_key ON print.printer_profiles USING btree (printer_id)

</details>

## المخطط `audit` (3 جدولاً)

### audit.audit_logs

> سجل العمليات الحساسة: من نفّذ ومن أين ومتى وماذا تغير وبأي صلاحية — الفصل بين منشئ الفاتورة ومنفذها/معتمدها (§16 ق7)

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `audit_id` | `bigint` | لا |  |  |
| `occurred_at` | `timestamp with time zone` | لا | now() |  |
| `user_id` | `bigint` | لا |  |  |
| `role_code` | `text` | نعم |  |  |
| `station_id` | `bigint` | نعم |  |  |
| `branch_id` | `bigint` | نعم |  |  |
| `entity_schema` | `text` | لا |  |  |
| `entity_id` | `bigint` | لا |  |  |
| `action` | `text` | لا |  |  |
| `old_values` | `jsonb` | نعم |  |  |
| `new_values` | `jsonb` | نعم |  |  |
| `reason` | `text` | نعم |  |  |
| `result` | `text` | لا |  |  |
| `correlation_id` | `text` | نعم |  |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `audit_logs_result_check`: CHECK (result = ANY (ARRAY['SUCCESS'::text, 'DENIED'::text, 'FAILED'::text]))
- `audit_logs_branch_id_fkey`: FOREIGN KEY (branch_id) REFERENCES core.branches(branch_id)
- `audit_logs_station_id_fkey`: FOREIGN KEY (station_id) REFERENCES core.stations(station_id)
- `audit_logs_user_id_fkey`: FOREIGN KEY (user_id) REFERENCES core.users(user_id)
- `audit_logs_pkey`: PRIMARY KEY (audit_id)

**الفهارس:**

- `idx_audit_correlation`: CREATE INDEX idx_audit_correlation ON audit.audit_logs USING btree (correlation_id) WHERE (correlation_id IS NOT NULL)
- `idx_audit_entity`: CREATE INDEX idx_audit_entity ON audit.audit_logs USING btree (entity_schema, entity_id, occurred_at DESC)
- `idx_audit_user_time`: CREATE INDEX idx_audit_user_time ON audit.audit_logs USING btree (user_id, occurred_at DESC)

</details>

### audit.idempotency_keys

> إعادة إرسال نفس الطلب بعد timeout تُرجع النتيجة نفسها أو ترفض — لا فاتورة ثانية ولا حركة مخزون مكررة (T-CAP-003)

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `idempotency_key` | `text` | لا |  |  |
| `request_hash` | `text` | لا |  |  |
| `status` | `text` | لا | 'IN_PROGRESS'::text |  |
| `response_hash` | `text` | نعم |  |  |
| `created_at` | `timestamp with time zone` | لا | now() |  |
| `expires_at` | `timestamp with time zone` | لا |  |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `idem_expiry_future`: CHECK (expires_at > created_at)
- `idempotency_keys_status_check`: CHECK (status = ANY (ARRAY['IN_PROGRESS'::text, 'COMPLETED'::text, 'FAILED'::text, 'TIMEOUT'::text]))
- `idempotency_keys_pkey`: PRIMARY KEY (idempotency_key)

</details>

### audit.user_sessions

> جلسات الدخول لكل محطة — تبديل المستخدم لا يسرب جلسة سابقة (اختبار §2 ق16)

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `session_id` | `bigint` | لا |  |  |
| `user_id` | `bigint` | لا |  |  |
| `station_id` | `bigint` | نعم |  |  |
| `login_at` | `timestamp with time zone` | لا | now() |  |
| `logout_at` | `timestamp with time zone` | نعم |  |  |
| `ip_address` | `inet` | نعم |  |  |
| `outcome` | `text` | لا | 'ACTIVE'::text |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `user_sessions_outcome_check`: CHECK (outcome = ANY (ARRAY['ACTIVE'::text, 'LOGOUT'::text, 'EXPIRED'::text, 'KICKED'::text]))
- `user_sessions_station_id_fkey`: FOREIGN KEY (station_id) REFERENCES core.stations(station_id)
- `user_sessions_user_id_fkey`: FOREIGN KEY (user_id) REFERENCES core.users(user_id)
- `user_sessions_pkey`: PRIMARY KEY (session_id)

**الفهارس:**

- `idx_sessions_user_active`: CREATE INDEX idx_sessions_user_active ON audit.user_sessions USING btree (user_id) WHERE (outcome = 'ACTIVE'::text)

</details>

## المخطط `ops` (3 جدولاً)

### ops.backup_records

> النسخ الاحتياطي: لا يعلن النجاح إلا بعد تحقق اكتمال وقابلية قراءة — النسخة على نفس القرص ليست خطة استعادة (§19، D-09)

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `backup_id` | `bigint` | لا |  |  |
| `backup_kind` | `text` | لا |  |  |
| `started_at` | `timestamp with time zone` | لا |  |  |
| `finished_at` | `timestamp with time zone` | نعم |  |  |
| `size_bytes` | `bigint` | نعم |  |  |
| `status` | `text` | لا | 'RUNNING'::text |  |
| `scope` | `text` | لا |  |  |
| `storage_location_ref` | `text` | لا |  |  |
| `backup_set_id` | `text` | لا |  |  |
| `verified_readable` | `boolean` | لا | false |  |
| `error_summary` | `text` | نعم |  |  |
| `created_at` | `timestamp with time zone` | لا | now() |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `backup_finished_after_start`: CHECK (finished_at IS NULL OR finished_at >= started_at)
- `backup_records_backup_kind_check`: CHECK (backup_kind = ANY (ARRAY['FULL'::text, 'INCREMENTAL'::text, 'MONTHLY_ARCHIVE'::text]))
- `backup_records_size_bytes_check`: CHECK (size_bytes IS NULL OR size_bytes > 0)
- `backup_records_status_check`: CHECK (status = ANY (ARRAY['RUNNING'::text, 'SUCCEEDED'::text, 'FAILED'::text, 'VERIFYING'::text, 'VERIFIED'::text]))
- `backup_records_pkey`: PRIMARY KEY (backup_id)

</details>

### ops.import_errors

> تقرير الصفوف الفاشلة لكل دفعة استيراد — رسالة توضح الكود المتعارض دون إنشاء سجلات ناقصة (§3.1)

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `error_id` | `bigint` | لا |  |  |
| `import_id` | `bigint` | لا |  |  |
| `row_ref` | `text` | لا |  |  |
| `error_code` | `text` | لا |  |  |
| `error_detail` | `text` | نعم |  |  |
| `created_at` | `timestamp with time zone` | لا | now() |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `import_errors_import_id_fkey`: FOREIGN KEY (import_id) REFERENCES ops.import_jobs(import_id) ON DELETE CASCADE
- `import_errors_pkey`: PRIMARY KEY (error_id)

**الفهارس:**

- `idx_import_errors_job`: CREATE INDEX idx_import_errors_job ON ops.import_errors USING btree (import_id)

</details>

### ops.import_jobs

> استيراد الخلفية: تقدم قابل للإلغاء والاستئناف من سجل ثابت — إعادة الطلب بعد timeout لا تكرر الصفوف (T-CAP-004)

| العمود | النوع | فارغ مسموح | افتراضي | الوصف |
|---|---|---|---|---|
| `import_id` | `bigint` | لا |  |  |
| `import_kind` | `text` | لا |  |  |
| `status` | `text` | لا | 'PENDING'::text |  |
| `total_rows` | `integer` | لا | 0 |  |
| `processed_rows` | `integer` | لا | 0 |  |
| `failed_rows` | `integer` | لا | 0 |  |
| `resume_cursor` | `jsonb` | لا | '{}'::jsonb |  |
| `started_at` | `timestamp with time zone` | نعم |  |  |
| `finished_at` | `timestamp with time zone` | نعم |  |  |
| `requested_by` | `bigint` | لا |  |  |
| `created_at` | `timestamp with time zone` | لا | now() |  |

<details><summary>القيود والفهارس</summary>

**القيود:**

- `import_counts_sane`: CHECK ((processed_rows + failed_rows) <= GREATEST(total_rows, processed_rows + failed_rows))
- `import_jobs_import_kind_check`: CHECK (import_kind = ANY (ARRAY['PRODUCTS'::text, 'IMAGES'::text, 'BARCODES'::text, 'OPENING_STOCK'::text]))
- `import_jobs_status_check`: CHECK (status = ANY (ARRAY['PENDING'::text, 'RUNNING'::text, 'CANCELLED'::text, 'COMPLETED'::text, 'COMPLETED_WITH_ERRORS'::text]))
- `import_jobs_requested_by_fkey`: FOREIGN KEY (requested_by) REFERENCES core.users(user_id)
- `import_jobs_pkey`: PRIMARY KEY (import_id)

</details>

---
**إجمالي:** 79 جدولاً في 9 مخططات + منظر `inv.v_stock_ledger_check`.
