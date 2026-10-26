#!/usr/bin/env python3
"""Run the benchmark manifest through the real CCTV app and record one result per video.

The app is exercised only through its HTTP API (POST /api/videos, then poll /api/state), so
what is measured is the application's own pipeline: frame sampling, YOLO gating, prompt,
model and JSON parsing. Nothing from the notebook is re-implemented here.

Results are appended to <out>/results.jsonl after every video, so an interrupted run resumes
where it stopped. Standard library only.

Example:
    python eval/run_app_eval.py --manifest benchmark/manifest.csv --video-root D:/ucf \
        --settings eval/settings.json --out eval/runs/app_v1
"""
import argparse
import csv
import hashlib
import json
import platform
import re
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

# The Windows console defaults to a legacy code page; model descriptions may contain characters it cannot print.
for _stream in (sys.stdout, sys.stderr):
    if hasattr(_stream, "reconfigure"):
        _stream.reconfigure(encoding="utf-8", errors="replace")

TERMINAL = {"completed", "failed", "cancelled"}
VIDEO_SUFFIXES = {".mp4", ".avi", ".mov", ".mkv", ".webm", ".m4v", ".mpg", ".mpeg", ".wmv"}
MAX_RAW = 20000


class Fatal(Exception):
    """Problem with the environment, not with a video. The run stops without recording it."""


# ---------------------------------------------------------------- HTTP helpers

def call(method, url, body=None, timeout=30):
    """JSON request. Returns (status, parsed_json_or_None). Raises URLError on connection failure."""
    data = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request(url, data=data, method=method,
                                 headers={"Content-Type": "application/json"} if data else {})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            raw = r.read()
            return r.status, (json.loads(raw) if raw else None)
    except urllib.error.HTTPError as e:
        raw = e.read()
        try:
            return e.code, json.loads(raw)
        except ValueError:
            return e.code, {"detail": raw.decode("utf-8", "replace")[:300]}


def call_retry(method, url, body=None, timeout=30, attempts=3):
    last = None
    for i in range(attempts):
        try:
            return call(method, url, body, timeout)
        except (urllib.error.URLError, ConnectionError, TimeoutError) as e:
            last = e
            time.sleep(3 * (i + 1))
    raise Fatal(f"Cannot reach the app at {url}: {last}")


def upload(base, path, timeout):
    """Stream the file as the raw request body, exactly as the browser UI does."""
    size = path.stat().st_size
    with path.open("rb") as f:
        req = urllib.request.Request(
            base + "/api/videos", data=f, method="POST",
            headers={"X-Filename": urllib.parse.quote(path.name),
                     "Content-Type": "application/octet-stream", "Content-Length": str(size)})
        try:
            with urllib.request.urlopen(req, timeout=timeout) as r:
                return r.status, json.loads(r.read())
        except urllib.error.HTTPError as e:
            raw = e.read()
            try:
                return e.code, json.loads(raw)
            except ValueError:
                return e.code, {"detail": raw.decode("utf-8", "replace")[:300]}


def digest(obj):
    return hashlib.sha256(json.dumps(obj, sort_keys=True, separators=(",", ":")).encode()).hexdigest()


