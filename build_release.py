#!/usr/bin/env python3
"""
build_release.py — Multi-platform release builder for OsuMapManager.

Produces self-contained single-file executables for every supported
platform × architecture combination. All output lands in ./releases/.

Usage:
    python build_release.py              # build all targets
    python build_release.py --version 1.2.0
    python build_release.py --only win-x64,linux-x64
    python build_release.py --dry-run     # print what would be built
"""

import argparse
import os
import shutil
import subprocess
import sys
from pathlib import Path


# ---------------------------------------------------------------------------
# Configuration
# ---------------------------------------------------------------------------

PROJECT_DIR = Path(__file__).resolve().parent
CSPROJ = PROJECT_DIR / "OsuMapManager" / "OsuMapManager.csproj"
VERSION_FILE = PROJECT_DIR / "VERSION"
RELEASES_DIR = PROJECT_DIR / "releases"

# All supported targets: (rid, platform_label, arch_label, exe_name)
TARGETS = [
    ("win-x64",   "windows", "amd64", "OsuMapManager.exe"),
    ("win-arm64", "windows", "arm64", "OsuMapManager.exe"),
    ("linux-x64",   "linux",   "amd64", "OsuMapManager"),
    ("linux-arm64", "linux",   "arm64", "OsuMapManager"),
    ("osx-x64",     "macOS",   "amd64", "OsuMapManager"),
    ("osx-arm64",   "macOS",   "arm64", "OsuMapManager"),
]


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def run(cmd: list[str], cwd: Path | None = None) -> None:
    """Run a command, streaming output; raise on failure."""
    print(f"\n  \033[36m{' '.join(cmd)}\033[0m")
    result = subprocess.run(cmd, cwd=cwd or PROJECT_DIR)
    if result.returncode != 0:
        sys.exit(result.returncode)


def get_version() -> str:
    """Read version from VERSION file, stripping whitespace."""
    if VERSION_FILE.exists():
        return VERSION_FILE.read_text(encoding="utf-8").strip()
    sys.exit("VERSION file not found. Create one or pass --version.")


# ---------------------------------------------------------------------------
# Build a single target
# ---------------------------------------------------------------------------

def build_target(rid: str, version: str, dry_run: bool = False) -> Path | None:
    """Publish for a single RID. Returns path to the publish output dir."""
    publish_dir = PROJECT_DIR / "OsuMapManager" / "bin" / "Release" / "net10.0" / rid / "publish"

    cmd = [
        "dotnet", "publish", str(CSPROJ),
        "-c", "Release",
        "-r", rid,
        "--self-contained", "true",
        "-p:Version=" + version,
        "-p:AssemblyVersion=" + version,
        "-p:FileVersion=" + version,
    ]

    if dry_run:
        print(f"  [DRY-RUN] Would publish {rid}")
        return publish_dir

    # Clean previous publish output for this RID
    if publish_dir.exists():
        shutil.rmtree(publish_dir)

    run(cmd, cwd=PROJECT_DIR)
    return publish_dir


def package_target(
    rid: str, platform_label: str, arch_label: str,
    exe_name: str, version: str, publish_dir: Path,
) -> Path:
    """Copy the single-file exe into releases/ with the naming convention."""
    exe_path = publish_dir / exe_name
    if not exe_path.exists():
        sys.exit(f"Expected executable not found: {exe_path}")

    # Name the executable directly: OsuMapManager-v0.1.0-windows-amd64.exe etc.
    ext = ".exe" if rid.startswith("win") else ""
    dest_name = f"OsuMapManager-v{version}-{platform_label}-{arch_label}{ext}"
    dest_path = RELEASES_DIR / dest_name
    shutil.copy2(exe_path, dest_path)

    # On non-Windows, make the binary executable
    if not rid.startswith("win"):
        os.chmod(dest_path, 0o755)

    return dest_path


# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

def main() -> None:
    parser = argparse.ArgumentParser(description="Build OsuMapManager releases")
    parser.add_argument("--version", help="Override version (default: read from VERSION file)")
    parser.add_argument(
        "--only",
        help="Comma-separated RIDs to build, e.g. win-x64,linux-x64",
    )
    parser.add_argument("--dry-run", action="store_true", help="Print build plan without executing")
    parser.add_argument("--skip-restore", action="store_true", help="Skip dotnet restore")
    args = parser.parse_args()

    version = args.version or get_version()
    print(f"\n{'='*60}")
    print(f"  OsuMapManager Release Builder — v{version}")
    print(f"{'='*60}")

    # Resolve targets
    selected_rids: set[str] | None = None
    if args.only:
        selected_rids = {r.strip() for r in args.only.split(",")}
        unknown = selected_rids - {t[0] for t in TARGETS}
        if unknown:
            sys.exit(f"Unknown RIDs: {unknown}. Valid: {[t[0] for t in TARGETS]}")

    targets = [t for t in TARGETS if selected_rids is None or t[0] in selected_rids]

    if args.dry_run:
        for rid, plat, arch, exe in targets:
            print(f"  → {rid:16s}  {plat}-{arch}  ({exe})")
        print(f"\n  {len(targets)} target(s) total.\n")
        return

    # Restore packages
    if not args.skip_restore:
        run(["dotnet", "restore", str(CSPROJ)])

    # Build & package each target
    RELEASES_DIR.mkdir(parents=True, exist_ok=True)
    results: list[Path] = []

    for rid, plat, arch, exe_name in targets:
        banner = f"  {rid} ({plat}-{arch})"
        print(f"\n{'─'*50}\n{banner}\n{'─'*50}")

        publish_dir = build_target(rid, version)
        if publish_dir is None:
            continue

        dest = package_target(rid, plat, arch, exe_name, version, publish_dir)
        results.append(dest)

    # Summary
    print(f"\n{'='*60}")
    print(f"  Done — {len(results)} release(s) built.")
    for r in results:
        size_mb = r.stat().st_size / (1024 * 1024)
        print(f"    {r.relative_to(PROJECT_DIR)}  ({size_mb:.0f} MB)")
    print(f"{'='*60}\n")


if __name__ == "__main__":
    main()
