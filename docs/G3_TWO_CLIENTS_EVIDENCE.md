# دليل بوابة G3 — عميلان متصلان بخادم الاختبار عبر TCP حقيقي

- التاريخ (UTC): 2026-11-02 20:12
- الخادم: ASP.NET Core 10 (Kestrel) على http://localhost:5400 — Release build
- قاعدة الاختبار: PostgreSQL 16 على localhost:55432 (ترحيلات V001..V014 مطبقة)
- العميلان: عميلان مستقلان متزامنان (خيطان) بجلسات JWT منفصلتين

## ملخص النتيجة

- العميل أ (مالك): أدوار ['OWNER'] · صلاحيات 58
- العميل ب (كاشير/فرع): أدوار ['CASHIER'] · قراءة الإعدادات: True

## السجل التفصيلي (عميل | عملية | حالة | تفصيل)

| A | login | 200 | wera-live-client-A |
| B | login(branch) | 200 | wera-live-client-B |
| A | settings(INVENTORY) | 200 | 2 |
| B | settings(WEIGHT) | 200 | precision=1g step=5g |
| B | branches | 200 | 1 |
| B | stations | 200 | 1 |
| A | audit/logins | 200 | 2 |
| B | audit/logins (RBAC deny) | 403 | 403 expected |
| A | 5x settings(WEIGHT) parallel | all-200 |  |
| A | logout | 204 |  |
| B | logout | 204 |  |

**الحكم: PASS — العميلان خُدما كامل الدورة بتزامن وبلا أخطاء**

كل طلب حمل معرف ترابط X-Correlation-ID فريداً وصل إلى سجل التدقيق (audit.audit_logs.correlation_id).