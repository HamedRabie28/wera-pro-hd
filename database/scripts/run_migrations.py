#!/usr/bin/env python3
"""WERA PRO HD — عدّاء الترحيلات (G1).

يطبق ملفات migrations/V###__*.sql بالترتيب ويسجل كل نسخة في core.schema_migrations
مع بصمة sha256. القواعد:
  - لا يطبق نسخة مسجلة سابقاً (Idempotent).
  - يرفض بصمة مختلفة لنفس النسخة (تعديل ترحيل مطبق ممنوع — Forward Fix فقط).
  - --target N يطبق حتى النسخة N حصراً (لمحاكاة الترقية من إصدار سابق).
  - --fresh يسقط المخططات التسعة والامتدادات أولاً (بيئة اختبار فقط).

الاستخدام:
  python3 run_migrations.py --db-uri "postgresql://..." [--target 10] [--fresh]
"""
import argparse
import hashlib
import re
import sys
from pathlib import Path

import psycopg2

SCHEMAS = ["audit", "catalog", "core", "finance", "inv", "ops", "print", "purch", "sales"]


def file_version(path: Path) -> int:
    m = re.match(r"V(\d+)__", path.name)
    return int(m.group(1)) if m else -1


def load_migrations(migrations_dir: Path):
    files = sorted(
        [p for p in migrations_dir.glob("V*__*.sql") if file_version(p) > 0],
        key=file_version,
    )
    if not files:
        sys.exit(f"no migration files found in {migrations_dir}")
    versions = [file_version(p) for p in files]
    if len(set(versions)) != len(versions):
        sys.exit("duplicate migration version numbers detected")
    return files


def run(db_uri: str, migrations_dir: Path, target: int | None, fresh: bool) -> None:
    files = load_migrations(migrations_dir)
    conn = psycopg2.connect(db_uri)
    conn.autocommit = True
    try:
        if fresh:  # بيئة اختبار فقط: إسقاط كل شيء ثم إنشاء من الصفر
            with conn.cursor() as cur:
                for s in SCHEMAS:
                    cur.execute(f"DROP SCHEMA IF EXISTS {s} CASCADE")
                cur.execute("DROP EXTENSION IF EXISTS pg_trgm")
                cur.execute("DROP SCHEMA IF EXISTS public CASCADE")
                cur.execute("CREATE SCHEMA public")

        applied = {}
        with conn.cursor() as cur:
            cur.execute(
                "SELECT EXISTS(SELECT 1 FROM information_schema.tables "
                "WHERE table_schema='core' AND table_name='schema_migrations')")
            if cur.fetchone()[0]:
                cur.execute("SELECT version, checksum FROM core.schema_migrations")
                applied = dict(cur.fetchall())

        print(f"{'version':>7}  {'file':48} {'status':10} note")
        print("-" * 102)
        for path in files:
            v = file_version(path)
            if target is not None and v > target:
                print(f"{v:>7}  {path.name:48} {'SKIPPED':10} (target={target})")
                continue
            sql = path.read_text(encoding="utf-8")
            checksum = hashlib.sha256(sql.encode("utf-8")).hexdigest()
            if v in applied:
                if applied[v] != checksum:
                    sys.exit(
                        f"FATAL: migration V{v} مسجل ببصمة مختلفة — تعديل ترحيل مطبق ممنوع؛"
                        " أنشئ ترحيلاً جديداً (سياسة Forward Fix)"
                    )
                print(f"{v:>7}  {path.name:48} {'ALREADY':10}")
                continue
            with conn.cursor() as cur:
                cur.execute(sql)
                cur.execute(
                    "INSERT INTO core.schema_migrations (version, name, checksum) VALUES (%s, %s, %s)",
                    (v, path.name, checksum),
                )
            print(f"{v:>7}  {path.name:48} {'APPLIED':10}")

        with conn.cursor() as cur:
            cur.execute("SELECT version, name FROM core.schema_migrations ORDER BY version")
            rows = cur.fetchall()
        print("-" * 102)
        print(f"head = V{rows[-1][0]} · {len(rows)} migrations مسجلة في core.schema_migrations")
    finally:
        conn.close()


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--db-uri", required=True, help="PostgreSQL connection URI")
    ap.add_argument("--migrations-dir", default=str(Path(__file__).resolve().parent.parent / "migrations"))
    ap.add_argument("--target", type=int, default=None, help="طبق حتى هذه النسخة حصراً")
    ap.add_argument("--fresh", action="store_true", help="اسقط المخططات أولاً (اختبار فقط)")
    a = ap.parse_args()
    run(a.db_uri, Path(a.migrations_dir), a.target, a.fresh)
