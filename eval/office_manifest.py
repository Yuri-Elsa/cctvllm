#!/usr/bin/env python3
"""Build an eval manifest from real office CCTV clips, so the existing eval scripts work on them.

The UCF-Crime manifest (eval/manifest.csv) cannot say anything about office cameras. This script turns a
folder of office clips plus a label sheet into a manifest with the same columns, which then works unchanged with
run_app_eval.py, analyze_app_eval.py, threshold_sweep.py and person_detection_eval.py (sample).

Steps (full runbook: docs/OFFICE-ROLLOUT.md):

  1. python eval/office_manifest.py --video-root D:/kantor --write-template eval/office/labels.csv
     -> one row per clip; fill `expected_suspicious` (1 = something that should raise an alert, 0 = normal).
        Label from what people saw, not from what the app answered.
  2. python eval/office_manifest.py --video-root D:/kantor --labels eval/office/labels.csv --out eval/office/manifest.csv
     -> manifest with a stratified calibration/test split (frozen by --seed; do not re-split after looking at results).

Shortcut: if reviewers already pressed Confirmed / False alarm / Missed event / Normal in the app, use the app's
CSV export instead of typing labels again:

     python eval/office_manifest.py --from-export cctv-jobs-20261201.csv --write-template eval/office/labels.csv
     -> label sheet filled from the verdicts (confirmed, missed -> 1; false_alarm, normal -> 0). Then run step 2 with
        --video-root pointing at the folder that holds those clips (matched by file name).
     Caveat: reviewers saw the model's flag when they judged, which biases the labels toward it. For a clean
     evaluation, have a second person relabel without seeing the flag, or at least a random sample of the clips.

Columns written match eval/manifest.csv. class_name is "Event" or "Normal", reference_text is empty.
ffmpeg is used to read each clip's duration (CCTV_FFMPEG, otherwise ffmpeg on the PATH).
"""
import argparse
import csv
import hashlib
import os
import random
import re
import subprocess
import sys
from pathlib import Path

VIDEO_EXT = {".mp4", ".avi", ".mkv", ".mov", ".webm", ".m4v", ".ts"}
FIELDS = ["clip_id", "relative_path", "source_video_id", "class_name", "start_sec", "end_sec", "reference_text",
          "source_split", "eligible", "exclusion_reason", "annotation_sources", "evaluation_split", "person_optional",
          "stratum", "expected_suspicious", "video_sha256"]
# The Calibration page warns below these counts; the same floor applies here.
MIN_EVENTS, MIN_NORMAL = 10, 30


def clip_id(rel):
    return re.sub(r"[^a-z0-9]+", "", Path(rel).with_suffix("").as_posix().lower()) or "clip"


def duration(path):
    ffmpeg = os.environ.get("CCTV_FFMPEG") or "ffmpeg"
    try:
        err = subprocess.run([ffmpeg, "-hide_banner", "-i", str(path)], capture_output=True, text=True, timeout=60).stderr
    except (OSError, subprocess.SubprocessError):
        return None
    m = re.search(r"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)", err)
    return int(m[1]) * 3600 + int(m[2]) * 60 + float(m[3]) if m else None


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def to_label(value):
    v = (value or "").strip().lower()
    if v in ("1", "yes", "y", "true", "event", "ya"):
        return True
    if v in ("0", "no", "n", "false", "normal", "tidak"):
        return False
    return None


def find_videos(root):
    return sorted(p for p in root.rglob("*") if p.is_file() and p.suffix.lower() in VIDEO_EXT)


