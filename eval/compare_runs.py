#!/usr/bin/env python3
"""Compare several finished runs on exactly the same clips, so the numbers are comparable.

Only clips listed in --manifest are used, for every run. A clip a run did not complete (rejected, failed, missing)
counts as NOT flagged, the same rule analyze_app_eval.py uses for recall. The first run is the reference; every other
run gets a paired comparison against it on the positive clips (clips only one of them caught, and an exact
McNemar p-value). Standard library only.

Example (Gemma baseline vs Qwen on the 47-clip subset):
    python eval/compare_runs.py --manifest eval/runs/subset47/subset_manifest.csv \
        gemma=eval/runs/subset47/baseline_subset.jsonl \
        qwen16=eval/runs/qwen4b_instruct_16f/results.jsonl \
        qwen8=eval/runs/qwen4b_instruct_8f/results.jsonl --stratum main
"""
import argparse
import csv
import json
import math
import sys
from pathlib import Path


def read_jsonl(path):
    return [json.loads(l) for l in Path(path).read_text(encoding="utf-8").splitlines() if l.strip()]


def wilson(k, n, z=1.96):
    if n == 0:
        return (float("nan"), float("nan"))
    p = k / n
    d = 1 + z * z / n
    c = p + z * z / (2 * n)
    m = z * math.sqrt(p * (1 - p) / n + z * z / (4 * n * n))
    return ((c - m) / d, (c + m) / d)


def mcnemar_exact(b, c):
    """Two-sided exact p-value for b vs c discordant pairs."""
    n = b + c
    if n == 0:
        return 1.0
    k = min(b, c)
    p = sum(math.comb(n, i) for i in range(k + 1)) / 2 ** n
    return min(1.0, 2 * p)


def fmt(k, n):
    if n == 0:
        return "n/a"
    lo, hi = wilson(k, n)
    return f"{k}/{n} = {k / n:.3f} [{lo:.3f}-{hi:.3f}]"


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--manifest", required=True, help="the clips to compare on (e.g. subset_manifest.csv)")
    ap.add_argument("--stratum", default="all", help="main, person_optional or all (default)")
    ap.add_argument("--field", default="suspicious", help="result field holding yes/no (default suspicious)")
    ap.add_argument("runs", nargs="+", help="NAME=path/to/results.jsonl, first one is the reference")
    a = ap.parse_args()

    with open(a.manifest, newline="", encoding="utf-8") as f:
        clips = list(csv.DictReader(f))
    for c in clips:
        if not c.get("stratum"):
            c["stratum"] = "person_optional" if c["class_name"] in ("RoadAccidents", "Explosion", "Arson") else "main"
    if a.stratum != "all":
        clips = [c for c in clips if c["stratum"] == a.stratum]
    label = {c["clip_id"]: str(c["expected_suspicious"]).strip().lower() in ("true", "1", "yes") for c in clips}
    if not clips:
        sys.exit("No clips selected.")

    runs = {}
    for spec in a.runs:
        if "=" not in spec:
            sys.exit(f"Expected NAME=path, got {spec!r}")
        name, path = spec.split("=", 1)
        by_id = {r["clip_id"]: r for r in read_jsonl(path)}
        flagged, missing = {}, 0
        for cid in label:
            r = by_id.get(cid)
            ok = r is not None and r.get("status") == "completed"
            missing += not ok
            flagged[cid] = bool(ok and r.get(a.field) == "yes")
        runs[name] = (flagged, missing)

    pos = [c for c in label if label[c]]
    neg = [c for c in label if not label[c]]
    print(f"Clips: {len(label)} ({len(pos)} positive, {len(neg)} Normal), stratum={a.stratum}\n")
    print(f"{'run':<12} {'recall (95% Wilson)':<34} {'false alarm':<26} {'yes-rate':<10} not-completed")
    for name, (flagged, missing) in runs.items():
        tp = sum(flagged[c] for c in pos)
        fp = sum(flagged[c] for c in neg)
        yes = sum(flagged.values()) / len(flagged)
        print(f"{name:<12} {fmt(tp, len(pos)):<34} {fmt(fp, len(neg)):<26} {yes:<10.3f} {missing}")

    names = list(runs)
    ref = names[0]
    if len(names) > 1:
        print(f"\nPaired on positive clips, against {ref}:")
        print(f"{'run':<12} {'both':>5} {ref + ' only':>12} {'run only':>9}  p (exact McNemar)")
        for name in names[1:]:
            both = sum(runs[ref][0][c] and runs[name][0][c] for c in pos)
            only_ref = sum(runs[ref][0][c] and not runs[name][0][c] for c in pos)
            only_run = sum(runs[name][0][c] and not runs[ref][0][c] for c in pos)
            print(f"{name:<12} {both:>5} {only_ref:>12} {only_run:>9}  {mcnemar_exact(only_ref, only_run):.4f}")


if __name__ == "__main__":
    main()