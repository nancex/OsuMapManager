#!/usr/bin/env python3
"""
CatboyDataFetcher (Python)
===========================
Fetches all Ranked beatmap metadata from catboy.best API
and stores it in a local SQLite database.

Dependencies: pip install requests
(cloudscraper is optional - only needed if Cloudflare blocks you)

Usage:
    python fetch_catboy.py              # Test mode: offset 0..100
    python fetch_catboy.py --full       # Full fetch (all offsets)
    python fetch_catboy.py --help       # Show all options
"""

import json
import sqlite3
import sys
import time
import datetime
import os
from concurrent.futures import ThreadPoolExecutor, as_completed
from dataclasses import dataclass, field
from typing import Optional
import threading
from email.utils import parsedate_to_datetime

# ================================================================
# Configuration
# ================================================================

CATBOY_BASE = "https://catboy.best/api/v2/search"
STATUS = 1        # Ranked
MODE = 3          # Mania
PAGE_SIZE = 100
MAX_OFFSET = 10000  # Test cap; use --full for unlimited
THREAD_COUNT = 8
OUTPUT_DB = "catboy_ranked_mania.db"

# Rate-limit pause coordination (shared across all threads)
_rate_limit_event = threading.Event()
_rate_limit_event.set()  # initially not paused
_rate_limit_lock = threading.Lock()

# Browser-like headers to bypass Cloudflare WAF
HEADERS = {
    "User-Agent": (
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
        "AppleWebKit/537.36 (KHTML, like Gecko) "
        "Chrome/131.0.0.0 Safari/537.36"
    ),
    "Accept": "application/json",
    "Accept-Language": "en-US,en;q=0.9",
    "Accept-Encoding": "gzip, deflate, br",
}


# ================================================================
# In-memory records
# ================================================================

@dataclass
class BeatmapSetData:
    id: int = 0
    bpm: float = 0.0
    nsfw: bool = False
    tags: str = ""
    user_id: int = 0
    creator: str = ""
    genre_id: int = 0
    title: str = ""
    title_unicode: str = ""
    video: bool = False
    artist: str = ""
    artist_unicode: str = ""
    ranked: int = 0
    rating: float = 0.0
    source: str = ""
    language_id: int = 0
    offset: int = 0
    ranked_date: str = ""
    submitted_date: str = ""


@dataclass
class BeatmapData:
    id: int = 0
    beatmapset_id: int = 0
    ar: float = 0.0
    cs: float = 0.0
    bpm: float = 0.0
    mode: str = ""
    drain: float = 0.0
    user_id: int = 0
    ranked: int = 0
    status: str = ""
    version: str = ""
    accuracy: float = 0.0
    checksum: str = ""
    mode_int: int = 0
    difficulty_rating: float = 0.0


# ================================================================
# API Fetching
# ================================================================

def _safe_float(obj: dict, key: str) -> float:
    v = obj.get(key)
    return float(v) if v is not None else 0.0


def _safe_int(obj: dict, key: str) -> int:
    v = obj.get(key)
    return int(v) if v is not None else 0


def _safe_str(obj: dict, key: str) -> str:
    v = obj.get(key)
    return str(v) if v is not None else ""


def _safe_bool(obj: dict, key: str) -> bool:
    v = obj.get(key)
    return bool(v) if v is not None else False


def parse_beatmap_set(item: dict, api_offset: int) -> BeatmapSetData:
    return BeatmapSetData(
        id=_safe_int(item, "id"),
        bpm=_safe_float(item, "bpm"),
        nsfw=_safe_bool(item, "nsfw"),
        tags=_safe_str(item, "tags"),
        user_id=_safe_int(item, "user_id"),
        creator=_safe_str(item, "creator"),
        genre_id=_safe_int(item, "genre_id"),
        title=_safe_str(item, "title"),
        title_unicode=_safe_str(item, "title_unicode"),
        video=_safe_bool(item, "video"),
        artist=_safe_str(item, "artist"),
        artist_unicode=_safe_str(item, "artist_unicode"),
        ranked=_safe_int(item, "ranked"),
        rating=_safe_float(item, "rating"),
        source=_safe_str(item, "source"),
        language_id=_safe_int(item, "language_id"),
        offset=api_offset,
        ranked_date=_safe_str(item, "ranked_date"),
        submitted_date=_safe_str(item, "submitted_date"),
    )


def parse_beatmap(bm: dict, beatmapset_id: int) -> BeatmapData:
    return BeatmapData(
        id=_safe_int(bm, "id"),
        beatmapset_id=beatmapset_id,
        ar=_safe_float(bm, "ar"),
        cs=_safe_float(bm, "cs"),
        bpm=_safe_float(bm, "bpm"),
        mode=_safe_str(bm, "mode"),
        drain=_safe_float(bm, "drain"),
        user_id=_safe_int(bm, "user_id"),
        ranked=_safe_int(bm, "ranked"),
        status=_safe_str(bm, "status"),
        version=_safe_str(bm, "version"),
        accuracy=_safe_float(bm, "accuracy"),
        checksum=_safe_str(bm, "checksum"),
        mode_int=_safe_int(bm, "mode_int"),
        difficulty_rating=_safe_float(bm, "difficulty_rating"),
    )


