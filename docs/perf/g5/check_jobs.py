#!/usr/bin/env python3
"""فحص حالة مهام استيراد الوسائط في قواعد الأداء (G5)."""
import psycopg2

for db in ['wera_perf100k', 'wera_perf_g5']:
    conn = psycopg2.connect(f'postgresql://postgres@localhost:55432/{db}')
    cur = conn.cursor()
    cur.execute("""
        SELECT j.job_id, j.label, j.state, j.created_at, j.started_at, j.finished_at,
               (SELECT count(*) FROM catalog.media_import_files f WHERE f.job_id = j.job_id),
               (SELECT count(*) FROM catalog.media_import_files f WHERE f.job_id = j.job_id AND f.state = 'DONE'),
               (SELECT count(*) FROM catalog.media_import_files f WHERE f.job_id = j.job_id AND f.state = 'SKIPPED'),
               (SELECT count(*) FROM catalog.media_import_files f WHERE f.job_id = j.job_id AND f.state = 'FAILED'),
               (SELECT count(*) FROM catalog.media_import_files f WHERE f.job_id = j.job_id AND f.state = 'PENDING'),
               (SELECT count(*) FROM catalog.media_import_files f WHERE f.job_id = j.job_id AND f.state = 'PROCESSING')
        FROM catalog.media_import_jobs j ORDER BY j.job_id""")
    rows = cur.fetchall()
    print(f'=== {db} ===')
    for r in rows:
        print(f'job#{r[0]} [{r[2]}] "{r[1][:40]}"')
        print(f'   files={r[6]} done={r[7]} skipped={r[8]} failed={r[9]} pending={r[10]} processing={r[11]}')
        print(f'   created={r[3]} started={r[4]} finished={r[5]}')
    cur.execute("SELECT count(*), count(distinct sha256) FROM catalog.media_assets")
    total, distinct = cur.fetchone()
    print(f'media_assets: total={total} distinct_sha={distinct}')
    cur.execute("SELECT count(*) FROM catalog.media_assets WHERE thumb_catalog_key IS NOT NULL")
    print(f'with thumb-catalog: {cur.fetchone()[0]}')
    conn.close()
