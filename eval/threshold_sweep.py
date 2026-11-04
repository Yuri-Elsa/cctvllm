#!/usr/bin/env python3
"""Re-threshold a finished run: what recall / false-alarm would each p(yes) threshold have given?

Reads the per-clip p(yes) score that run_app_eval.py records (suspicious_score, or --score-field classifier_score
for the separate flag call). Runs made before that field existed have no scores and cannot be used.

It prints the AUC first (the chance that a positive clip scores higher than a Normal one; 0.5 = the score carries no
information, so no threshold can help), then the usual curve, then three picks. Because the threshold is chosen on
the same clips it is measured on, the numbers at a pick are optimistic. The cross-validated line chooses the threshold
on part of the clips and measures on the rest, which is the honest estimate to quote. Standard library only.

Example:
    python eval/threshold_sweep.py --results eval/runs/gemma_split/results.jsonl \
        --manifest eval/runs/subset47/subset_manifest.csv --target-recall 0.9 --out eval/runs/gemma_split/threshold
"""
import argparse
import csv
import json
import math
import random
import sys
from pathlib import Path


def read_jsonl(path):
    return [json.loads(l) for l in Path(path).read_text(encoding="utf-8").splitlines() if l.strip()]


def auc(scored):
    pos = [s for s, y in scored if y]
    neg = [s for s, y in scored if not y]
    if not pos or not neg:
        return None
    wins = sum((p > n) + 0.5 * (p == n) for p in pos for n in neg)
    return wins / (len(pos) * len(neg))


def point(scored, t):
    tp = sum(1 for s, y in scored if y and s >= t)
    fp = sum(1 for s, y in scored if not y and s >= t)
    pos = sum(1 for _, y in scored if y)
    neg = len(scored) - pos
    fn, tn = pos - tp, neg - fp
    rec = tp / pos if pos else None
    prec = tp / (tp + fp) if tp + fp else None
    fpr = fp / neg if neg else None
    f1 = 2 * tp / (2 * tp + fp + fn) if 2 * tp + fp + fn else None
    return dict(threshold=t, tp=tp, fp=fp, fn=fn, tn=tn, recall=rec, precision=prec, fpr=fpr, f1=f1)


def candidates(scored):
    """Every observed score is a cut-off (flag when score >= t), plus 'flag everything' and 'flag nothing'."""
    return sorted({s for s, _ in scored} | {0.0}) + [1.0000001]


def pick_target(scored, target):
    """Highest threshold that still reaches the target recall (fewest false alarms among those)."""
    ok = [point(scored, t) for t in candidates(scored)]
    ok = [p for p in ok if p["recall"] is not None and p["recall"] >= target]
    return max(ok, key=lambda p: p["threshold"]) if ok else None


