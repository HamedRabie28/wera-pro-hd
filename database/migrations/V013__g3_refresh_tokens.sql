-- ═══════════════════════════════════════════════════════════════════════════
-- WERA PRO HD — Migration V013 · G3: رموز التجديد (Refresh Tokens)
-- المرجع: GFM-CSharp 2.4.1 §11.2 «إدارة جلسات آمنة، وتدوير/إلغاء الجلسة عند
-- تسجيل الخروج أو تغيير كلمة المرور» + §18 (لا أسرار في السجلات).
--
-- الغرض: تخزين رموز تجديد الجلسة على الخادم بصيغة hash فقط (SHA-256) —
-- لا تُخزن الرموز صريحة أبداً، والتدوير عند كل استخدام، والإلغاء عند الخروج.
-- المفتاح مرتبط بجلسة audit.user_sessions لتتبع «من أين ومتى» (V008 تسبق هذا الترحيل).
-- إضافة جدول جديد فقط (Forward Fix) — لا تعديل على أي ترحيل مطبق سابقاً.
-- ═══════════════════════════════════════════════════════════════════════════

BEGIN;

CREATE TABLE core.refresh_tokens (
    refresh_token_id  BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    session_id        BIGINT NOT NULL REFERENCES audit.user_sessions(session_id),
    token_hash        TEXT NOT NULL UNIQUE,          -- sha256 للرمز فقط — لا نص صريح أبداً
    user_id           BIGINT NOT NULL REFERENCES core.users(user_id),
    issued_at         TIMESTAMPTZ NOT NULL DEFAULT now(),
    expires_at        TIMESTAMPTZ NOT NULL,
    rotated_to_id     BIGINT,                        -- رمز التجديد التالي عند التدوير
    revoked_at        TIMESTAMPTZ,                   -- لحظة الإلغاء (خروج/تغيير كلمة مرور)
    revoked_reason    TEXT,
    CONSTRAINT refresh_expiry_future CHECK (expires_at > issued_at),
    CONSTRAINT refresh_revoked_after_issued CHECK (revoked_at IS NULL OR revoked_at >= issued_at),
    CONSTRAINT refresh_tokens_rotated_to_fk FOREIGN KEY (rotated_to_id)
        REFERENCES core.refresh_tokens(refresh_token_id)
);
COMMENT ON TABLE core.refresh_tokens IS 'رموز تجديد الجلسة: hash فقط على الخادم، تدوير عند كل استخدام، إلغاء عند الخروج/تغيير كلمة المرور (§11.2)';
COMMENT ON COLUMN core.refresh_tokens.token_hash IS 'SHA-256 للرمز — تخزين الرمز الصريح ممنوع منعًا باتًا (§18)';
COMMENT ON COLUMN core.refresh_tokens.rotated_to_id IS 'الرمز الذي حل محل هذا الرمز عند التدوير — استخدام رمز مُدار يعني إلغاء السلسلة (كشف إعادة الاستخدام)';
COMMENT ON COLUMN core.refresh_tokens.revoked_reason IS 'سبب الإلغاء: LOGOUT / PASSWORD_CHANGED / REVOKED_BY_OWNER / REUSE_DETECTED';

CREATE INDEX idx_refresh_tokens_user_active
    ON core.refresh_tokens (user_id)
    WHERE revoked_at IS NULL AND rotated_to_id IS NULL;

-- ═══ تحقق داخلي ═══
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.tables
                    WHERE table_schema='core' AND table_name='refresh_tokens') THEN
        RAISE EXCEPTION 'V013 check failed: core.refresh_tokens not created';
    END IF;
END $$;

COMMIT;