def _parse_retry_after(response) -> float:
    """Parse Retry-After header. Returns seconds to wait (may be <= 0 if already past)."""
    retry_after = response.headers.get("Retry-After", "")
    if not retry_after:
        return 60.0
    try:
        return float(retry_after)
    except ValueError:
        pass
    try:
        retry_dt = parsedate_to_datetime(retry_after)
        delay = (retry_dt - datetime.datetime.now(datetime.timezone.utc)).total_seconds()
        return delay
    except Exception:
        pass
    return 60.0


def fetch_page(session, offset: int, limit: int):
    """Fetch a single page. Returns (sets_list, beatmaps_list, ok_bool)."""
    global _rate_limit_event, _rate_limit_lock

    url = f"{CATBOY_BASE}?status={STATUS}&limit={limit}&offset={offset}&mode={MODE}"
    try:
        # Wait if rate-limit pause is active
        _rate_limit_event.wait()

        resp = session.get(url, timeout=30)

        if resp.status_code == 429:
            if _rate_limit_lock.acquire(blocking=False):
                try:
                    _rate_limit_event.clear()
                    delay = _parse_retry_after(resp)
                    delay = max(0.0, min(delay, 300.0))
                    print(f"  [RATELIMIT] HTTP 429 at offset={offset}. Pausing for {delay:.1f}s...")
                    time.sleep(delay)
                    _rate_limit_event.set()
                    print(f"  [RATELIMIT] Pause over, resuming.")
                finally:
                    _rate_limit_lock.release()
            else:
                _rate_limit_event.wait()
            return [], [], False

        if resp.status_code != 200:
            print(f"  [API] offset={offset}: HTTP {resp.status_code}")
            return [], [], False

        data = resp.json()
        sets = []
        beatmaps = []
        for item in data:
            s = parse_beatmap_set(item, offset)
            sets.append(s)
            for bm in item.get("beatmaps", []):
                beatmaps.append(parse_beatmap(bm, s.id))

        return sets, beatmaps, True
    except Exception as e:
        print(f"  [API] offset={offset}: ERROR - {e}")
        return [], [], False


# ================================================================
# SQLite helpers
# ================================================================

CREATE_SETS_TABLE = """
CREATE TABLE IF NOT EXISTS beatmap_sets (
    id              INTEGER PRIMARY KEY,
    bpm             REAL,
    nsfw            INTEGER,
    tags            TEXT,
    user_id         INTEGER,
    creator         TEXT,
    genre_id        INTEGER,
    title           TEXT,
    title_unicode   TEXT,
    video           INTEGER,
    artist          TEXT,
    artist_unicode  TEXT,
    ranked          INTEGER,
    rating          REAL,
    source          TEXT,
    language_id     INTEGER,
    offset          INTEGER,
    ranked_date     TEXT,
    submitted_date  TEXT
)
"""

CREATE_BEATMAPS_TABLE = """
CREATE TABLE IF NOT EXISTS beatmaps (
    id               INTEGER PRIMARY KEY,
    beatmapset_id    INTEGER,
    ar               REAL,
    cs               REAL,
    bpm              REAL,
    mode             TEXT,
    drain            REAL,
    user_id          INTEGER,
    ranked           INTEGER,
    status           TEXT,
    version          TEXT,
    accuracy         REAL,
    checksum         TEXT,
    mode_int         INTEGER,
    difficulty_rating REAL
)
"""

CREATE_INDEX = "CREATE INDEX IF NOT EXISTS idx_beatmaps_set ON beatmaps(beatmapset_id)"

INSERT_SET = """
INSERT OR REPLACE INTO beatmap_sets
    (id, bpm, nsfw, tags, user_id, creator, genre_id, title,
     title_unicode, video, artist, artist_unicode, ranked, rating,
     source, language_id, offset, ranked_date, submitted_date)
VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
"""

INSERT_BEATMAP = """
INSERT OR REPLACE INTO beatmaps
    (id, beatmapset_id, ar, cs, bpm, mode, drain, user_id, ranked,
     status, version, accuracy, checksum, mode_int, difficulty_rating)
VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
"""


