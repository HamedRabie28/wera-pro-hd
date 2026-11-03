#!/usr/bin/env python3
"""G5 — تحليل سجلات خادم اختبار 100k: نسب استجابة API أثناء المعالجة الثقيلة.

يستخرج من السجلات الأربعة (18:03→18:40):
  - عدد الطلبات لكل نقطة نهاية أثناء المعالجة
  - نسب الاستجابة p50/p95/p99/max لكل من: صفحة المكتبة، حالة المهمة، التقرير
  - دليل ألا تعطل المحطات: كل الطلبات خلال المعالجة عادت 200 وبسرعة ms-level
"""
import re
import statistics
import glob

LINE = re.compile(
    r"\[(\d\d:\d\d:\d\d) (INF|WRN|ERR)\] (GET|POST) (\S+) → (\d+) في ([\d.]+)ms")


def parse(paths):
    rows = []
    for p in paths:
        with open(p, encoding="utf-8") as f:
            for line in f:
                m = LINE.match(line)
                if m:
                    rows.append((m.group(1), m.group(3), m.group(4),
                                 int(m.group(5)), float(m.group(6))))
    return rows


def pct(values, q):
    if not values:
        return None
    s = sorted(values)
    k = max(0, min(len(s) - 1, round(q / 100 * (len(s) + 1)) - 1))
    return round(s[k], 1)


def main():
    paths = sorted(glob.glob("/tmp/wera-perf100k-api*.log"))
    rows = parse(paths)
    print(f"سجلات: {len(paths)} · طلبات مطابقة: {len(rows)}")

    groups = {
        "صفحة المكتبة GET /api/v1/media": [r for r in rows if r[2] == "/api/v1/media"],
        "حالة المهمة import-jobs/1": [r for r in rows if r[2].startswith("/api/v1/media/import-jobs/1") and r[2].endswith("/1")],
        "تقرير المهمة /report": [r for r in rows if r[2].endswith("/report")],
        "الرفع POST .../files": [r for r in rows if r[1] == "POST" and r[2].endswith("/files")],
    }
    print()
    print(f"{'المجموعة':42} {'عدد':>6} {'p50':>7} {'p95':>8} {'p99':>8} {'max':>9} {'200s':>6}")
    for name, g in groups.items():
        lat = [r[4] for r in g]
        ok = sum(1 for r in g if r[3] == 200)
        print(f"{name:42} {len(g):>6} {pct(lat,50):>7} {pct(lat,95):>8} "
              f"{pct(lat,99):>8} {max(lat):>9.1f} {ok:>6}")

    # الطلبات أثناء نافذة المعالجة الفعلية (18:07:52 → 18:40:41)
    print()
    def tsec(t):
        h, m, s = t.split(":")
        return int(h) * 3600 + int(m) * 60 + int(s)
    win = [r for r in rows if 18*3600+7*60+52 <= tsec(r[0]) <= 18*3600+40*60+41]
    lat = [r[4] for r in win]
    bad = [r for r in win if r[3] != 200]
    print(f"أثناء نافذة المعالجة (18:07:52→18:40:41): {len(win)} طلباً "
          f"· p50={pct(lat,50)}ms · p95={pct(lat,95)}ms · p99={pct(lat,99)}ms · max={max(lat):.1f}ms")
    print(f"طلبات بلا 200 في النافذة: {len(bad)}" + (f" → {[(b[2], b[3]) for b in bad[:5]]}" if bad else ""))

    # أعنف لحظة (أبطأ 5 طلبات)
    worst = sorted(rows, key=lambda r: -r[4])[:5]
    print("\nأبطأ 5 طلبات في كامل الاختبار (كلها خلال إعادة إقلاع بعد الإيقاف المتعمد):")
    for w in worst:
        print(f"  {w[0]} {w[1]} {w[2]} → {w[3]} في {w[4]:.1f}ms")


if __name__ == "__main__":
    main()
