# PRINT ROUTING RULES — قواعد توجيه الطباعة تلقائياً
> **الوثيقة:** WERA-PR-002 · **الإصدار:** 1.0 · **التاريخ:** 2026-11-01 · **المرجع الناظم:** GFM-CSharp 2.4.1 §29.4 و§29.9

## 1. قاعدة التوجيه بعد ضبط الطابعة مرة واحدة

```text
Verified Printer Media = 80mm
    -> Use the document-specific 80mm template

Verified Printer Media = 58mm
    -> Use the document-specific 58mm template

Detailed A4 action explicitly selected by authorized user
    -> Use A4_DETAILED_FULL and an assigned A4 printer

Unknown / conflicting / unverified media
    -> HOLD PRINT JOB; ask administrator to configure/calibrate once
```

لا تُعد `UNKNOWN` أو `NOT_CONFIGURED` إذناً لاختيار مقاس افتراضي.

## 2. قواعد المحطة
- محطة مجهزة بطابعة 80mm: لا يجوز طباعة إيصال العميل بقالب 58mm أو A4.
- محطة مجهزة بطابعة 58mm: يُرسل قالب 58mm المستقل — لا قالب 80mm مصغراً ولا A4.

## 3. عند فشل طابعة الرول أو انقطاعها
1. تبقى عملية البيع/المستند محفوظة وفق قواعدها المالية.
2. توضع مهمة الطباعة في حالة فشل/انتظار واضحة (`FAILED` أو `BLOCKED_PRINTER_UNAVAILABLE` أو `UNKNOWN`).
3. لا تُرسل المهمة تلقائياً إلى طابعة A4 بديلة.
4. لا تنشئ نسخة بيع أو مستند مالي جديد.
5. تُعرض إعادة المحاولة/إعادة الطباعة للمستخدم المخوّل فقط.

## 4. تعيين الطابعة ↔ نوع المستند/الدور
لا يكفي تعيين طابعة افتراضية واحدة لكل النظام. التعيين بحسب المحطة والدور:
`Customer Receipt` · `Supplier/Purchase Documents` · `Finance Vouchers` · `Confidential Payroll` · `A4 Detailed Reports` · `Barcode Labels`.

يجوز اشتراك عدة أنواع مستند في طابعة واحدة إذا كان الملف يدعم الوسيط نفسه؛ ولا يجوز لمستند حساس استخدام طابعة مشتركة دون سياسة وصول واضحة (انظر PRINT_PRIVACY_RULES.md).

## 5. تغيير الطابعة أو الدرج أو التعريف
عند تغيير الطابعة أو برنامج التشغيل أو عرض الورق أو الدرج:
1. إيقاف التوجيه التلقائي للملف مؤقتاً.
2. إعادة قراءة القدرات أو إجراء معايرة.
3. تنفيذ Test Receipt موسوم بوضوح.
4. التحقق من المقاس والحروف العربية والقص.
5. اعتماد الملف من مسؤول مخوّل.
6. إعادة تشغيل Auto Routing.

لا يُغيَّر قالب مستند سابق بعد ترحيله؛ تُحفظ نسخة القالب المستخدمة وإصدار Printer Profile مع سجل المهمة.

## 6. الحالة الراهنة
كل الطابعات `NOT_CONFIGURED` إلى حين جرد الأجهزة الفعلية (قرار **D-15**). حتى اعتماده: التوجيه التلقائي موقوف (`HOLD`) لعدم وجود ملفات معايرة — لا تخمين لمقاس ولا بديل تلقائي.