def write_to_sqlite(db_path: str, all_sets: list, all_beatmaps: list):
    """Write all parsed data to SQLite in a single transaction."""
    print(f"\n--- Writing to SQLite: {db_path} ---")
    conn = sqlite3.connect(db_path)
    conn.execute("PRAGMA journal_mode=WAL")
    conn.execute("PRAGMA synchronous=NORMAL")
    conn.execute("PRAGMA cache_size=-64000")  # 64 MB cache

    conn.execute(CREATE_SETS_TABLE)
    conn.execute(CREATE_BEATMAPS_TABLE)
    conn.execute(CREATE_INDEX)

    set_rows = [
        (s.id, s.bpm, int(s.nsfw), s.tags, s.user_id, s.creator,
         s.genre_id, s.title, s.title_unicode, int(s.video),
         s.artist, s.artist_unicode, s.ranked, s.rating,
         s.source, s.language_id, s.offset, s.ranked_date, s.submitted_date)
        for s in all_sets
    ]
    bm_rows = [
        (b.id, b.beatmapset_id, b.ar, b.cs, b.bpm, b.mode, b.drain,
         b.user_id, b.ranked, b.status, b.version, b.accuracy,
         b.checksum, b.mode_int, b.difficulty_rating)
        for b in all_beatmaps
    ]

    conn.executemany(INSERT_SET, set_rows)
    conn.executemany(INSERT_BEATMAP, bm_rows)
    conn.commit()

    size_mb = os.path.getsize(db_path) / (1024 * 1024)
    print(f"  Sets written:    {len(set_rows)}")
    print(f"  Beatmaps written: {len(bm_rows)}")
    print(f"  DB size:          {size_mb:.1f} MB")
    conn.close()


# ================================================================
# Main
# ================================================================

def main():
    import argparse

    parser = argparse.ArgumentParser(description="Catboy Beatmap Fetcher")
    parser.add_argument("--full", action="store_true",
                        help="Full fetch (no offset cap)")
    parser.add_argument("--max-offset", type=int, default=MAX_OFFSET,
                        help=f"Max offset (default: {MAX_OFFSET})")
    parser.add_argument("--page-size", type=int, default=PAGE_SIZE,
                        help=f"Items per page (default: {PAGE_SIZE})")
    parser.add_argument("--threads", type=int, default=THREAD_COUNT,
                        help=f"Concurrent threads (default: {THREAD_COUNT})")
    parser.add_argument("--output", type=str, default=OUTPUT_DB,
                        help=f"Output DB file (default: {OUTPUT_DB})")
    parser.add_argument("--cloudscraper", action="store_true",
                        help="Use cloudscraper to bypass Cloudflare")
    args = parser.parse_args()

    max_offset = args.max_offset
    if args.full:
        # Full fetch: start with a generous cap, auto-extend if needed
        max_offset = 100000

    offsets = list(range(0, max_offset + args.page_size, args.page_size))

    print("=" * 60)
    print("  CatboyDataFetcher (Python) - Beatmap DB Builder")
    print("=" * 60)
    print(f"  API:      {CATBOY_BASE}")
    print(f"  Status:   {STATUS} (Ranked)")
    print(f"  Mode:     {MODE} (Mania)")
    print(f"  Pages:    {len(offsets)}  (offset 0..{max_offset}, {args.page_size}/page)")
    print(f"  Threads:  {args.threads}")
    print(f"  Output:   {args.output}")
    print()

    # --- Choose HTTP backend ---
    if args.cloudscraper:
        try:
            import cloudscraper
            session = cloudscraper.create_scraper()
            print("[OK] Using cloudscraper (Cloudflare bypass)")
        except ImportError:
            print("[WARN] cloudscraper not installed. pip install cloudscraper")
            print("[WARN] Falling back to requests.")
            import requests
            session = requests.Session()
    else:
        import requests
        session = requests.Session()
    session.headers.update(HEADERS)

    # --- Phase 1: Fetch ---
    print("\n--- Phase 1: Fetching API data ---")
    t0 = time.time()
    all_sets = []
    all_beatmaps = []
    ok_pages = 0
    fail_pages = 0

    with ThreadPoolExecutor(max_workers=args.threads) as executor:
        futures = {
            executor.submit(fetch_page, session, off, args.page_size): off
            for off in offsets
        }
        for future in as_completed(futures):
            off = futures[future]
            sets, beatmaps, ok = future.result()
            if ok:
                all_sets.extend(sets)
                all_beatmaps.extend(beatmaps)
                ok_pages += 1
            else:
                fail_pages += 1
            print(f"  [Fetch] {ok_pages + fail_pages}/{len(offsets)} pages  "
                  f"| Sets: {len(all_sets)}  | Beatmaps: {len(all_beatmaps)}")

    t1 = time.time()
    print(f"\nFetch complete in {t1 - t0:.1f}s.")
    print(f"  Pages: {ok_pages} OK, {fail_pages} failed")
    print(f"  BeatmapSets:  {len(all_sets)}")
    print(f"  Beatmaps:     {len(all_beatmaps)}")

    if not all_sets:
        print("\n[ERROR] No data fetched.")
        print("If HTTP 403: try --cloudscraper flag (pip install cloudscraper)")
        print("If SSL error: your Python SSL stack may need updating.")
        return 1

    # --- Phase 2: Write ---
    t2 = time.time()
    write_to_sqlite(args.output, all_sets, all_beatmaps)
    t3 = time.time()

    print(f"\nDone!  Fetch: {t1 - t0:.1f}s  |  Write: {t3 - t2:.1f}s")
    return 0


if __name__ == "__main__":
    sys.exit(main())
