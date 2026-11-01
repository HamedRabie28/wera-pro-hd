-- ═══════════════════════════════════════════════════════════════════════════
-- WERA PRO HD — Migration V002 · طبقة catalog: المنتجات والـSKU والوحدات والعبوات
-- والباركود وPLU وملفات الوزن المتغير والقوائم السعرية والصور
-- المرجع: GFM-CSharp 2.4.1 §3 (سعة 5M/10M) · §4 (نموذج المنتج) · §5 (الوزني) · §6 (الصور)
-- قاعدة البوابة: لا خلط بين Product (الهوية) وSKU (صيغة البيع) والعبوة (عامل تحويل) والباركود (معرف خارجي)
-- ═══════════════════════════════════════════════════════════════════════════

BEGIN;

-- ── 1) الفئات والعلامات ─────────────────────────────────────────────────────
CREATE TABLE catalog.categories (
    category_id   BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    parent_id     BIGINT REFERENCES catalog.categories(category_id),
    code          TEXT NOT NULL UNIQUE,
    name_ar       TEXT NOT NULL,
    name_en       TEXT,
    is_active     BOOLEAN NOT NULL DEFAULT TRUE,
    CONSTRAINT categories_not_self_parent CHECK (parent_id IS NULL OR parent_id <> category_id)
);
COMMENT ON TABLE catalog.categories IS 'شجرة الفئات: تعيين ذاتي الأب ممنوع — الدورة العميقة تُمنع في طبقة المجال وتُختبر في T-CAT-006';

CREATE TABLE catalog.brands (
    brand_id    BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    code        TEXT NOT NULL UNIQUE,
    name_ar     TEXT NOT NULL,
    name_en     TEXT,
    is_active   BOOLEAN NOT NULL DEFAULT TRUE
);

-- ── 2) وحدات القياس والتحويلات (§4.1: بُعد منضبط — لا تحويل بين أبعاد غير متوافقة) ──
CREATE TABLE catalog.units (
    uom_id      BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    code        TEXT NOT NULL UNIQUE,             -- PIECE/KG/G/L/ML/M/CM
    name_ar     TEXT NOT NULL,
    dimension   TEXT NOT NULL CHECK (dimension IN ('COUNT','MASS','VOLUME','LENGTH')),
    is_base     BOOLEAN NOT NULL DEFAULT FALSE,   -- وحدة أساسية للبعد
    decimals    SMALLINT NOT NULL DEFAULT 3 CHECK (decimals BETWEEN 0 AND 6)
);
COMMENT ON TABLE catalog.units IS 'وحدات القياس: كل وحدة منضبطة ببعدها — التحويل مسموح فقط داخل البعد نفسه (كجم→جرام نعم، كجم→لتر لا)';

CREATE TABLE catalog.unit_conversions (
    conversion_id   BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    from_uom_id     BIGINT NOT NULL REFERENCES catalog.units(uom_id),
    to_uom_id       BIGINT NOT NULL REFERENCES catalog.units(uom_id),
    factor          NUMERIC(18,6) NOT NULL,       -- الكمية الأساسية = الكمية × factor
    is_fixed        BOOLEAN NOT NULL DEFAULT TRUE,-- تحويل تعريفي (1kg=1000g) لا يُعدل
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT conv_from_neq_to CHECK (from_uom_id <> to_uom_id),
    CONSTRAINT conv_factor_positive CHECK (factor > 0)
);
COMMENT ON TABLE catalog.unit_conversions IS 'التحويلات الصريحة: الكمية في وحدة المصدر × factor = الكمية بالوحدة الأساسية — عاملة التحويل دائماً موجبة';

-- خط حماية: منع التحويل بين وحدات من أبعاد مختلفة (INV-CAT-02)
CREATE OR REPLACE FUNCTION catalog.tf_unit_conversion_dimension()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    d_from TEXT; d_to TEXT;
BEGIN
    SELECT dimension INTO d_from FROM catalog.units WHERE uom_id = NEW.from_uom_id;
    SELECT dimension INTO d_to   FROM catalog.units WHERE uom_id = NEW.to_uom_id;
    IF d_from IS DISTINCT FROM d_to THEN
        RAISE EXCEPTION 'UNIT_DIMENSION_MISMATCH: التحويل مرفوض بين وحدتين من بُعدين مختلفين (%) <> (%)', d_from, d_to
            USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END $$;

CREATE TRIGGER trg_unit_conversion_dimension
    BEFORE INSERT OR UPDATE ON catalog.unit_conversions
    FOR EACH ROW EXECUTE FUNCTION catalog.tf_unit_conversion_dimension();