def file_sha(path):
    h = hashlib.sha256()
    with Path(path).open("rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


# ---------------------------------------------------------------- manifest

def to_bool(v):
    s = str(v).strip().lower()
    if s in ("true", "1", "yes"):
        return True
    if s in ("false", "0", "no"):
        return False
    raise SystemExit(f"manifest: expected true/false in expected_suspicious, got {v!r}")


def load_manifest(path, splits, strata, limit):
    with open(path, newline="", encoding="utf-8") as f:
        rows = list(csv.DictReader(f))
    need = {"clip_id", "class_name", "evaluation_split", "expected_suspicious"}
    missing = need - set(rows[0].keys() if rows else [])
    if missing:
        raise SystemExit(f"manifest is missing columns: {sorted(missing)}")
    ids = [r["clip_id"] for r in rows]
    if len(ids) != len(set(ids)):
        raise SystemExit("manifest has duplicate clip_id")
    for r in rows:
        r["expected_suspicious"] = to_bool(r["expected_suspicious"])
        if not r.get("stratum"):
            r["stratum"] = "person_optional" if r["class_name"] in ("RoadAccidents", "Explosion", "Arson") else "main"
    if splits != ["all"]:
        rows = [r for r in rows if r["evaluation_split"] in splits]
    if strata != ["all"]:
        rows = [r for r in rows if r["stratum"] in strata]
    rows.sort(key=lambda r: r["clip_id"])
    return rows[:limit] if limit else rows


def video_key(value):
    """Same key the notebook uses for clip_id: lower-case file stem without any symbols.
    'Abuse003_x264.mp4' and 'Normal_Videos_010_x264.mp4' become 'abuse003x264' and 'normalvideos010x264'."""
    return re.sub(r"[^a-z0-9]", "", Path(str(value)).stem.lower())


class VideoLocator:
    """Finds a manifest video under --video-root. Uses relative_path when it exists there, otherwise matches
    the notebook's clip_id against the normalised file names, so the folder layout does not matter."""

    def __init__(self, root):
        self.root = Path(root)
        self._by_key = None

    def _index(self):
        self._by_key = {}
        for p in self.root.rglob("*"):
            if p.suffix.lower() in VIDEO_SUFFIXES:
                self._by_key.setdefault(video_key(p), []).append(p)

    def find(self, row):
        rel = (row.get("relative_path") or "").strip()
        if rel and (self.root / rel).is_file():
            return self.root / rel
        if self._by_key is None:
            self._index()
        key = video_key(rel) if rel else video_key(row["clip_id"])
        hits = self._by_key.get(key) or self._by_key.get(video_key(row["clip_id"])) or []
        if len(hits) > 1:
            raise SystemExit(f"{row['clip_id']}: {len(hits)} files match ({hits[0]}, {hits[1]}, ...). "
                             "Remove the duplicate or put the intended file under a separate --video-root.")
        return hits[0] if hits else None


# ---------------------------------------------------------------- one video

def find_job(state, job_id):
    for j in state.get("jobs", []):
        if j.get("id") == job_id:
            return j
    return None


def evaluate_one(base, row, path, args, settings_hash):
    rec = {
        "clip_id": row["clip_id"], "class_name": row["class_name"],
        "evaluation_split": row["evaluation_split"], "stratum": row["stratum"],
        "expected_suspicious": row["expected_suspicious"], "file": path.name,
        "settings_hash": settings_hash,
        "recorded_at": datetime.now(timezone.utc).isoformat(timespec="seconds"),
    }
    started = time.perf_counter()
    size = path.stat().st_size
    rec["file_mb"] = round(size / 1048576, 1)
    if size > args.max_upload_mb * 1048576:
        # The app refuses bodies over 500 MB (Kestrel limit) and drops the connection while the client is still
        # sending, which looks like a network error. Decide here instead, exactly as the app would.
        rec.update(status="upload_rejected", http_status=413,
                   error=f"File is {size / 1048576:.0f} MB; the app accepts at most {args.max_upload_mb:g} MB "
                         "(rejected before upload).")
        rec["wall_seconds"] = 0.0
        return rec
    status = None
    for attempt in range(3):
        try:
            status, body = upload(base, path, args.upload_timeout)
            break
        except (urllib.error.URLError, ConnectionError, TimeoutError) as e:
            time.sleep(3 * (attempt + 1))
            last = e
    if status is None:
        raise Fatal(f"Upload failed three times for {path.name} ({size / 1048576:.0f} MB): {last}. "
                    "Look at the window where the app runs for an error. If the app crashed or was closed, "
                    "start it again and rerun this same command.")
    rec["upload_seconds"] = round(time.perf_counter() - started, 2)
    rec["http_status"] = status
    if status >= 500:
        raise Fatal(f"App returned {status} on upload of {path.name}: {body.get('detail')}")
    if status >= 400:
        # 413 (>500 MB), 422 (>5 min or unreadable): a property of the video. Counts against coverage.
        rec.update(status="upload_rejected", error=str(body.get("detail", ""))[:300])
        rec["wall_seconds"] = round(time.perf_counter() - started, 2)
        return rec

    job_id = body["id"]
    rec["job_id"] = job_id
    try:
        span = float(row["end_sec"]) - float(row["start_sec"])
        rec["manifest_span_sec"] = round(span, 2)
        rec["span_mismatch"] = abs(span - float(body.get("duration", 0))) > 2.0
    except (KeyError, ValueError, TypeError):
        pass

    deadline = time.monotonic() + args.timeout
    job = None
    while True:
        _, state = call_retry("GET", base + "/api/state")
        if digest(state.get("settings", {})) != settings_hash:
            raise Fatal("Model settings changed while the evaluation was running. "
                        "Restore them (or start a new run) before continuing.")
        job = find_job(state, job_id)
        if job is None:
            rec.update(status="vanished", error="Job disappeared from the queue (removed in the UI?).")
            break
        if job["status"] in TERMINAL:
            break
        if job["status"] == "queued" and not state.get("running"):
            # The app pauses its queue by itself whenever it runs empty (QueueStore.Take), so a single
            # "start" before the first upload is not enough. Restart it whenever our job is waiting.
            call_retry("POST", base + "/api/queue/start")
        if time.monotonic() > deadline:
            call_retry("POST", f"{base}/api/videos/{job_id}/cancel")
            for _ in range(30):
                time.sleep(2)
                _, state = call_retry("GET", base + "/api/state")
                job = find_job(state, job_id)
                if job is None or job["status"] in TERMINAL:
                    break
            rec["status"] = "timeout"
            rec["error"] = f"No result within {args.timeout} s; cancelled."
            break
        time.sleep(args.poll)

    if job is not None and "status" not in rec:
        raw = job.get("rawText")
        rec.update(
            status=job["status"], stage=job.get("stage"),
            suspicious=job.get("suspicious"), weapon=job.get("weapon"),
            description=job.get("description"), error=job.get("error"),
            raw_text=raw[:MAX_RAW] if isinstance(raw, str) else raw,
            model=job.get("model"), backend_mode=job.get("backendMode"),
            person_labels=job.get("personLabels"), no_people=bool(job.get("noPeople")),
            n_frames=len(job.get("timestamps") or []), app_elapsed_seconds=job.get("elapsed"),
            app_duration_sec=job.get("duration"), fps=job.get("fps"),
        )
    elif job is not None:
        rec.update(app_duration_sec=job.get("duration"), fps=job.get("fps"))
    rec["wall_seconds"] = round(time.perf_counter() - started, 2)

    if args.delete_after:
        call_retry("POST", f"{base}/api/videos/{job_id}/delete")
    return rec


# ---------------------------------------------------------------- main

def read_jsonl(path):
    if not path.exists():
        return []
    rows = []
    for n, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        if line.strip():
            try:
                rows.append(json.loads(line))
            except ValueError as e:
                raise SystemExit(f"{path}: line {n} is truncated. Inspect it before resuming.") from e
    return rows


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--manifest", required=True, help="benchmark/manifest.csv exported by the notebook appendix")
    ap.add_argument("--video-root", required=True, help="folder holding the UCF-Crime videos")
    ap.add_argument("--out", required=True, help="run folder (results.jsonl and run.json are written here)")
    ap.add_argument("--base-url", default="http://127.0.0.1:8766")
    ap.add_argument("--settings", help="JSON of model settings to apply first (see eval/settings.example.json). "
                                        "Strongly recommended: it pins the configuration for reproducibility.")
    ap.add_argument("--split", default="test", help="comma list of evaluation_split values, or 'all' (default: test)")
    ap.add_argument("--strata", default="all", help="comma list: main,person_optional (default: all)")
    ap.add_argument("--limit", type=int, help="only the first N clips (smoke test)")
    ap.add_argument("--timeout", type=int, default=900, help="seconds to wait for one video (default 900)")
    ap.add_argument("--upload-timeout", type=int, default=600)
    ap.add_argument("--max-upload-mb", type=float, default=500,
                    help="the app's per-video size limit; larger files are recorded as upload_rejected (default 500)")
    ap.add_argument("--poll", type=float, default=2.0)
    ap.add_argument("--max-consecutive-failures", type=int, default=5,
                    help="stop after this many failed/timed-out videos in a row, e.g. the model server died")
    ap.add_argument("--redo-failed", action="store_true",
                    help="drop failed/timeout rows from results.jsonl (kept in attempts.jsonl) and run them again")
    ap.add_argument("--delete-after", action="store_true", help="remove each job from the app once recorded")
    ap.add_argument("--allow-existing-jobs", action="store_true")
    ap.add_argument("--check-videos", action="store_true",
                    help="only check that every selected video can be found under --video-root, then exit "
                         "(the app does not need to be running)")
    args = ap.parse_args()

    base = args.base_url.rstrip("/")
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    results_path, attempts_path = out / "results.jsonl", out / "attempts.jsonl"

    rows = load_manifest(args.manifest, args.split.split(","), args.strata.split(","), args.limit)
    if not rows:
        raise SystemExit("No manifest rows match --split/--strata.")
    locator = VideoLocator(args.video_root)
    missing = [r["clip_id"] for r in rows if locator.find(r) is None]
    if missing:
        raise SystemExit(f"{len(missing)} of {len(rows)} videos not found under --video-root, e.g. {missing[:5]}. "
                         "Files are matched by name with symbols removed, e.g. 'Abuse003_x264.mp4' = abuse003x264.")
    if args.check_videos:
        total = sum(locator.find(r).stat().st_size for r in rows)
        print(f"OK: all {len(rows)} selected videos found under {args.video_root} ({total / 1e9:.2f} GB).")
        return

    # ---- preflight
    try:
        _, health = call("GET", base + "/api/health")
    except (urllib.error.URLError, ConnectionError) as e:
        raise SystemExit(f"Cannot reach the app at {base}. Start it first. ({e})")
    if not (health or {}).get("ok"):
        raise SystemExit(f"Unexpected /api/health response: {health}")

    if args.settings:
        wanted = json.loads(Path(args.settings).read_text(encoding="utf-8"))
        code, resp = call("POST", base + "/api/settings", wanted)
        if code != 200:
            raise SystemExit(f"App rejected the settings ({code}): {resp}")
    _, state = call("GET", base + "/api/state")
    settings = state.get("settings", {})
    settings_hash = digest(settings)

    busy = [j["name"] for j in state.get("jobs", []) if j.get("status") in ("queued", "processing")]
    if busy and not args.allow_existing_jobs:
        raise SystemExit(f"{len(busy)} jobs are already queued/processing ({busy[:3]}...). "
                         "Clear them or use a fresh CCTV_DATA_DIR, otherwise they interleave with the evaluation.")
    if state.get("linkedFolder"):
        print(f"WARNING: a folder is linked ({state['linkedFolder']}). The watcher may import files into the queue.")
    _, cams = call("GET", base + "/api/cameras")
    if cams:
        print("WARNING: RTSP cameras are configured. Unset CCTV_CAMERAS for the evaluation.")
    print("NOTE: flagged videos trigger notifications if CCTV_WEBHOOK_URL / SMTP / WhatsApp are set. Unset them.")

    spec = {"manifest": str(args.manifest), "manifest_sha256": file_sha(args.manifest),
            "split": args.split, "strata": args.strata, "n_clips": len(rows),
            "settings": settings, "settings_hash": settings_hash, "frame_limit": state.get("frameLimit"),
            "app_health": health, "base_url": base, "python": platform.python_version()}
    run_json = out / "run.json"
    if run_json.exists():
        old = json.loads(run_json.read_text(encoding="utf-8"))
        for key in ("manifest_sha256", "settings_hash", "split", "strata", "frame_limit"):
            if old.get(key) != spec[key] and not args.limit:
                hint = (" The app is running with a different frame limit (CCTV_MAX_FRAMES): restart it with "
                        f"the original value ({old.get('frame_limit')}).") if key == "frame_limit" else ""
                raise SystemExit(f"Cannot resume: {key} differs from the original run "
                                 f"({old.get(key)!r} then, {spec[key]!r} now).{hint} Otherwise use a new --out folder.")
    else:
        spec["started_at"] = datetime.now(timezone.utc).isoformat(timespec="seconds")
        run_json.write_text(json.dumps(spec, indent=2, ensure_ascii=False), encoding="utf-8")

    existing = read_jsonl(results_path)
    if args.redo_failed:
        redo = [r for r in existing if r.get("status") in ("failed", "timeout", "vanished")]
        if redo:
            with attempts_path.open("a", encoding="utf-8") as f:
                for r in redo:
                    f.write(json.dumps(r, ensure_ascii=False) + "\n")
            existing = [r for r in existing if r not in redo]
            results_path.write_text("".join(json.dumps(r, ensure_ascii=False) + "\n" for r in existing), encoding="utf-8")
            print(f"Moved {len(redo)} failed rows to {attempts_path.name} and will run them again.")
    done = {r["clip_id"] for r in existing}
    todo = [r for r in rows if r["clip_id"] not in done]
    print(f"{len(rows)} clips selected, {len(done & {r['clip_id'] for r in rows})} already done, {len(todo)} to run.")

    code, _ = call("POST", base + "/api/queue/start")
    if code != 200:
        raise SystemExit(f"Could not start the queue ({code}).")

    streak = 0
    t0 = time.monotonic()
    for i, row in enumerate(todo, 1):
        path = locator.find(row)
        try:
            rec = evaluate_one(base, row, path, args, settings_hash)
        except Fatal as e:
            raise SystemExit(f"\nSTOPPED (not recorded, safe to resume): {e}")
        with results_path.open("a", encoding="utf-8") as f:
            f.write(json.dumps(rec, ensure_ascii=False, allow_nan=False) + "\n")
            f.flush()
        label = rec.get("suspicious") or rec.get("error") or rec["status"]
        print(f"[{i}/{len(todo)}] {row['clip_id']:<28} {rec['status']:<16} flag={str(label)[:60]} "
              f"({rec['wall_seconds']}s, elapsed total {int(time.monotonic() - t0)}s)")
        streak = streak + 1 if rec["status"] in ("failed", "timeout") else 0
        if streak >= args.max_consecutive_failures:
            raise SystemExit(f"\nSTOPPED: {streak} failures in a row (last error: {rec.get('error')}). "
                             "Fix the model server, then resume with --redo-failed.")

    print(f"\nDone. Results: {results_path}\nNext: python eval/analyze_app_eval.py --results {results_path} "
          f"--manifest {args.manifest} --out {out / 'report'}")


if __name__ == "__main__":
    sys.exit(main())