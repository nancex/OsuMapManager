#!/usr/bin/env python3
"""
CatboyDataFetcher
=================
Fetches beatmap metadata from catboy.best API and stores it in local SQLite databases.

Dependencies: pip install requests

Usage:
    python fetch_catboy.py                           # ranked + mania (default)
    python fetch_catboy.py --status 4                # loved, all modes
    python fetch_catboy.py --status 1 --mode 2       # ranked + osu
    python fetch_catboy.py --all                     # ranked + loved, all modes, merged
    python fetch_catboy.py --help                    # show all options

API parameters:
    --status    -2 = graveyard   -1 = WIP   0 = pending
                 1 = ranked       3 = qualified   4 = loved
    --mode      1 = taiko         2 = osu     3 = mania
                Omit --mode to fetch all modes.
"""

import argparse
import os
import sqlite3
import sys
import time
import threading
from concurrent.futures import ThreadPoolExecutor, as_completed
from dataclasses import dataclass
from email.utils import parsedate_to_datetime
from typing import Optional


# ================================================================
# Configuration
# ================================================================

CATBOY_BASE = "https://catboy.best/api/v2/search"
PAGE_SIZE = 200
MAX_OFFSET = 80000       # test cap; use --full for unlimited
THREAD_COUNT = 8
OUTPUT_DIR = "./map_dbs"  # default output directory

# Rate-limit pause coordination
_rate_limit_event = threading.Event()
_rate_limit_event.set()
_rate_limit_lock = threading.Lock()

# End-of-data signal: set when a page returns [] to stop all threads
_end_of_data = threading.Event()

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
# API helpers
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
    retry_after = response.headers.get("Retry-After", "")
    if not retry_after:
        return 60.0
    try:
        return float(retry_after)
    except ValueError:
        pass
    try:
        import datetime as dt
        retry_dt = parsedate_to_datetime(retry_after)
        delay = (retry_dt - dt.datetime.now(dt.timezone.utc)).total_seconds()
        return delay
    except Exception:
        pass
    return 60.0


def build_url(offset: int, limit: int, status: int, mode: Optional[int]) -> str:
    """Build API URL. mode is optional - omit to fetch all modes."""
    url = f"{CATBOY_BASE}?status={status}&limit={limit}&offset={offset}"
    if mode is not None:
        url += f"&mode={mode}"
    return url


# ================================================================
# Fetch
# ================================================================

def fetch_page(session, offset: int, limit: int, status: int, mode: Optional[int]):
    """Fetch a single page. Returns (sets, beatmaps, ok, eod)."""
    global _rate_limit_event, _rate_limit_lock, _end_of_data

    # Stop immediately if another thread already hit end-of-data
    if _end_of_data.is_set():
        return [], [], False, True

    url = build_url(offset, limit, status, mode)
    try:
        _rate_limit_event.wait()
        resp = session.get(url, timeout=30)

        if resp.status_code == 429:
            if _rate_limit_lock.acquire(blocking=False):
                try:
                    _rate_limit_event.clear()
                    delay = _parse_retry_after(resp)
                    delay = max(0.0, min(delay, 300.0))
                    print(f"  [RATELIMIT] HTTP 429 at offset={offset}. Pausing {delay:.1f}s...")
                    time.sleep(delay)
                    _rate_limit_event.set()
                    print(f"  [RATELIMIT] Resuming.")
                finally:
                    _rate_limit_lock.release()
            else:
                _rate_limit_event.wait()
            return [], [], False, False

        if resp.status_code != 200:
            print(f"  [API] offset={offset}: HTTP {resp.status_code}")
            return [], [], False, False

        data = resp.json()

        # Empty list [] = end of data reached - signal all threads to stop
        if not data:
            print(f"  [EOD] offset={offset}: empty page, reached end.")
            _end_of_data.set()
            return [], [], True, True

        sets = []
        beatmaps = []
        for item in data:
            s = parse_beatmap_set(item, offset)
            sets.append(s)
            for bm in item.get("beatmaps", []):
                beatmaps.append(parse_beatmap(bm, s.id))
        return sets, beatmaps, True, False
    except Exception as e:
        print(f"  [API] offset={offset}: ERROR - {e}")
        return [], [], False, False