-- خط حماية: منع دورات التحويل (أ→ب + ب→أ مرفوضان معاً) (INV-CAT-03)
CREATE OR REPLACE FUNCTION catalog.tf_unit_conversion_no_cycle()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM catalog.unit_conversions
        WHERE from_uom_id = NEW.to_uom_id AND to_uom_id = NEW.from_uom_id
    ) THEN
        RAISE EXCEPTION 'UNIT_CONVERSION_CYCLE: يوجد تحويل معاكس (%→%) — الدوائر ممنوعة', NEW.to_uom_id, NEW.from_uom_id
            USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END $$;

CREATE TRIGGER trg_unit_conversion_no_cycle
    BEFORE INSERT OR UPDATE ON catalog.unit_conversions
    FOR EACH ROW EXECUTE FUNCTION catalog.tf_unit_conversion_no_cycle();

-- ── 3) المنتج الأساسي (الهوية — لا سعر ولا باركود هنا) ──────────────────────
CREATE TABLE catalog.products (
    product_id      BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    code            TEXT NOT NULL UNIQUE,                  -- كود داخلي ثابت
    name_ar         TEXT NOT NULL,
    name_en         TEXT,
    category_id     BIGINT NOT NULL REFERENCES catalog.categories(category_id),
    brand_id        BIGINT REFERENCES catalog.brands(brand_id),
    description     TEXT,
    status          TEXT NOT NULL DEFAULT 'ACTIVE' CHECK (status IN ('ACTIVE','INACTIVE','DISCONTINUED')),
    track_lots      BOOLEAN NOT NULL DEFAULT FALSE,        -- تتبع الدفعات/الصلاحية
    track_serials   BOOLEAN NOT NULL DEFAULT FALSE,        -- تتبع السيريال
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);
COMMENT ON TABLE catalog.products IS 'المنتج الأساسي = الهوية فقط (اسم/فئة/علامة/خصائص تتبع) — الأسعار والباركود وصيغ البيع كلها على SKUs';
COMMENT ON COLUMN catalog.products.track_lots IS 'تشغيل تتبع الدفعة/الصلاحية — عند TRUE تصبح حركات الاستلام ملزمة بـ inv.lots';

CREATE INDEX idx_products_category  ON catalog.products (category_id) WHERE status = 'ACTIVE';
CREATE INDEX idx_products_brand     ON catalog.products (brand_id);
CREATE INDEX idx_products_status    ON catalog.products (status);
CREATE INDEX idx_products_updated   ON catalog.products (updated_at);
CREATE INDEX idx_products_name_ar   ON catalog.products (lower(name_ar));

-- ── 4) SKU — صيغة البيع الصريحة (§4.2: sale_mode لا يُستنتج أبداً) ───────────
CREATE TABLE catalog.skus (
    sku_id          BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    product_id      BIGINT NOT NULL REFERENCES catalog.products(product_id),
    sku_code        TEXT NOT NULL UNIQUE,                  -- كود SKU فريد على مستوى النشاط
    sale_mode       TEXT NOT NULL CHECK (sale_mode IN ('COUNT','WEIGHT','PACK','SERIALIZED')),
    base_uom_id     BIGINT NOT NULL REFERENCES catalog.units(uom_id),   -- وحدة المخزون الأساسية
    sale_uom_id     BIGINT NOT NULL REFERENCES catalog.units(uom_id),   -- وحدة البيع الافتراضية
    status          TEXT NOT NULL DEFAULT 'ACTIVE' CHECK (status IN ('ACTIVE','INACTIVE','RETIRED')),
    -- إعدادات الوزن (تُعبأ لصيغة WEIGHT فقط — §4.4 هـ)
    weight_precision_g   INTEGER,                          -- دقة الوزن بالجرام (D-07: افتراضي 1)
    weight_step_g        INTEGER,                          -- خطوة الوزن بالجرام (D-07: افتراضي 5)
    allow_manual_weight  BOOLEAN NOT NULL DEFAULT TRUE,    -- السماح بالإدخال اليدوي (مسجَّل المصدر)
    allow_amount_target  BOOLEAN NOT NULL DEFAULT TRUE,    -- السماح بالبيع بمبلغ مستهدف
    min_weight_kg        NUMERIC(14,3),                    -- حدود الوزن المقبولة
    max_weight_kg        NUMERIC(14,3),
    default_tare_kg      NUMERIC(14,3) NOT NULL DEFAULT 0,
    -- إعدادات المخزون (§4.4 ز)
    reorder_level   NUMERIC(18,6) NOT NULL DEFAULT 0,
    allow_negative  BOOLEAN,                               -- NULL = يتبع سياسة النشاط D-03
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT sku_weight_mode_fields CHECK (
        sale_mode <> 'WEIGHT'
        OR (weight_precision_g IS NOT NULL AND weight_step_g IS NOT NULL
            AND weight_precision_g > 0 AND weight_step_g > 0
            AND weight_step_g >= weight_precision_g)),
    CONSTRAINT sku_weight_bounds CHECK (
        min_weight_kg IS NULL OR max_weight_kg IS NULL OR min_weight_kg < max_weight_kg),
    CONSTRAINT sku_weight_positive CHECK (
        min_weight_kg IS NULL OR min_weight_kg > 0),
    CONSTRAINT sku_tare_nonnegative CHECK (default_tare_kg >= 0)
);
COMMENT ON TABLE catalog.skus IS 'SKU = صيغة قابلة للبيع بsale_mode صريح: فرد/كيس/علبة/كرتونة/وزن — للمنتج الواحد عدة SKUs لكل منها باركود وسعر مستقلان';
COMMENT ON COLUMN catalog.skus.sale_mode IS 'COUNT عددي · WEIGHT وزني متغير · PACK عبوة بعامل تحويل معروف · SERIALIZED يتطلب رقم تسلسلي لكل وحدة';
COMMENT ON COLUMN catalog.skus.allow_negative IS 'تفريد سياسة السالب لهذا الصنف (NULL = يتبع إعداد النشاط inventory.negative_stock_policy — D-03)';