def write_template(root, out):
    vids = find_videos(root)
    out.parent.mkdir(parents=True, exist_ok=True)
    existing = {}
    if out.exists():                      # never lose labels already typed in
        with open(out, newline="", encoding="utf-8") as f:
            existing = {r["relative_path"]: r for r in csv.DictReader(f)}
    with open(out, "w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, ["relative_path", "expected_suspicious", "person_optional", "note"])
        w.writeheader()
        for v in vids:
            rel = v.relative_to(root).as_posix()
            old = existing.get(rel, {})
            w.writerow({"relative_path": rel, "expected_suspicious": old.get("expected_suspicious", ""),
                        "person_optional": old.get("person_optional", ""), "note": old.get("note", "")})
    print(f"{len(vids)} clips listed in {out}. Fill expected_suspicious (1 or 0) for each.")


REVIEW_LABEL = {"confirmed": "1", "missed": "1", "false_alarm": "0", "normal": "0"}


def labels_from_export(export_csv, out):
    """Turn the app's CSV export (Export button / GET /api/export?format=csv) into a label sheet."""
    with open(export_csv, newline="", encoding="utf-8-sig") as f:
        rows = list(csv.DictReader(f))
    if not rows or "review" not in rows[0] or "file" not in rows[0]:
        sys.exit("This does not look like the app's CSV export (needs the columns `file` and `review`).")
    kept, seen, dup = [], {}, 0
    for r in rows:
        label = REVIEW_LABEL.get((r.get("review") or "").strip())
        if label is None or r.get("status") != "completed":
            continue
        name = r["file"]
        if name in seen:                       # same file name twice: names are the only link to the clips, so skip both
            dup += 1
            seen[name] = None
            continue
        seen[name] = len(kept)
        kept.append({"relative_path": name, "expected_suspicious": label, "person_optional": "",
                     "note": f"app id {r.get('id', '')}; review={r['review']}; model flagged={r.get('flagged', '')}"})
    kept = [k for k in kept if seen[k["relative_path"]] is not None]
    out.parent.mkdir(parents=True, exist_ok=True)
    with open(out, "w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, ["relative_path", "expected_suspicious", "person_optional", "note"])
        w.writeheader()
        w.writerows(kept)
    events = sum(1 for k in kept if k["expected_suspicious"] == "1")
    print(f"{len(kept)} reviewed videos written to {out} (events {events}, normal {len(kept) - events}); "
          f"{len(rows) - len(kept)} export rows skipped (not reviewed, not completed, or duplicate file name).")
    if dup:
        print(f"  {dup} duplicate file name(s) dropped: rename the clips so every name is unique.", file=sys.stderr)


def build(root, labels_path, out, calib_frac, seed):
    with open(labels_path, newline="", encoding="utf-8") as f:
        sheet = list(csv.DictReader(f))
    rows, skipped = [], []
    by_name = {}                              # file name -> paths, for labels that only carry a name (from the app export)
    for v in find_videos(root):
        by_name.setdefault(v.name.lower(), []).append(v)
    for r in sheet:
        rel = (r.get("relative_path") or "").replace("\\", "/").strip()
        label = to_label(r.get("expected_suspicious"))
        path = root / rel
        if not path.is_file():
            hits = by_name.get(Path(rel).name.lower(), [])
            if len(hits) == 1:
                path = hits[0]
                rel = path.relative_to(root).as_posix()
            elif len(hits) > 1:
                skipped.append((rel, f"{len(hits)} files with this name under {root}"))
                continue
        if label is None:
            skipped.append((rel, "no label"))
        elif not path.is_file():
            skipped.append((rel, "file not found"))
        else:
            dur = duration(path)
            if dur is None:
                skipped.append((rel, "ffmpeg could not read it"))
            else:
                rows.append((rel, path, label, dur, bool(to_label(r.get("person_optional")))))
    if not rows:
        sys.exit("No clip could be matched to a label; nothing written. Check --video-root and the file names in the label sheet.\n"
                 + "\n".join(f"  skipped: {rel} ({why})" for rel, why in skipped[:10]))
    ids = [clip_id(r[0]) for r in rows]
    if len(set(ids)) != len(ids):
        sys.exit("Two clips map to the same clip_id; rename one of them.")

    # stratified split: events and normals are divided separately, so both splits have both kinds
    rng = random.Random(seed)
    split = {}
    for want in (True, False):
        group = sorted((r for r in rows if r[2] is want), key=lambda r: r[0])
        rng.shuffle(group)
        cut = round(len(group) * calib_frac)
        for i, r in enumerate(group):
            split[r[0]] = "calibration" if i < cut else "test"

    out.parent.mkdir(parents=True, exist_ok=True)
    with open(out, "w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, FIELDS)
        w.writeheader()
        for rel, path, label, dur, optional in sorted(rows):
            cid = clip_id(rel)
            w.writerow({"clip_id": cid, "relative_path": rel, "source_video_id": cid,
                        "class_name": "Event" if label else "Normal", "start_sec": 0.0, "end_sec": round(dur, 3),
                        "reference_text": "", "source_split": "office", "eligible": True, "exclusion_reason": "",
                        "annotation_sources": '["office_labels"]', "evaluation_split": split[rel],
                        "person_optional": optional, "stratum": "person_optional" if optional else "main",
                        "expected_suspicious": label, "video_sha256": sha256(path)})

    events = sum(1 for r in rows if r[2])
    normal = len(rows) - events
    print(f"{len(rows)} clips written to {out}  (events {events}, normal {normal})")
    for s in ("calibration", "test"):
        e = sum(1 for r in rows if split[r[0]] == s and r[2])
        n = sum(1 for r in rows if split[r[0]] == s and not r[2])
        print(f"  {s:12s} events {e:3d}  normal {n:3d}")
    for rel, why in skipped:
        print(f"  skipped: {rel} ({why})", file=sys.stderr)
    if events < MIN_EVENTS or normal < MIN_NORMAL:
        print(f"WARNING: fewer than {MIN_EVENTS} events or {MIN_NORMAL} normal clips. Recall/precision intervals will be "
              "wide; report results with counts (\"8 of 10\") and treat the threshold as provisional.", file=sys.stderr)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--video-root", help="folder with the office clips (subfolders allowed)")
    ap.add_argument("--from-export", metavar="CSV", help="the app's CSV export; with --write-template, build the label sheet from its review verdicts")
    ap.add_argument("--write-template", metavar="CSV", help="list the clips into a label sheet and stop")
    ap.add_argument("--labels", help="filled label sheet (relative_path, expected_suspicious[, person_optional])")
    ap.add_argument("--out", help="manifest to write")
    ap.add_argument("--calib-frac", type=float, default=0.5, help="share of each class in the calibration split")
    ap.add_argument("--seed", type=int, default=20261117)
    a = ap.parse_args()
    if a.from_export:
        if not a.write_template:
            ap.error("--from-export needs --write-template (where to write the label sheet)")
        return labels_from_export(Path(a.from_export), Path(a.write_template))
    if not a.video_root:
        ap.error("--video-root is required")
    root = Path(a.video_root)
    if not root.is_dir():
        sys.exit(f"{root} is not a folder")
    if a.write_template:
        write_template(root, Path(a.write_template))
    elif a.labels and a.out:
        build(root, Path(a.labels), Path(a.out), a.calib_frac, a.seed)
    else:
        ap.error("use --write-template, or --labels together with --out")


if __name__ == "__main__":
    main()