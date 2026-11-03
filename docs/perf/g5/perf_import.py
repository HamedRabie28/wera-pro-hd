#!/usr/bin/env python3
"""G5 — اختبار الاستيراد الدفعي المرجعي (بيئة القياس الحالية) · WERA PRO HD.

يقيس على الخادم الحقيقي + العامل الخلفي الحقيقي:
  1) رفع دفعة صور متميزة عبر API (multipart على دفعات)
  2) المعالجة الخلفية حتى الاكتمال (حالة كل ملف من القاعدة §6.1)
  3) استجابة API أثناء المعالجة (دليل «لا تعطيل POS بسبب الصور» §6)
  4) ذاكرة عملية الخادم RSS أثناء الرفع والمعالجة (RAM Report)

كل صورة PNG حقيقية متميزة البايتات (بصمة SHA-256 مختلفة) — لا صور زائفة.
النتيجة تُطبع JSON إلى stdout وتُحفظ للتقرير.
"""
import argparse
import json
import statistics
import struct
import time
import urllib.request
import urllib.parse
import uuid
import zlib
import os
import sys

BASE = os.environ.get("PERF_BASE", "http://127.0.0.1:5188")
USERNAME = "perf.owner"
PASSWORD = "Perf#G5-Pass"


# ── مولد PNG حقيقي متميز (ترويسة IHDR + IDAT + IEND — يمر فحص البنية) ──
def make_png(seed: int, w: int = 24, h: int = 24) -> bytes:
    # إنتروبيا كاملة: القنوات الثلاث تحمل بتات متفرقة من البذرة — التمييز
    # صالح حتى 2^19 = 524,288 صورة متميزة (فوق 100k بهامش واسع)
    s0 = seed & 0xFF
    s1 = (seed >> 3) & 0xFF
    s2 = (seed >> 11) & 0xFF
    raw = bytearray()
    for y in range(h):
        raw.append(0)  # Filter: None
        for x in range(w):
            raw.append((x * 8 + s0) & 0xFF)
            raw.append((y * 5 + s1) & 0xFF)
            raw.append((x + y * 3 + s2) & 0xFF)
    compressed = zlib.compress(bytes(raw), 6)

    def chunk(kind: bytes, data: bytes) -> bytes:
        return (struct.pack(">I", len(data)) + kind + data
                + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF))

    ihdr = struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0)  # 8-bit RGB
    return (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", ihdr)
            + chunk(b"IDAT", compressed)
            + chunk(b"IEND", b""))


class Api:
    def __init__(self):
        self.token = None

    def _req(self, method, path, data=None, headers=None, timeout=120):
        url = BASE + path
        body = data
        hdrs = dict(headers or {})
        if self.token:
            hdrs["Authorization"] = "Bearer " + self.token
        req = urllib.request.Request(url, data=body, method=method, headers=hdrs)
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            return resp.status, resp.read(), dict(resp.headers)

    def login(self):
        payload = json.dumps({"username": USERNAME, "password": PASSWORD,
                              "branchId": None, "stationId": None}).encode()
        status, body, _ = self._req("POST", "/api/v1/auth/login", payload,
                                    {"Content-Type": "application/json"})
        assert status == 200, f"login failed: {status} {body[:200]}"
        self.token = json.loads(body)["accessToken"]

    def create_job(self, label):
        payload = json.dumps({"label": label}).encode()
        status, body, _ = self._req("POST", "/api/v1/media/import-jobs", payload,
                                    {"Content-Type": "application/json"})
        assert status == 201, f"create job: {status} {body[:200]}"
        return json.loads(body)["jobId"]

    def add_files(self, job_id, files):
        boundary = "----wera-perf-" + uuid.uuid4().hex
        parts = []
        for name, blob in files:
            parts.append(
                (f"--{boundary}\r\n"
                 f'Content-Disposition: form-data; name="files"; filename="{name}"\r\n'
                 f"Content-Type: application/octet-stream\r\n\r\n").encode()
                + blob + b"\r\n")
        parts.append(f"--{boundary}--\r\n".encode())
        body = b"".join(parts)
        status, resp, _ = self._req(
            "POST", f"/api/v1/media/import-jobs/{job_id}/files", body,
            {"Content-Type": f"multipart/form-data; boundary={boundary}"})
        assert status in (200, 201), f"add files: {status} {resp[:300]}"
        return json.loads(resp)

    def start(self, job_id):
        status, body, _ = self._req("POST", f"/api/v1/media/import-jobs/{job_id}/start", b"")
        assert status == 200, f"start: {status} {body[:200]}"

    def status(self, job_id):
        status, body, _ = self._req("GET", f"/api/v1/media/import-jobs/{job_id}")
        return json.loads(body)

    def report(self, job_id):
        status, body, _ = self._req("GET", f"/api/v1/media/import-jobs/{job_id}/report")
        return json.loads(body)

    def media_page(self):
        status, body, _ = self._req("GET", "/api/v1/media?pageSize=1")
        return status, json.loads(body)

    def health_ready(self):
        status, body, _ = self._req("GET", "/health/ready")
        return status