def fetch_all(session, status: int, mode: Optional[int],
              page_size: int, max_offset: int, threads: int):
    """Fetch all pages for a given (status, mode) combo. Stops early if end-of-data reached."""
    global _end_of_data
    _end_of_data.clear()

    offsets = list(range(0, max_offset + page_size, page_size))
    status_label = _status_label(status)
    mode_label = f"mode={mode}" if mode is not None else "all modes"
    print(f"\n  Fetching status={status} ({status_label}), {mode_label}")
    print(f"  Pages: {len(offsets)}, Threads: {threads}")

    all_sets: list = []
    all_beatmaps: list = []
    ok_pages = 0
    fail_pages = 0

    with ThreadPoolExecutor(max_workers=threads) as executor:
        futures = {
            executor.submit(fetch_page, session, off, page_size, status, mode): off
            for off in offsets
        }
        for future in as_completed(futures):
            sets, beatmaps, ok, eod = future.result()
            if ok:
                all_sets.extend(sets)
                all_beatmaps.extend(beatmaps)
                ok_pages += 1
            else:
                fail_pages += 1
            print(f"  [{ok_pages + fail_pages}/{len(offsets)}] "
                  f"Sets: {len(all_sets)}  Beatmaps: {len(all_beatmaps)}")

            if eod:
                # Cancel all remaining pending futures
                for f in futures:
                    f.cancel()
                print(f"  [EOD] Stopped early at offset ~{offsets[ok_pages + fail_pages - 1]}.")
                break

    print(f"  Done: {ok_pages} OK, {fail_pages} failed  |  "
          f"Sets: {len(all_sets)}  Beatmaps: {len(all_beatmaps)}")
    return all_sets, all_beatmaps


def _status_label(status: int) -> str:
    return {-2: "graveyard", -1: "WIP", 0: "pending",
            1: "ranked", 3: "qualified", 4: "loved"}.get(status, str(status))


# ================================================================
# SQLite
# ================================================================

CREATE_SETS = """
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

CREATE_BEATMAPS = """
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
    os.makedirs(os.path.dirname(db_path) or ".", exist_ok=True)
    print(f"\n  Writing {db_path} ...")
    conn = sqlite3.connect(db_path)
    conn.execute("PRAGMA journal_mode=WAL")
    conn.execute("PRAGMA synchronous=NORMAL")
    conn.execute("PRAGMA cache_size=-64000")
    conn.execute(CREATE_SETS)
    conn.execute(CREATE_BEATMAPS)
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
    print(f"  Sets: {len(set_rows)}  Beatmaps: {len(bm_rows)}  Size: {size_mb:.1f} MB")
    conn.close()


def merge_databases(src_paths: list[str], dest_path: str):
    """Merge multiple SQLite DBs into one by copying all rows."""
    os.makedirs(os.path.dirname(dest_path) or ".", exist_ok=True)
    print(f"\n  Merging into {dest_path} ...")

    dest = sqlite3.connect(dest_path)
    dest.execute("PRAGMA journal_mode=WAL")
    dest.execute("PRAGMA synchronous=NORMAL")
    dest.execute("PRAGMA cache_size=-64000")
    dest.execute(CREATE_SETS)
    dest.execute(CREATE_BEATMAPS)
    dest.execute(CREATE_INDEX)

    total_sets = 0
    total_bm = 0

    for src_path in src_paths:
        if not os.path.exists(src_path):
            print(f"  [SKIP] {src_path} not found")
            continue
        src = sqlite3.connect(src_path)

        set_rows = list(src.execute("SELECT * FROM beatmap_sets"))
        if set_rows:
            cols = ",".join("?" * len(set_rows[0]))
            dest.executemany(f"INSERT OR REPLACE INTO beatmap_sets VALUES ({cols})", set_rows)
            total_sets += len(set_rows)

        bm_rows = list(src.execute("SELECT * FROM beatmaps"))
        if bm_rows:
            cols = ",".join("?" * len(bm_rows[0]))
            dest.executemany(f"INSERT OR REPLACE INTO beatmaps VALUES ({cols})", bm_rows)
            total_bm += len(bm_rows)

        src.close()
        print(f"  + {os.path.basename(src_path)}: {len(set_rows)} sets, {len(bm_rows)} beatmaps")

    dest.commit()
    size_mb = os.path.getsize(dest_path) / (1024 * 1024)
    print(f"  Total: {total_sets} sets, {total_bm} beatmaps  |  Size: {size_mb:.1f} MB")
    dest.close()


# ================================================================
# Main
# ================================================================

