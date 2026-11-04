#!/usr/bin/env python3
"""Download only the benchmark videos you are missing from the Kaggle dataset the notebook used.

The manifest's `relative_path` is the path inside the dataset (the notebook builds it with
path.relative_to(dataset_root)), so each video is fetched on its own with the Kaggle CLI instead of
downloading the whole dataset. Files land in --dest under the same relative path, so --dest (or its
parent) works as --video-root for eval/run_app_eval.py.

Videos that already exist under --dest or any --also-search folder (matched by name, symbols ignored,
like run_app_eval.py does) are skipped, and a re-run continues where an interrupted one stopped.

Needs the Kaggle CLI and an API token (see docs/DOWNLOAD-CALIBRATION.md):
    pip install kaggle

Example (preview first, then download):
    python tools/fetch_calibration_videos.py --manifest eval/manifest.csv --dest D:/ucf/ucf_train --also-search D:/ucf --dry-run
    python tools/fetch_calibration_videos.py --manifest eval/manifest.csv --dest D:/ucf/ucf_train --also-search D:/ucf
"""
import argparse
import csv
import re
import shutil
import subprocess
import sys
import tempfile
import time
import zipfile
from pathlib import Path

DATASET = "vigneshwar472/ucaucf-crime-annotation-dataset"
VIDEO_SUFFIXES = {".mp4", ".avi", ".mov", ".mkv"}
MIN_BYTES = 50_000          # anything smaller is an error page or a truncated download, not a video


def video_key(value):
    """Same key as the notebook and run_app_eval.py: lower-case stem without symbols."""
    return re.sub(r"[^a-z0-9]", "", Path(str(value)).stem.lower())


def index_videos(roots):
    found = {}
    for root in roots:
        root = Path(root)
        if root.is_dir():
            for p in root.rglob("*"):
                if p.suffix.lower() in VIDEO_SUFFIXES and p.is_file() and p.stat().st_size >= MIN_BYTES:
                    found.setdefault(video_key(p), p)
    return found


def read_wanted(manifest, splits, strata):
    with open(manifest, newline="", encoding="utf-8") as f:
        rows = list(csv.DictReader(f))
    need = {"clip_id", "relative_path", "evaluation_split"}
    if not rows or need - set(rows[0]):
        raise SystemExit(f"manifest needs the columns {sorted(need)}")
    wanted, seen = [], set()
    for r in rows:
        stratum = r.get("stratum") or ("person_optional" if r.get("class_name") in ("RoadAccidents", "Explosion", "Arson") else "main")
        if splits != ["all"] and r["evaluation_split"] not in splits:
            continue
        if strata != ["all"] and stratum not in strata:
            continue
        if r["clip_id"] in seen:
            continue
        seen.add(r["clip_id"])
        wanted.append(r)
    return wanted


def kaggle_command(override):
    if override:
        return override.split()
    exe = shutil.which("kaggle")
    if exe:
        return [exe]
    # pip put the module on the path but the Scripts folder is not on PATH (common on Windows)
    return [sys.executable, "-c", "from kaggle.cli import main; main()"]


def fetch_one(cmd, dataset, rel, target, attempts=3):
    """Download one dataset file to `target`. Returns (ok, message). Handles the CLI returning the file zipped."""
    last = ""
    for attempt in range(1, attempts + 1):
        with tempfile.TemporaryDirectory(dir=target.parent) as t:
            tmp = Path(t)
            p = subprocess.run(cmd + ["datasets", "download", "-d", dataset, "-f", rel, "-p", str(tmp), "--force"],
                               capture_output=True, text=True, encoding="utf-8", errors="replace")
            out = (p.stdout + p.stderr).strip()
            for z in list(tmp.glob("*.zip")):
                try:
                    with zipfile.ZipFile(z) as zf:
                        zf.extractall(tmp)
                    z.unlink()
                except zipfile.BadZipFile:
                    pass
            videos = [x for x in tmp.rglob("*") if x.is_file() and x.suffix.lower() in VIDEO_SUFFIXES]
            if p.returncode == 0 and videos and videos[0].stat().st_size >= MIN_BYTES:
                shutil.move(str(videos[0]), str(target))
                return True, ""
            last = out[-300:] or f"exit code {p.returncode}, no video file received"
        low = last.lower()
        if any(w in low for w in ("401", "403", "unauthorized", "forbidden", "could not find", "kaggle.json", "404", "not found")):
            break                                   # retrying will not fix credentials or a wrong path
        time.sleep(3 * attempt)
    return False, last


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--manifest", required=True)
    ap.add_argument("--dest", required=True, help="folder to put the downloaded videos in")
    ap.add_argument("--also-search", action="append", default=[],
                    help="folder that may already hold some of the videos (repeatable); those are not downloaded again")
    ap.add_argument("--split", default="calibration", help="comma list of evaluation_split values, or 'all' (default: calibration)")
    ap.add_argument("--strata", default="main", help="comma list: main,person_optional, or 'all' (default: main)")
    ap.add_argument("--dataset", default=DATASET)
    ap.add_argument("--dry-run", action="store_true", help="only list what would be downloaded")
    ap.add_argument("--kaggle-cmd", help="command that runs the Kaggle CLI (default: auto-detect; mainly for tests)")
    a = ap.parse_args()

    dest = Path(a.dest)
    wanted = read_wanted(a.manifest, a.split.split(","), a.strata.split(","))
    have = index_videos([dest, *a.also_search])
    todo = [r for r in wanted if video_key(r["relative_path"]) not in have and r["clip_id"] not in have]
    print(f"{len(wanted)} videos in the manifest for split={a.split} strata={a.strata}: "
          f"{len(wanted) - len(todo)} already on disk, {len(todo)} to download.")
    if a.dry_run or not todo:
        for r in todo:
            print("  would download:", r["relative_path"])
        return 0

    cmd = kaggle_command(a.kaggle_cmd)
    failed = []
    for i, r in enumerate(todo, 1):
        rel = r["relative_path"].strip().lstrip("/")
        target = dest / rel
        target.parent.mkdir(parents=True, exist_ok=True)
        print(f"[{i}/{len(todo)}] {rel} ...", end=" ", flush=True)
        ok, msg = fetch_one(cmd, a.dataset, rel, target)
        if ok:
            print(f"ok ({target.stat().st_size / 1e6:.1f} MB)")
        else:
            print("FAILED")
            print("    " + msg.replace("\n", "\n    "))
            failed.append(rel)
            if any(w in msg.lower() for w in ("401", "403", "unauthorized", "forbidden", "kaggle.json")):
                print("\nKaggle refused the request: check your API token (see docs/DOWNLOAD-CALIBRATION.md). Stopping.")
                break

    done = len(todo) - len(failed)
    print(f"\nDownloaded {done} of {len(todo)}.")
    if failed:
        print("Not downloaded:")
        for rel in failed:
            print("  ", rel)
        print("Run the same command again to retry only these.")
        return 1
    print(f"Done. Use --video-root {dest.parent if dest.parent != dest else dest} (or {dest}) with run_app_eval.py.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