def pick_f1(scored):
    pts = [point(scored, t) for t in candidates(scored)]
    best = max((p["f1"] or -1) for p in pts)
    plateau = [p for p in pts if (p["f1"] or -1) >= best - 1e-9]
    return plateau[(len(plateau) - 1) // 2]


def pick_youden(scored):
    pts = [point(scored, t) for t in candidates(scored)]
    pts = [p for p in pts if p["recall"] is not None and p["fpr"] is not None]
    return max(pts, key=lambda p: p["recall"] - p["fpr"]) if pts else None


def cross_validate(scored, target, folds, seed):
    rng = random.Random(seed)
    pos = [x for x in scored if x[1]]
    neg = [x for x in scored if not x[1]]
    rng.shuffle(pos)
    rng.shuffle(neg)
    held = {"tp": 0, "fn": 0, "fp": 0, "tn": 0}
    thresholds = []
    k = min(folds, len(pos), len(neg))
    if k < 2:
        return None
    for i in range(k):
        test = pos[i::k] + neg[i::k]
        train = [x for x in pos + neg if x not in test]
        chosen = pick_target(train, target) or point(train, 0.0)
        thresholds.append(chosen["threshold"])
        p = point(test, chosen["threshold"])
        for key in held:
            held[key] += p[key]
    return held, thresholds


def fmt(x, d=3):
    return "n/a" if x is None else f"{x:.{d}f}"


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--results", required=True)
    ap.add_argument("--manifest", help="restrict to these clips (e.g. subset_manifest.csv)")
    ap.add_argument("--score-field", default="suspicious_score")
    ap.add_argument("--stratum", default="all")
    ap.add_argument("--target-recall", type=float, default=0.90)
    ap.add_argument("--folds", type=int, default=5)
    ap.add_argument("--seed", type=int, default=20260929)
    ap.add_argument("--out", help="folder for threshold_curve.csv")
    a = ap.parse_args()

    rows = [r for r in read_jsonl(a.results) if r.get("status") == "completed"]
    if a.manifest:
        with open(a.manifest, newline="", encoding="utf-8") as f:
            keep = {r["clip_id"] for r in csv.DictReader(f)}
        rows = [r for r in rows if r["clip_id"] in keep]
    if a.stratum != "all":
        rows = [r for r in rows if r.get("stratum") == a.stratum]
    scored = [(float(r[a.score_field]), bool(r["expected_suspicious"])) for r in rows if r.get(a.score_field) is not None]
    skipped = len(rows) - len(scored)
    if not scored:
        sys.exit(f"No clip has a {a.score_field}. Run it again with the current run_app_eval.py, and check that the "
                 "model server returns token probabilities (scoreSource must be 'logprobs').")
    npos = sum(y for _, y in scored)
    nneg = len(scored) - npos
    print(f"{len(scored)} clips with a score ({npos} positive, {nneg} Normal); {skipped} completed clips without one.")
    if npos == 0 or nneg == 0:
        sys.exit("Need both positive and Normal clips.")
    print(f"{len({s for s, _ in scored})} distinct score values. "
          f"Positives: median {sorted(s for s, y in scored if y)[npos // 2]:.3f}; "
          f"Normal: median {sorted(s for s, y in scored if not y)[nneg // 2]:.3f}")
    print(f"AUC = {fmt(auc(scored))}  (0.5 = score is no better than chance)\n")

    current = point(scored, 0.5)
    print(f"{'pick':<34}{'threshold':>10}{'recall':>9}{'false alarm':>13}{'precision':>11}")
    def line(name, p):
        if p is None:
            print(f"{name:<34}{'--':>10}  not reachable")
        else:
            print(f"{name:<34}{p['threshold']:>10.4f}{fmt(p['recall']):>9}{fmt(p['fpr']):>13}{fmt(p['precision']):>11}")
    line("threshold 0.50 (app default)", current)
    line(f"recall >= {a.target_recall:g}, fewest FP", pick_target(scored, a.target_recall))
    line("best F1", pick_f1(scored))
    line("Youden (recall - false alarm)", pick_youden(scored))

    cv = cross_validate(scored, a.target_recall, a.folds, a.seed)
    if cv:
        held, ths = cv
        rec = held["tp"] / (held["tp"] + held["fn"])
        fpr = held["fp"] / (held["fp"] + held["tn"])
        print(f"\nCross-validated ({len(ths)} folds, threshold chosen for recall >= {a.target_recall:g} on the other folds):")
        print(f"  held-out recall {rec:.3f} ({held['tp']}/{held['tp'] + held['fn']}), "
              f"false alarm {fpr:.3f} ({held['fp']}/{held['fp'] + held['tn']}); thresholds chosen: "
              + ", ".join(f"{t:.3f}" for t in ths))
        print("  Quote this, not the in-sample line above.")
    else:
        print("\nToo few clips of one kind for cross-validation.")

    if a.out:
        out = Path(a.out)
        out.mkdir(parents=True, exist_ok=True)
        with (out / "threshold_curve.csv").open("w", newline="", encoding="utf-8") as f:
            w = csv.writer(f)
            w.writerow(["threshold", "tp", "fp", "fn", "tn", "recall", "precision", "false_alarm", "f1"])
            for t in candidates(scored):
                p = point(scored, t)
                w.writerow([f"{t:.6f}", p["tp"], p["fp"], p["fn"], p["tn"], fmt(p["recall"], 4), fmt(p["precision"], 4),
                            fmt(p["fpr"], 4), fmt(p["f1"], 4)])
        print(f"\nCurve written to {out / 'threshold_curve.csv'}")


if __name__ == "__main__":
    main()