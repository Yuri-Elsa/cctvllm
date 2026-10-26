#!/usr/bin/env python3
"""Pick a small balanced subset of the test split so a second configuration can be compared quickly.

Writes next to --out-dir:
  subset_manifest.csv      -> use as --manifest for run_app_eval.py and analyze_app_eval.py
  baseline_subset.jsonl    -> the baseline run's results restricted to the same clips (for the comparison report)
Videos the app rejected in the baseline (over 5 min / 500 MB) are skipped: they never reach the model.
Selection is deterministic (seeded) and does not look at any model output other than that rejection.
"""
import argparse
import csv
import json
import random
from pathlib import Path


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--manifest", required=True)
    ap.add_argument("--baseline-results", required=True, help="results.jsonl of the finished baseline run")
    ap.add_argument("--out-dir", required=True)
    ap.add_argument("--per-class", type=int, default=3, help="anomaly videos per class (default 3)")
    ap.add_argument("--normal", type=int, default=8, help="Normal videos (default 8)")
    ap.add_argument("--seed", type=int, default=20260929)
    a = ap.parse_args()

    with open(a.manifest, newline="", encoding="utf-8") as f:
        reader = csv.DictReader(f)
        fields = reader.fieldnames
        rows = [r for r in reader if r["evaluation_split"] == "test"]
    base = {}
    for line in Path(a.baseline_results).read_text(encoding="utf-8").splitlines():
        if line.strip():
            r = json.loads(line)
            base[r["clip_id"]] = r
    usable = [r for r in rows if base.get(r["clip_id"], {}).get("status") == "completed"]

    rng = random.Random(a.seed)
    chosen = []
    for cls in sorted({r["class_name"] for r in usable}):
        pool = sorted((r for r in usable if r["class_name"] == cls), key=lambda r: r["clip_id"])
        rng.shuffle(pool)
        chosen += pool[: a.normal if cls == "Normal" else a.per_class]
    chosen.sort(key=lambda r: r["clip_id"])

    out = Path(a.out_dir)
    out.mkdir(parents=True, exist_ok=True)
    with (out / "subset_manifest.csv").open("w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=fields)
        w.writeheader()
        w.writerows(chosen)
    ids = {r["clip_id"] for r in chosen}
    with (out / "baseline_subset.jsonl").open("w", encoding="utf-8") as f:
        for cid in sorted(ids):
            f.write(json.dumps(base[cid], ensure_ascii=False) + "\n")
    pos = sum(r["expected_suspicious"].lower() == "true" for r in chosen)
    print(f"{len(chosen)} clips ({pos} anomaly, {len(chosen) - pos} Normal) -> {out}")


if __name__ == "__main__":
    main()
