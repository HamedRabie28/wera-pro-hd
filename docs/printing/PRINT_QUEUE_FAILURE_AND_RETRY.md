# PRINT QUEUE — FAILURE AND RETRY
> **الوثيقة:** WERA-PR-007 · **الإصدار:** 1.0 · **التاريخ:** 2026-11-01 · **المرجع الناظم:** GFM-CSharp 2.4.1 §29.8 و§29.9

## 1. حالات مهمة الطباعة المعتمدة
`QUEUED` · `PROCESSING` · `SUBMITTED_TO_SPOOLER` · `COMPLETED_CONFIRMED` · `FAILED` · `UNKNOWN` · `HELD_CONFIGURATION_MISMATCH` · `BLOCKED_PRINTER_UNAVAILABLE` · `CANCELLED` · `REPRINT_REQUESTED`

## 2. القواعد الملزمة
1. فشل الطباعة لا يلغي البيع أو فاتورة المورد أو سند الراتب أو المصروف الذي تم ترحيله.
2. لا ينشأ مستند مالي جديد عند إعادة الطباعة.
3. كل Print Job فريد وله `IdempotencyKey`.
4. حالة `UNKNOWN` لا تعيد الطباعة تلقائياً إذا كان ذلك قد يطبع نسختين؛ استعلم من Windows Spooler/Adapter أولاً.
5. إعادة الطباعة الصريحة تنشئ Job جديداً مرتبطاً بالمستند الأصلي وتحمل علامة «نسخة إعادة طباعة».
6. إذا كان العرض الفعلي للطابعة غير مطابق، توضع المهمة في `HELD_CONFIGURATION_MISMATCH` ولا ترسل إلى وسيط آخر.
7. لا يتحول فشل 80mm إلى 58mm أو A4 تلقائياً، ولا العكس.
8. يحتفظ سجل الطباعة بالطابعة والمقاس والقالب والنسخة وسبب الفشل والمستخدم.

## 3. PrintJob مستقل لكل مستند
كل مهمة تحمل: `DocumentId`، `DocumentType`، `TemplateId` + `TemplateVersion`، `PrinterProfileId` + `PrinterProfileVersion`، `MediaClass` (58/80/A4 detailed)، `PrintableWidthMm` و`DPI`، `CopyType` (Customer/Admin/Reprint/Original/PaymentVoucher/Statement)، `RequestedBy`، `CreatedAt`، `CorrelationId`، `IdempotencyKey`، حالة المهمة والمحاولات.

لا تُجمع فاتورة مورد وسند دفع وكشف حساب في نفس المهمة. Batch Print الصريح = حزمة وظائف مستقلة أو فواصل/قصاصات واضحة مع فهرس يبين كل مستند.

## 4. سلوك إعادة التشغيل
- إعادة تشغيل التطبيق مع مهام معلقة: استعلم عن حالة كل مهمة قبل أي إعادة إرسال؛ لا تكرار مالي أو طباعي (اختبار 19).
- قبول Windows Spooler ليس تأكيداً لخروج الورقة مادياً إذا كان الجهاز/التعريف لا يوفر هذا التأكيد.

## 5. الفشل وفق وسيلة التكامل
- **Windows Driver Adapter:** تُقرأ الحالة الفعلية التي يوفرها التعريف فقط؛ لا تُخترع حالة.
- **ESC/POS (للموديلات المختبرة فقط):** تُطابق الاستجابة مع الأمر المرسل قبل اعتماد `COMPLETED_CONFIRMED`.