CREATE INDEX idx_skus_product   ON catalog.skus (product_id);
CREATE INDEX idx_skus_mode      ON catalog.skus (sale_mode) WHERE status = 'ACTIVE';
CREATE INDEX idx_skus_status    ON catalog.skus (status);

-- ── 5) تعريف العبوة (§4.3: علبة 12 كيساً — ربط صيغة البيع بالمخزون الأساسي) ──
CREATE TABLE catalog.packages (
    package_id          BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    package_sku_id      BIGINT NOT NULL UNIQUE REFERENCES catalog.skus(sku_id),  -- SKU العبوة (PACK)
    content_sku_id      BIGINT NOT NULL REFERENCES catalog.skus(sku_id),        -- SKU المحتوى (يُخصم منه المخزون)
    units_per_package   NUMERIC(18,6) NOT NULL,       -- عدد الوحدات الأساسية داخل العبوة (12)
    open_package_allowed BOOLEAN NOT NULL DEFAULT TRUE, -- السماح بفتح العبوة وبيع مفرد
    valid_from          TIMESTAMPTZ NOT NULL DEFAULT now(),
    valid_to            TIMESTAMPTZ,                  -- إصدار تعريف جديد لا يعدل التاريخ (§4.3 ق9)
    CONSTRAINT pkg_units_positive CHECK (units_per_package > 0),
    CONSTRAINT pkg_not_self CHECK (package_sku_id <> content_sku_id)
);

-- خط حماية: SKU العبوة يجب أن تكون صيغة بيعها PACK أو COUNT (لا تُربط عبوة بـSKU وزنية)
CREATE OR REPLACE FUNCTION catalog.tf_package_mode_guard()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE v_mode TEXT;
BEGIN
    SELECT sale_mode INTO v_mode FROM catalog.skus WHERE sku_id = NEW.package_sku_id;
    IF v_mode NOT IN ('PACK','COUNT') THEN
        RAISE EXCEPTION 'PACKAGE_MODE_INVALID: SKU العبوة (%) صيغتها % — يجب أن تكون PACK أو COUNT', NEW.package_sku_id, v_mode
            USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END $$;

CREATE TRIGGER trg_package_mode_guard
    BEFORE INSERT OR UPDATE ON catalog.packages
    FOR EACH ROW EXECUTE FUNCTION catalog.tf_package_mode_guard();
COMMENT ON TABLE catalog.packages IS 'تعريف العبوة: بيع العلبة يخصم units_per_package من رصيد المحتوى — أثر مخزون واحد فقط مهما اختلف شكل البيع (§4.3 ق5)';
COMMENT ON COLUMN catalog.packages.valid_to IS 'إغلاق التعريف المؤرخ لا يغيّر المستندات التاريخية — التعديل = إصدار جديد يؤثر في المعاملات الجديدة فقط';

CREATE INDEX idx_packages_content ON catalog.packages (content_sku_id);