def rss_kb(pid: int) -> int:
    try:
        with open(f"/proc/{pid}/status", encoding="utf-8") as f:
            for line in f:
                if line.startswith("VmRSS:"):
                    return int(line.split()[1])
    except OSError:
        return -1
    return -1


def run_resume(api, a):
    """استئناف مهمة قائمة: العامل الخلفي يلتقطها تلقائياً (الحالة RUNNING في
    القاعدة — لا إعادة تشغيل)؛ هنا استقصاء + قياس حتى الحالة النهائية."""
    job = a.resume_job
    print(f"[perf] resuming job #{job} — polling until terminal state", flush=True)
    latency_during = []
    poll_durations = []
    rss_samples = [rss_kb(a.api_pid)]
    t_start = time.monotonic()
    while True:
        t0 = time.monotonic()
        s = api.status(job)
        poll_durations.append(time.monotonic() - t0)
        if s["state"] == "RUNNING":
            tl = time.monotonic()
            code, _ = api.media_page()
            latency_during.append((time.monotonic() - tl) * 1000.0)
            assert code == 200
        rss_samples.append(rss_kb(a.api_pid))
        if s["state"] in ("COMPLETED", "CANCELLED"):
            break
        if time.monotonic() - t_start > 580:
            raise TimeoutError("resume polling exceeded 580s")
        time.sleep(1.0)
    elapsed = time.monotonic() - t_start
    final = api.status(job)
    rep = api.report(job)
    counts = final["counts"]
    result = {
        "mode": "resume-after-crash",
        "resume_seconds": round(elapsed, 2),
        "api_latency_ms_during_resume": {
            "samples": len(latency_during),
            "p50": round(statistics.median(latency_during), 1) if latency_during else None,
            "max": round(max(latency_during), 1) if latency_during else None,
        },
        "server_rss_kb": {"peak": max(rss_samples), "after": rss_kb(a.api_pid)},
        "final_state": final["state"],
        "counts": counts,
        "errors_by_code": rep["errorsByCode"],
    }
    print("[perf] RESULT " + json.dumps(result, ensure_ascii=False), flush=True)
    if a.out:
        with open(a.out, "w", encoding="utf-8") as f:
            json.dump(result, f, ensure_ascii=False, indent=2)
    assert counts["pending"] == 0 and counts["processing"] == 0, f"unfinished: {counts}"
    assert counts["failed"] == 0, f"failures: {rep['errorsByCode']}"
    print("[perf] RESUME COMPLETE — job accounted in full", flush=True)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--count", type=int, default=10000)
    ap.add_argument("--batch", type=int, default=250)
    ap.add_argument("--api-pid", type=int, required=True)
    ap.add_argument("--out", default=None)
    ap.add_argument("--duplicate-of", type=int, default=None,
                                       help="استيراد بذور موجودة مسبقاً (اختبار التخطي المتعمد) — إزاحة البذور")
    ap.add_argument("--resume-job", type=int, default=None,
                                   help="استئناف مهمة قائمة (بعد انهيار/إلغاء): استقصاء وقياس فقط — لا رفع")
    a = ap.parse_args()

    api = Api()
    api.login()
    if a.resume_job is not None:
        run_resume(api, a)
        return
    mode = "مكرر متعمد" if a.duplicate_of is not None else "متميز"
    print(f"[perf] logged in — {a.count} images ({mode}) in batches of {a.batch}", flush=True)

    def seed_of(i: int) -> int:
        return i if a.duplicate_of is None else a.duplicate_of + i

    # 1) إنشاء المهمة (تُختَم ببصمة نطاق جذر الخادم)
    job = api.create_job(f"قياس مرجعي {a.count} صورة")
    print(f"[perf] job #{job} created", flush=True)

    # 2) الرفع على دفعات + قياس RSS أثناء الرفع
    upload_times = []
    rss_baseline = rss_kb(a.api_pid)
    rss_samples = [rss_baseline]
    t_upload_start = time.monotonic()
    generated = 0
    for batch_no in range(0, a.count, a.batch):
        files = []
        for i in range(batch_no, min(batch_no + a.batch, a.count)):
            files.append((f"perf-{seed_of(i):06d}.png", make_png(seed=seed_of(i))))
            generated += 1
        t0 = time.monotonic()
        api.add_files(job, files)
        upload_times.append(time.monotonic() - t0)
        rss_samples.append(rss_kb(a.api_pid))
    t_upload = time.monotonic() - t_upload_start
    print(f"[perf] uploaded {generated} files in {t_upload:.1f}s "
          f"(median batch {statistics.median(upload_times)*1000:.0f}ms)", flush=True)

    # 3) التشغيل + الاستقصاء مع أخذ عينات استجابة API وRSS أثناء المعالجة
    api.start(job)
    print("[perf] job started — background worker processing", flush=True)
    latency_during = []
    poll_durations = []
    t_proc_start = time.monotonic()
    last_done = -1
    while True:
        t0 = time.monotonic()
        s = api.status(job)
        poll_durations.append(time.monotonic() - t0)
        done = s["counts"]["done"] + s["counts"]["skipped"] + s["counts"]["failed"]
        # عينة استجابة API عادية أثناء المعالجة (proxy لعدم تعطيل المحطات)
        if s["state"] == "RUNNING":
            tl = time.monotonic()
            code, page = api.media_page()
            latency_during.append((time.monotonic() - tl) * 1000.0)
            assert code == 200, f"media page failed during import: {code}"
        rss_samples.append(rss_kb(a.api_pid))
        if s["state"] in ("COMPLETED", "CANCELLED"):
            break
        if done == last_done and s["state"] == "RUNNING" and len(poll_durations) > 30:
            print(f"[perf] WARNING: stalled at done={done}", flush=True)
        last_done = done
        time.sleep(1.0)
    t_process = time.monotonic() - t_proc_start

    final = api.status(job)
    rep = api.report(job)
    counts = final["counts"]

    result = {
        "images": a.count,
        "batch_size": a.batch,
        "upload_seconds": round(t_upload, 2),
        "upload_batches": len(upload_times),
        "upload_batch_median_ms": round(statistics.median(upload_times) * 1000, 1),
        "upload_throughput_files_per_sec": round(a.count / t_upload, 1),
        "process_seconds": round(t_process, 2),
        "process_throughput_files_per_sec": round(a.count / t_process, 1),
        "total_seconds": round(t_upload + t_process, 2),
        "api_latency_ms_during_import": {
            "samples": len(latency_during),
            "p50": round(statistics.median(latency_during), 1) if latency_during else None,
            "max": round(max(latency_during), 1) if latency_during else None,
        },
        "status_poll_ms_median": round(statistics.median(poll_durations) * 1000, 1),
        "server_rss_kb": {
            "baseline": rss_baseline,
            "peak": max(rss_samples),
            "after": rss_kb(a.api_pid),
        },
        "final_state": final["state"],
        "counts": counts,
        "errors_by_code": rep["errorsByCode"],
        "storage_scope": final["storageScope"],
    }
    print("[perf] RESULT " + json.dumps(result, ensure_ascii=False), flush=True)
    if a.out:
        with open(a.out, "w", encoding="utf-8") as f:
            json.dump(result, f, ensure_ascii=False, indent=2)

    # التحقق الحاسم: كل ملف محسوب — لا مفقود ولا مكرر غير محسوب
    assert counts["pending"] == 0 and counts["processing"] == 0, f"unfinished files: {counts}"
    assert counts["done"] + counts["skipped"] + counts["failed"] == a.count, \
        f"accounting mismatch: {counts}"
    assert counts["failed"] == 0, f"failures: {rep['errorsByCode']}"
    if a.duplicate_of is None:
        assert counts["skipped"] == 0, f"صور متميزة تخُطيت خطأً: {counts['skipped']}"
    else:
        assert counts["done"] == 0, f"مكررات دخلت المكتبة كأصول جديدة: {counts['done']}"
        assert counts["skipped"] == a.count, f"لم يُتخطَ كل المكرر: {counts}"
    print("[perf] ALL FILES ACCOUNTED", flush=True)


if __name__ == "__main__":
    main()
