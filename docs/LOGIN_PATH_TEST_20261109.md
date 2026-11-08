# اختبار مسار الدخول (بوابة الزجاج الكامل) — 2026-11-09

المسار: probe → login(مركزي) → بالرمز: branches → auto(first) → stations → auto(first) → EnrichScope → me

- ✅ أقلاع API — health/ready = 200
- ✅ probe /health/ready (نبضة كل 30ث) — HTTP 200
- ✅ login بكلمة مرور خاطئة → 401 — 
- ✅ login owner (طور 1 — نطاق مركزي) — دور: OWNER
- ✅ جلسة كاملة access+refresh — 
- ✅ GET branches {items} بالرمز — 1 فرعاً
- ✅ auto-select first branch — #1 فرع الاختبار الحي
- ✅ GET stations {items} بالرمز — 1 محطة
- ✅ auto-select first station — #1 محطة الاختبار الحي
- ✅ GET /me — owner · صلاحيات: 1
- ✅ EnrichScope — سياق الجلسة — {"branchId": 1, "branchNameAr": "فرع الاختبار الحي", "stationId": 1, "stationNameAr": "محطة الاختبار الحي"}
- ✅ branches بدون رمز → 401 (لماذا الإثراء بعد الدخول) — المصادقة مطلوبة — يثبت أن التحميل قبل الدخول كان كوداً ميتاً