-- ── 6) الباركود — معرف خارجي يخص SKU واحدة (تفرد نشط على المستوى المطَبَّع) ──
CREATE TABLE catalog.barcodes (
    barcode_id          BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    sku_id              BIGINT NOT NULL REFERENCES catalog.skus(sku_id),
    barcode_value       TEXT NOT NULL,                 -- القيمة كما هي مطبوعة/مقروءة
    barcode_normalized  TEXT NOT NULL,                 -- المطابقة الدقيقة (Exact Match §3.1)
    barcode_type        TEXT NOT NULL CHECK (barcode_type IN ('EAN13','EAN8','UPC_A','UPC_E','CODE128','CODE39','GS1_128','INTERNAL')),
    status              TEXT NOT NULL DEFAULT 'ACTIVE' CHECK (status IN ('ACTIVE','INACTIVE','RETIRED')),
    is_printed_label    BOOLEAN NOT NULL DEFAULT FALSE,-- باركود ملصق مطبوع داخلياً
    created_at          TIMESTAMPTZ NOT NULL DEFAULT now()
);
COMMENT ON TABLE catalog.barcodes IS 'كل باركود يقود إلى SKU واحدة فحسب — البحث عنه Exact Match بفهرس فريد على القيمة المنمّطة النشطة، لا بحث نصي عام';
COMMENT ON COLUMN catalog.barcodes.barcode_normalized IS 'قيمة موحّدة (trim + أرقام فقط للـ EAN/UPC) — فهرس فريد جزئي على الحالة النشطة يفرض نطاق التفرد';

-- فهرس فريد جزئي: باركود نشط واحد لكل قيمة منمّطة (نطاق التفرد المعتمد — §3.1)
CREATE UNIQUE INDEX uq_barcodes_normalized_active
    ON catalog.barcodes (barcode_normalized) WHERE status = 'ACTIVE';
CREATE INDEX idx_barcodes_sku ON catalog.barcodes (sku_id);

-- ── 7) PLU وملفات باركود الوزن المتغير (§5.3 ط6: تحليل البادئة والطول دون تخمين) ──
CREATE TABLE catalog.plus (
    plu_id      BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    plu_code    TEXT NOT NULL UNIQUE,                  -- 4–5 خانات كما في الميزان
    sku_id      BIGINT NOT NULL REFERENCES catalog.skus(sku_id),
    is_active   BOOLEAN NOT NULL DEFAULT TRUE
);
COMMENT ON TABLE catalog.plus IS 'أكواد PLU للميزان والملصقات — تربط الكود بالـSKU الوزنية مباشرة';

CREATE TABLE catalog.vw_barcode_profiles (
    profile_id      BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name            TEXT NOT NULL,
    prefix          TEXT NOT NULL,                     -- بادئة النظام (مثل 20/21/22)
    total_length    SMALLINT NOT NULL,                 -- الطول الكلي للباركود
    plu_start_pos   SMALLINT NOT NULL,                 -- موضع PLU (1-based من اليسار)
    plu_length      SMALLINT NOT NULL,
    value_type      TEXT NOT NULL CHECK (value_type IN ('WEIGHT','PRICE')),  -- الحقل المشفر وزن أم سعر
    value_start_pos SMALLINT NOT NULL,
    value_length    SMALLINT NOT NULL,
    value_decimals  SMALLINT NOT NULL DEFAULT 3 CHECK (value_decimals BETWEEN 0 AND 3),
    value_unit_id   BIGINT REFERENCES catalog.units(uom_id),  -- وحدة الوزن المشفرة (بُعد MASS)
    check_digit_rule TEXT NOT NULL DEFAULT 'NONE' CHECK (check_digit_rule IN ('NONE','MOD10','MOD10_WEIGHTED')),
    is_active       BOOLEAN NOT NULL DEFAULT TRUE,
    CONSTRAINT vw_segments_valid CHECK (
        plu_start_pos > 0 AND value_start_pos > 0
        AND plu_length > 0 AND value_length > 0
        AND plu_start_pos + plu_length <= total_length
        AND value_start_pos + value_length <= total_length
        AND total_length BETWEEN 8 AND 20)
);
COMMENT ON TABLE catalog.vw_barcode_profiles IS 'ملف باركود الوزن/السعر المتغير: البادئة والطول ومواضع PLU والقيمة ورقم التحقق — الصيغة المجهولة تُرفض لا تُخمَّن (§5.3 ق6)';