def main():
    parser = argparse.ArgumentParser(
        description="Catboy Beatmap Fetcher - build local beatmap databases from catboy.best",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
API parameters:
  --status    -2 = graveyard   -1 = WIP   0 = pending
               1 = ranked       3 = qualified   4 = loved
  --mode      1 = taiko         2 = osu     3 = mania
              Omit --mode to fetch all modes.

Examples:
  python fetch_catboy.py                           # ranked + mania (default)
  python fetch_catboy.py --status 4                # loved, all modes
  python fetch_catboy.py --status 1 --mode 2       # ranked + osu
  python fetch_catboy.py --all --full              # full fetch: ranked + loved
        """,
    )
    parser.add_argument("--all", action="store_true",
                        help="Fetch ranked and loved (all modes), then merge into catboy_all.db. "
                             "Ignores --status, --mode, and --output.")
    parser.add_argument("--status", type=int, default=1,
                        help="Beatmap status: -2=graveyard, -1=WIP, 0=pending, 1=ranked (default), 3=qualified, 4=loved")
    parser.add_argument("--mode", type=int, default=3,
                        help="Game mode: 1=taiko, 2=osu, 3=mania (default). Omit for all modes.")
    parser.add_argument("--full", action="store_true",
                        help="Full fetch (no offset cap)")
    parser.add_argument("--max-offset", type=int, default=MAX_OFFSET,
                        help=f"Max offset (default: {MAX_OFFSET})")
    parser.add_argument("--page-size", type=int, default=PAGE_SIZE,
                        help=f"Items per page (default: {PAGE_SIZE})")
    parser.add_argument("--threads", type=int, default=THREAD_COUNT,
                        help=f"Concurrent threads (default: {THREAD_COUNT})")
    parser.add_argument("--output", type=str, default=None,
                        help=f"Output DB path (default: {OUTPUT_DIR}/catboy_<status>_<mode>.db)")
    parser.add_argument("--output-dir", type=str, default=OUTPUT_DIR,
                        help=f"Output directory (default: {OUTPUT_DIR})")
    parser.add_argument("--cloudscraper", action="store_true",
                        help="Use cloudscraper to bypass Cloudflare")
    args = parser.parse_args()

    max_offset = args.max_offset
    if args.full:
        max_offset = 100000

    # --- HTTP session ---
    if args.cloudscraper:
        try:
            import cloudscraper
            session = cloudscraper.create_scraper()
            print("[OK] Using cloudscraper")
        except ImportError:
            print("[WARN] cloudscraper not installed. Falling back to requests.")
            import requests
            session = requests.Session()
    else:
        import requests
        session = requests.Session()
    session.headers.update(HEADERS)

    print("=" * 60)
    print("  CatboyDataFetcher - Beatmap DB Builder")
    print("=" * 60)
    print(f"  API:       {CATBOY_BASE}")
    print(f"  Pages:     ~{max_offset // args.page_size}  ({args.page_size}/page, max offset={max_offset})")
    print(f"  Threads:   {args.threads}")
    print(f"  Output:    {args.output_dir}/")

    # --- --all mode ---
    if args.all:
        print(f"\n  Mode:      --all (ranked + loved, all modes)")
        t0 = time.time()

        targets = [
            (1, None, f"{args.output_dir}/catboy_ranked.db"),
            (4, None, f"{args.output_dir}/catboy_loved.db"),
        ]

        for status, mode, out_path in targets:
            sets, beatmaps = fetch_all(session, status, mode,
                                       args.page_size, max_offset, args.threads)
            if not sets:
                print(f"  [ERROR] No data for status={status}")
                return 1
            write_to_sqlite(out_path, sets, beatmaps)

        merge_databases(
            [t[2] for t in targets],
            f"{args.output_dir}/catboy_all.db",
        )

        print(f"\nDone!  Total time: {time.time() - t0:.0f}s")
        return 0

    # --- Single fetch mode ---
    status = args.status
    mode = args.mode

    if args.output:
        out_path = args.output
    else:
        mode_tag = f"_mode{mode}" if mode is not None else ""
        out_path = f"{args.output_dir}/catboy_{_status_label(status)}{mode_tag}.db"

    print(f"  Status:    {status} ({_status_label(status)})")
    print(f"  Mode:      {mode if mode is not None else 'all'}")
    print()

    t0 = time.time()
    sets, beatmaps = fetch_all(session, status, mode,
                               args.page_size, max_offset, args.threads)

    if not sets:
        print("\n[ERROR] No data fetched.")
        print("If HTTP 403: try --cloudscraper (pip install cloudscraper)")
        print("If SSL error: your Python SSL stack may need updating.")
        return 1

    write_to_sqlite(out_path, sets, beatmaps)
    print(f"\nDone!  Time: {time.time() - t0:.0f}s")
    return 0


if __name__ == "__main__":
    sys.exit(main())