-- ── 8) القوائم السعرية وأسعار الـSKU (§4.4 د: لا سعر صفر غير مقصود) ──────────
CREATE TABLE catalog.price_lists (
    price_list_id   BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    code            TEXT NOT NULL UNIQUE,
    name_ar         TEXT NOT NULL,
    currency        TEXT NOT NULL DEFAULT 'EGP',       -- إعداد نشاط قابل للتغيير (§2 ق9)
    is_default      BOOLEAN NOT NULL DEFAULT FALSE,
    is_active       BOOLEAN NOT NULL DEFAULT TRUE
);
COMMENT ON TABLE catalog.price_lists IS 'قوائم الأسعار: العملة إعداد لكل قائمة (EGP افتراضياً) — لا تجميع بين عملات دون تحويل موثق (§15.4)';

CREATE TABLE catalog.product_prices (
    price_id        BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    sku_id          BIGINT NOT NULL REFERENCES catalog.skus(sku_id),
    price_list_id   BIGINT NOT NULL REFERENCES catalog.price_lists(price_list_id),
    unit_id         BIGINT NOT NULL REFERENCES catalog.units(uom_id),  -- وحدة السعر (للكيلو/للجرام/للقطعة)
    unit_price      NUMERIC(19,4) NOT NULL,            -- سعر الوحدة شامل الضريبة أو صافيها بحسب السياسة
    tax_treatment   TEXT NOT NULL DEFAULT 'TAXABLE' CHECK (tax_treatment IN ('TAXABLE','ZERO_RATED','EXEMPT')),
    valid_from      TIMESTAMPTZ NOT NULL DEFAULT now(),
    valid_to        TIMESTAMPTZ,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT price_positive CHECK (unit_price > 0),   -- السعر صفر غير مقصود يُرفض (§4.4 د)
    CONSTRAINT price_validity CHECK (valid_to IS NULL OR valid_to > valid_from)
);
COMMENT ON TABLE catalog.product_prices IS 'السعر فعالية زمنية: التطبيق يقرأ السعر الساري في لحظة البيع ويحفظ لقطة السعر في سطر الفاتورة (§16 ق6)';

CREATE UNIQUE INDEX uq_product_prices_sku_list_from
    ON catalog.product_prices (sku_id, price_list_id, valid_from);
CREATE INDEX idx_prices_lookup ON catalog.product_prices (sku_id, price_list_id, valid_from) WHERE valid_to IS NULL;

-- ── 9) مكتبة الصور (§6: لا BLOB داخل الصف — metadata ومسار تخزين فقط §3.1) ──
CREATE TABLE catalog.media_assets (
    media_id        BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    storage_key     TEXT NOT NULL UNIQUE,              -- معرف التخزين المعتمد
    sha256          TEXT NOT NULL,                     -- اكتشاف المكرر (§G5)
    mime_type       TEXT NOT NULL,
    width_px        INTEGER,
    height_px       INTEGER,
    size_bytes      BIGINT NOT NULL CHECK (size_bytes > 0),
    thumb_key       TEXT,                              -- مفتاح المصغرة
    uploaded_by     BIGINT REFERENCES core.users(user_id),
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);
COMMENT ON TABLE catalog.media_assets IS 'أصول الوسائط: بيانات وصفية وبصمة ومسار تخزين فقط — الصورة الأصلية ليست BLOB داخل صف المنتج (§3.1)';

CREATE INDEX idx_media_sha256 ON catalog.media_assets (sha256);

CREATE TABLE catalog.product_images (
    link_id     BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    product_id  BIGINT REFERENCES catalog.products(product_id),
    sku_id      BIGINT REFERENCES catalog.skus(sku_id),
    media_id    BIGINT NOT NULL REFERENCES catalog.media_assets(media_id),
    sort_order  INTEGER NOT NULL DEFAULT 0,
    is_primary  BOOLEAN NOT NULL DEFAULT FALSE,
    CONSTRAINT img_one_target CHECK (num_nonnulls(product_id, sku_id) = 1)
);
COMMENT ON TABLE catalog.product_images IS 'ربط الصور: أولوية الربط على مستوى SKU ثم المنتج — إزالة الربط لا تحذف الأصل إن كان مستخدماً في موضع آخر (§4.4 ح)';

CREATE UNIQUE INDEX uq_product_images_primary_sku
    ON catalog.product_images (sku_id) WHERE is_primary AND sku_id IS NOT NULL;
CREATE UNIQUE INDEX uq_product_images_primary_product
    ON catalog.product_images (product_id) WHERE is_primary AND product_id IS NOT NULL;

COMMIT;
