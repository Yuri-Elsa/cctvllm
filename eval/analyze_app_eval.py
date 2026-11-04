#!/usr/bin/env python3
"""Score an app evaluation run and compare it with the notebook.

Metric definitions follow evaluation.ipynb (classification_summary): valid-only confusion
counts, `positive_recall_all` counts invalid positives as misses, and invalid outputs are routed
to human review. Confidence intervals are bootstrap percentile intervals that resample positives
and negatives separately (default 2000 draws), plus Wilson intervals for plain proportions.

Needs pandas and numpy.

Example:
    python eval/analyze_app_eval.py --results eval/runs/app_v1/results.jsonl \
        --manifest benchmark/manifest.csv --out eval/runs/app_v1/report
"""
import argparse
import json
import sys
from pathlib import Path

import numpy as np
import pandas as pd

# The Windows console defaults to a legacy code page that cannot print some characters; never crash on output.
for _stream in (sys.stdout, sys.stderr):
    if hasattr(_stream, "reconfigure"):
        _stream.reconfigure(encoding="utf-8", errors="replace")

# Numbers printed by evaluation.ipynb on test/main (91 videos: 70 positive, 21 Normal) and
# test/person_optional (21 positive). Sources: cell 16/18 (fixed yes/no flag) and cell 20
# (threshold 0.004905 on p_yes_binary, selected on calibration/main to reach 0.90 recall).
NOTEBOOK_REFERENCE = {
    "main": {
        "Notebook, flag yes/no (gated_8)": dict(TP=44, FN=26, FP=0, TN=21, review_rate=0.4835),
        "Notebook, flag yes/no (ungated_8)": dict(TP=43, FN=27, FP=0, TN=21, review_rate=0.4725),
        "Notebook, flag yes/no (gated_16)": dict(TP=41, FN=29, FP=0, TN=21, review_rate=0.4505),
        "Notebook, threshold 0.0049 (slide: 0.914)": dict(TP=64, FN=6, FP=3, TN=18, review_rate=0.7363),
    },
    "person_optional": {
        "Notebook, flag yes/no (gated_8)": dict(TP=11, FN=10, FP=0, TN=0, review_rate=0.5238),
        "Notebook, threshold 0.0049 (slide: 0.914)": dict(TP=15, FN=6, FP=0, TN=0, review_rate=0.7143),
    },
}
SLIDE_RECALL = 0.914
DEGENERATE_SHARE = 0.95


# ---------------------------------------------------------------- loading

def load(results_path, manifest_path, allow_incomplete):
    manifest = pd.read_csv(manifest_path, keep_default_na=False, dtype={"clip_id": str})
    manifest["expected_suspicious"] = manifest.expected_suspicious.astype(str).str.lower().eq("true")
    if "stratum" not in manifest or manifest.stratum.eq("").any():
        manifest["stratum"] = np.where(manifest.class_name.isin(["RoadAccidents", "Explosion", "Arson"]),
                                       "person_optional", "main")
    rows = [json.loads(line) for line in Path(results_path).read_text(encoding="utf-8").splitlines() if line.strip()]
    res = pd.DataFrame(rows)
    if res.empty:
        raise SystemExit("results file is empty")
    if res.clip_id.duplicated().any():
        raise SystemExit("duplicate clip_id in results")
    unknown = set(res.clip_id) - set(manifest.clip_id)
    if unknown:
        raise SystemExit(f"results contain clips that are not in the manifest: {sorted(unknown)[:5]}")
    keep = ["clip_id", "class_name", "evaluation_split", "stratum", "expected_suspicious"]
    ran_splits = set(res.evaluation_split)
    scope = manifest[manifest.evaluation_split.isin(ran_splits)][keep]
    df = scope.merge(res.drop(columns=[c for c in keep if c != "clip_id"]), on="clip_id", how="left")
    n_missing = int(df.status.isna().sum())
    if n_missing and not allow_incomplete:
        raise SystemExit(f"{n_missing} manifest clips in the evaluated splits have no result. Finish the run, "
                         "or pass --allow-incomplete to count them as not_run (invalid).")
    df["status"] = df.status.fillna("not_run")
    flag = df.get("suspicious", pd.Series(index=df.index, dtype=object)).astype(str).str.strip().str.lower()
    df["parsed_flag"] = flag.map({"yes": True, "no": False})
    df["valid"] = df.status.eq("completed") & df.parsed_flag.notna()
    df["invalid_reason"] = np.select(
        [df.valid, df.status.eq("completed"), df.status.eq("not_run")],
        ["", "unparsable_flag", "not_run"], default=df.status)
    for col in ("no_people", "span_mismatch"):
        if col not in df:
            df[col] = False
        df[col] = df[col].fillna(False).astype(bool)

    # p(yes) recorded by the app (run_app_eval.py stores it from the screening log). Runs made before that
    # was added have no such column: everything below that needs scores is then skipped, not guessed.
    raw_score = (pd.to_numeric(df["suspicious_score"], errors="coerce") if "suspicious_score" in df
                 else pd.Series(np.nan, index=df.index))
    df["score"] = raw_score
    # score_eff is the value a threshold sweep compares against, defined only for valid outputs:
    #   nobody detected -> 0.0 (the model never ran, so no threshold can flag it, as in the app);
    #   score recorded  -> the score;
    #   valid but no score (model server gave no log-probabilities) -> 1.0 for "yes", 0.0 for "no"
    #   (the same fallback as Scoring.Effective in the app).
    fallback = df.parsed_flag.fillna(False).astype(bool).astype(float)
    df["score_eff"] = np.where(~df.valid, np.nan, np.where(df.no_people, 0.0, raw_score.fillna(fallback)))
    df["scored"] = df.valid & ~df.no_people & raw_score.notna()
    return df, manifest


# ---------------------------------------------------------------- metrics

def ratio(a, b):
    return float(a / b) if b else None


def counts(d):
    y = d.expected_suspicious.to_numpy(bool)
    valid = d.valid.to_numpy(bool)
    pred = d.parsed_flag.fillna(False).to_numpy(bool)
    return y, valid, pred


def summary(d):
    y, valid, pred = counts(d)
    tp = int((valid & y & pred).sum()); fn = int((valid & y & ~pred).sum())
    fp = int((valid & ~y & pred).sum()); tn = int((valid & ~y & ~pred).sum())
    inv_pos = int((~valid & y).sum()); inv_neg = int((~valid & ~y).sum())
    routed = ~valid | pred
    return {
        "n_total": len(d), "n_valid": int(valid.sum()), "coverage": ratio(valid.sum(), len(d)),
        "n_positive": int(y.sum()), "n_negative": int((~y).sum()),
        "TP": tp, "FN_valid": fn, "FP": fp, "TN": tn,
        "invalid_positive": inv_pos, "invalid_negative": inv_neg,
        "accuracy_valid": ratio(tp + tn, valid.sum()),
        "precision_valid": ratio(tp, tp + fp), "recall_valid": ratio(tp, tp + fn),
        "F1_valid": ratio(2 * tp, 2 * tp + fp + fn),
        "positive_recall_all": ratio(tp, y.sum()),
        "false_alarm_rate_valid": ratio(fp, fp + tn),
        "false_alarm_rate_all": ratio(fp + inv_neg, (~y).sum()),
        "review_rate_all": ratio(routed.sum(), len(d)),
    }


def wilson(k, n, z=1.96):
    if not n:
        return (None, None)
    p = k / n
    den = 1 + z * z / n
    centre = (p + z * z / (2 * n)) / den
    half = z * np.sqrt(p * (1 - p) / n + z * z / (4 * n * n)) / den
    return (float(max(0, centre - half)), float(min(1, centre + half)))


def bootstrap(d, n_boot, rng):
    """Percentile CIs for the headline metrics. Resamples positives and negatives separately so the
    class balance of the benchmark is preserved in every draw."""
    y, valid, pred = counts(d)
    pos, neg = np.flatnonzero(y), np.flatnonzero(~y)
    if not len(pos):
        return {}
    out = {k: [] for k in ("positive_recall_all", "precision_valid", "F1_valid", "false_alarm_rate_valid", "review_rate_all")}
    for _ in range(n_boot):
        idx = np.concatenate([rng.choice(pos, len(pos)), rng.choice(neg, len(neg)) if len(neg) else neg])
        yy, vv, pp = y[idx], valid[idx], pred[idx]
        tp = (vv & yy & pp).sum(); fp = (vv & ~yy & pp).sum(); fn = (vv & yy & ~pp).sum(); tn = (vv & ~yy & ~pp).sum()
        out["positive_recall_all"].append(tp / yy.sum())
        out["precision_valid"].append(tp / (tp + fp) if tp + fp else np.nan)
        out["F1_valid"].append(2 * tp / (2 * tp + fp + fn) if 2 * tp + fp + fn else np.nan)
        out["false_alarm_rate_valid"].append(fp / (fp + tn) if fp + tn else np.nan)
        out["review_rate_all"].append(((~vv) | pp).mean())
    ci = {}
    for k, v in out.items():
        v = np.asarray(v, float)
        v = v[~np.isnan(v)]
        ci[k] = (float(np.percentile(v, 2.5)), float(np.percentile(v, 97.5))) if len(v) else (None, None)
    return ci


def from_counts(ref):
    tp, fn, fp, tn = ref["TP"], ref["FN"], ref["FP"], ref["TN"]
    return {"recall": ratio(tp, tp + fn), "precision": ratio(tp, tp + fp),
            "F1": ratio(2 * tp, 2 * tp + fp + fn), "false_alarm_rate": ratio(fp, fp + tn),
            "review_rate": ref["review_rate"], "TP": tp, "FN": fn, "FP": fp, "TN": tn}


def paired_recall_delta(a, b, n_boot, rng):
    """b minus a on the same positive videos (invalid counts as a miss). a and b are bool arrays."""
    n = len(a)
    if not n:
        return None
    idx = rng.integers(0, n, size=(n_boot, n))
    deltas = b[idx].mean(axis=1) - a[idx].mean(axis=1)
    return float(b.mean() - a.mean()), float(np.percentile(deltas, 2.5)), float(np.percentile(deltas, 97.5))


# ---------------------------------------------------------------- threshold sweep

MIN_POSITIVES, MIN_NEGATIVES = 10, 30      # same warning limits as the app's Calibration page
SATURATED_HI, SATURATED_LO = 0.9999, 1e-4  # scores this close to 1 or 0 are fragile (quantisation, model update)
NOTEBOOK_OPERATING_POINT = (0.143, 0.914)  # (false alarm, recall) of the notebook at its tuned threshold


def curve(g):
    """Every distinct threshold of the valid outputs in g, with the confusion counts a flag at `score >= t`
    would give. Invalid outputs count as missed events (recall over all positives) and are never false alarms,
    like the headline table."""
    y = g.expected_suspicious.to_numpy(bool)
    valid = g.valid.to_numpy(bool)
    s = g.score_eff.to_numpy(float)
    if not valid.any():
        return pd.DataFrame()
    cand = np.unique(s[valid])
    ts = np.unique(np.concatenate([[0.0], cand, [np.nextafter(cand.max(), np.inf)]]))
    n_pos = int(y.sum())
    rows = []
    for t in ts:
        f = valid & (s >= t)
        tp, fp = int((f & y).sum()), int((f & ~y).sum())
        fn, tn = int((valid & y & ~f).sum()), int((valid & ~y & ~f).sum())
        rows.append(dict(threshold=float(t), TP=tp, FN_valid=fn, FP=fp, TN=tn,
                         recall_all=ratio(tp, n_pos), precision=ratio(tp, tp + fp),
                         false_alarm_rate=ratio(fp, fp + tn), F1=ratio(2 * tp, 2 * tp + fp + fn),
                         review_rate=float(((~valid) | f).mean())))
    return pd.DataFrame(rows)


def _auc(y, s):
    """Area under the ROC curve (Mann-Whitney, ties count half). None when one class is missing."""
    n1, n0 = int(y.sum()), int((~y).sum())
    if not n1 or not n0:
        return None
    r = pd.Series(s).rank().to_numpy()
    return float((r[y].sum() - n1 * (n1 + 1) / 2) / (n1 * n0))


def auc_with_ci(g, n_boot, rng):
    v = g[g.valid]
    y, s = v.expected_suspicious.to_numpy(bool), v.score_eff.to_numpy(float)
    point = _auc(y, s)
    if point is None:
        return None, (None, None)
    pos, neg = np.flatnonzero(y), np.flatnonzero(~y)
    draws = []
    for _ in range(n_boot):
        idx = np.concatenate([rng.choice(pos, len(pos)), rng.choice(neg, len(neg))])
        draws.append(_auc(y[idx], s[idx]))
    return point, (float(np.percentile(draws, 2.5)), float(np.percentile(draws, 97.5)))


def pick_threshold(c, target_recall):
    """Highest threshold that still reaches the target recall (fewest false alarms for that recall).
    None when no threshold reaches it, for example because invalid outputs already cap the recall."""
    ok = c[c.recall_all >= target_recall]
    return None if ok.empty else float(ok.threshold.max())


def at_threshold(g, t, n_boot, rng):
    """Summary and bootstrap CIs for g when flagging `score >= t`, using the same code as the headline table."""
    g2 = g.copy()
    g2["parsed_flag"] = (g2.score_eff >= t).where(g2.valid)
    return summary(g2), bootstrap(g2, n_boot, rng)


def op_row(label, t, s, ci):
    fa = wilson(s["FP"], s["FP"] + s["TN"])
    return (label, "n/a" if t is None else f"{t:.6g}",
            with_ci(s["positive_recall_all"], ci.get("positive_recall_all")), with_ci(s["false_alarm_rate_valid"], fa),
            num(s["precision_valid"]), f"{s['review_rate_all']:.3f}",
            f"{s['TP']}/{s['FN_valid']}/{s['FP']}/{s['TN']}")


def fragile(t):
    return t is not None and (t >= SATURATED_HI or (0 < t <= SATURATED_LO))


def plot_curves(curves, app_points, path):
    """Recall against false-alarm rate. Optional: skipped when matplotlib is not installed."""
    try:
        import matplotlib
        matplotlib.use("Agg")
        import matplotlib.pyplot as plt
    except ImportError:
        return False
    fig, ax = plt.subplots(figsize=(5.5, 5))
    for label, c in curves.items():
        ax.step(c.false_alarm_rate, c.recall_all, where="post", label=label)
    for label, (x, y) in app_points.items():
        ax.scatter([x], [y], zorder=3, label=label)
    ax.plot([0, 1], [0, 1], color="grey", lw=0.8, ls=":")
    ax.set(xlabel="False alarm (Normal yang ditandai)", ylabel="Recall (semua positif)", xlim=(0, 1), ylim=(0, 1),
           title="Recall vs false alarm per threshold p(yes)")
    ax.legend(fontsize=7, loc="lower right")
    fig.tight_layout()
    fig.savefig(path, dpi=130)
    plt.close(fig)
    return True


# ---------------------------------------------------------------- formatting

def missing(x):
    return x is None or (isinstance(x, float) and np.isnan(x))


def pct(x, digits=1):
    return "n/a" if missing(x) else f"{100 * x:.{digits}f}%"


def num(x):
    return "n/a" if missing(x) else f"{x:.3f}"


def with_ci(x, ci):
    """ci is a (lo, hi) pair or None."""
    if missing(x):
        return "n/a"
    if not ci or missing(ci[0]) or missing(ci[1]):
        return num(x)
    return f"{x:.3f} [{ci[0]:.3f}-{ci[1]:.3f}]"


def verdict(value, ci, ref, name):
    if missing(value) or not ci or missing(ci[0]):
        return ""
    if ci[0] > ref:
        return f"lebih tinggi dari {name}; batas bawah CI {ci[0]:.3f} di atas {ref:.3f}"
    if ci[1] < ref:
        return f"lebih rendah dari {name}; batas atas CI {ci[1]:.3f} di bawah {ref:.3f}"
    return f"tidak dapat dibedakan dari {name}; CI {ci[0]:.3f}-{ci[1]:.3f} mencakup {ref:.3f}"


def md_table(headers, rows):
    lines = ["| " + " | ".join(headers) + " |", "|" + "|".join("---" for _ in headers) + "|"]
    lines += ["| " + " | ".join(str(c) for c in r) + " |" for r in rows]
    return "\n".join(lines)


# ---------------------------------------------------------------- main

def threshold_section(df, sweep, agg, run, args, rng):
    """Markdown lines for the threshold sweep on test/main. Threshold selection follows the person-detection
    protocol: choose on the calibration split, freeze, then score on test. Without a calibration split in the
    run only an optimistic (chosen-on-test) point can be shown, and it is labelled as such."""
    has_test = df.evaluation_split.eq("test").any()
    tg = df[df.evaluation_split.eq("test") & df.stratum.eq("main")] if has_test else df[df.stratum.eq("main")]
    if tg.empty or (has_test and ("test", "main") not in sweep):
        return []
    L = ["## Kurva threshold (skor p(yes))\n"]
    n_valid = int(tg.valid.sum())
    n_scored = int(tg.scored.sum())
    n_nop = int((tg.valid & tg.no_people).sum())
    n_fallback = int((tg.valid & ~tg.no_people & ~tg.scored).sum())
    L.append(f"- Skor p(yes) tersedia untuk {n_scored} dari {n_valid} video valid (test/main); {n_nop} video tanpa orang "
             "dihitung 0 karena model tidak dipanggil"
             + (f"; {n_fallback} video valid tanpa skor memakai yes=1 / no=0, sehingga kurva kasar di bagian itu" if n_fallback else "")
             + ".")
    if "model" in df and df.model.dropna().nunique() > 1:
        L.append("- **Peringatan:** skor berasal dari lebih dari satu model; skor model berbeda tidak sebanding.")
    auc, ci_auc = auc_with_ci(tg, args.n_boot, rng)
    if auc is not None:
        L.append(f"- AUC (video valid): {with_ci(auc, ci_auc)}. Nilai 0,5 berarti skor tidak lebih baik dari menebak.")
    L.append("")

    headers = ["Titik operasi", "Threshold", "Recall (semua positif)", "False alarm (Normal)", "Precision",
               "Review rate", "TP/FN/FP/TN"]
    rows = []
    s_app, ci_app = summary(tg), bootstrap(tg, args.n_boot, rng)
    ths = sorted({round(float(x), 6) for x in tg.suspicious_threshold.dropna()}) if "suspicious_threshold" in tg else []
    rows.append(op_row("Aplikasi (flag saat run)", ths[0] if len(ths) == 1 else None, s_app, ci_app))
    nb = from_counts(NOTEBOOK_REFERENCE["main"]["Notebook, threshold 0.0049 (slide: 0.914)"])
    rows.append(("Notebook, threshold 0.0049 (skala skor berbeda)", "0.0049", f"{nb['recall']:.3f}",
                 num(nb["false_alarm_rate"]), num(nb["precision"]), f"{nb['review_rate']:.3f}",
                 f"{nb['TP']}/{nb['FN']}/{nb['FP']}/{nb['TN']}"))

    notes = []
    target = args.target_recall
    cal = df[df.evaluation_split.eq("calibration") & df.stratum.eq("main")]
    c_test = sweep.get(("test", "main"))
    if has_test and len(cal) and ("calibration", "main") in sweep:
        n_pos_c, n_neg_c = int(cal.expected_suspicious.sum()), int((~cal.expected_suspicious).sum())
        if n_pos_c < MIN_POSITIVES or n_neg_c < MIN_NEGATIVES:
            notes.append(f"Split calibration kecil ({n_pos_c} positif, {n_neg_c} Normal; batas yang disarankan "
                         f"{MIN_POSITIVES} dan {MIN_NEGATIVES}). Threshold yang dipilih darinya tidak stabil.")
        t = pick_threshold(sweep[("calibration", "main")], target)
        if t is None:
            notes.append(f"Tidak ada threshold di calibration yang mencapai recall {target:.0%} "
                         "(output tidak valid ikut dihitung sebagai terlewat).")
        else:
            s_t, ci_t = at_threshold(tg, t, args.n_boot, rng)
            rows.append(op_row(f"Dipilih di calibration (recall ≥ {target:.0%}), dibekukan", t, s_t, ci_t))
            rec = s_t["positive_recall_all"]
            notes.append("Recall di titik terbekukan: "
                         + (verdict(rec, ci_t.get("positive_recall_all"), SLIDE_RECALL, "angka di slide") or "n/a")
                         + f"; target {target:.0%} " + ("tercapai" if rec >= target else "tidak tercapai") + " di test.")
            if fragile(t):
                notes.append(f"Threshold {t:.6g} ada di ekor jenuh (dekat 0 atau 1). Nilai sedekat itu rapuh terhadap "
                             "perubahan model, kuantisasi atau perangkat; periksa kurva dan titik yang lebih longgar sebelum dipakai.")
    elif c_test is not None:
        t = pick_threshold(c_test, target)
        if t is not None:
            s_t, ci_t = at_threshold(tg, t, args.n_boot, rng)
            rows.append(op_row(f"OPTIMISTIS: dipilih di test sendiri (recall ≥ {target:.0%})", t, s_t, ci_t))
        notes.append("Run ini tidak memuat split calibration, jadi threshold tidak bisa dipilih secara jujur. Baris "
                     "'OPTIMISTIS' dipilih dan dinilai pada data yang sama dan melebihkan kinerja. Jalankan "
                     "`run_app_eval.py --split calibration,test` agar threshold dipilih di calibration lalu dibekukan.")
    for t in args.threshold:
        s_t, ci_t = at_threshold(tg, t, args.n_boot, rng)
        rows.append(op_row("Ditetapkan (--threshold)", t, s_t, ci_t))
    L.append(md_table(headers, rows))
    L.append("")
    L.append("Recall dihitung atas semua positif; CI 95% bootstrap untuk recall, Wilson untuk false alarm. "
             "Skor p(yes) dari model 4B cenderung jenuh (mendekati 0 atau 1), jadi banyak video bisa berbagi skor yang sama "
             "dan recall naik dalam lompatan besar.\n")
    L += [f"- {n}" for n in notes]
    if notes:
        L.append("")
    return L


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--results", required=True)
    ap.add_argument("--manifest", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--run-json", help="defaults to run.json next to results.jsonl")
    ap.add_argument("--n-boot", type=int, default=2000)
    ap.add_argument("--seed", type=int, default=20260929)
    ap.add_argument("--allow-incomplete", action="store_true")
    ap.add_argument("--notebook-predictions",
                    help="notebook runs/baseline_v2/gated_8/predictions.jsonl (or .csv) for a paired per-video comparison")
    ap.add_argument("--notebook-threshold", type=float, default=0.004905405919998884,
                    help="threshold applied to the notebook's p_yes_binary for the 'tuned' arm")
    ap.add_argument("--target-recall", type=float, default=0.90,
                    help="recall to reach when choosing a threshold on the calibration split (default 0.90, the notebook's)")
    ap.add_argument("--threshold", type=float, action="append", default=[],
                    help="a p(yes) threshold to report on test, frozen in advance (repeatable)")
    args = ap.parse_args()

    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    rng = np.random.default_rng(args.seed)
    df, _ = load(args.results, args.manifest, args.allow_incomplete)
    run_json = Path(args.run_json) if args.run_json else Path(args.results).with_name("run.json")
    run = json.loads(run_json.read_text(encoding="utf-8")) if run_json.exists() else {}

    # ---- tables
    agg_rows, ci_by_group = [], {}
    for (split, stratum), g in df.groupby(["evaluation_split", "stratum"]):
        s = summary(g)
        ci = bootstrap(g, args.n_boot, rng)
        ci_by_group[(split, stratum)] = ci
        lo, hi = wilson(s["TP"], s["n_positive"])
        s.update(split=split, stratum=stratum, recall_wilson_lo=lo, recall_wilson_hi=hi)
        for k, (a, b) in ci.items():
            s[f"{k}_ci_lo"], s[f"{k}_ci_hi"] = a, b
        fa_lo, fa_hi = wilson(s["FP"], s["FP"] + s["TN"])
        pr_lo, pr_hi = wilson(s["TP"], s["TP"] + s["FP"])
        s.update(false_alarm_wilson_lo=fa_lo, false_alarm_wilson_hi=fa_hi,
                 precision_wilson_lo=pr_lo, precision_wilson_hi=pr_hi)
        agg_rows.append(s)
    agg = pd.DataFrame(agg_rows)
    first = ["split", "stratum"]
    agg = agg[first + [c for c in agg.columns if c not in first]]
    agg.to_csv(out / "metrics_by_split_stratum.csv", index=False)

    cls_rows = []
    for (split, cls), g in df.groupby(["evaluation_split", "class_name"]):
        v = g[g.valid]
        cls_rows.append({
            "split": split, "class_name": cls, "n": len(g), "n_valid": len(v),
            "flagged_yes": int(v.parsed_flag.sum()), "flagged_no": int((~v.parsed_flag.astype(bool)).sum()),
            "no_people": int(g.no_people.sum()), "invalid": int((~g.valid).sum()),
            "note": "jumlah saja (n<10)" if len(g) < 10 else "",
        })
    pd.DataFrame(cls_rows).to_csv(out / "per_class.csv", index=False)

    cols = [c for c in ["clip_id", "class_name", "evaluation_split", "stratum", "expected_suspicious", "status",
                        "invalid_reason", "suspicious", "suspicious_score", "suspicious_threshold",
                        "score_source", "weapon", "no_people", "person_detector", "n_frames", "app_elapsed_seconds",
                        "wall_seconds", "app_duration_sec", "manifest_span_sec", "span_mismatch", "model",
                        "error", "description"] if c in df.columns]
    df[cols].to_csv(out / "per_video.csv", index=False)
    df[~df.valid][[c for c in cols if c != "description"] + (["raw_text"] if "raw_text" in df else [])] \
        .to_csv(out / "invalid_outputs.csv", index=False)

    # ---- no-people effect and degenerate flags (test split only if present, else everything)
    scope = df[df.evaluation_split.eq("test")] if df.evaluation_split.eq("test").any() else df
    pos = scope[scope.expected_suspicious]
    np_note = []
    for label, sub in (("tanpa orang terdeteksi (LLM tidak dipanggil)", pos[pos.no_people]),
                       ("ada orang terdeteksi", pos[~pos.no_people])):
        if len(sub):
            hit = int((sub.valid & sub.parsed_flag.fillna(False).astype(bool)).sum())
            np_note.append((label, len(sub), hit, ratio(hit, len(sub))))
    degenerate = []
    for stratum, g in scope.groupby("stratum"):
        v = g[g.valid]
        if len(v):
            share_yes = float(v.parsed_flag.astype(bool).mean())
            if share_yes >= DEGENERATE_SHARE:
                degenerate.append(f"{stratum}: {pct(share_yes)} flag 'yes' (hampir selalu yes)")
            elif share_yes <= 1 - DEGENERATE_SHARE:
                degenerate.append(f"{stratum}: {pct(1 - share_yes)} flag 'no' (hampir selalu no)")
    v_all = scope[scope.valid]
    yes_share_all = float(v_all.parsed_flag.astype(bool).mean()) if len(v_all) else None

    # ---- paired comparison with notebook predictions
    paired_lines = []
    if args.notebook_predictions:
        p = Path(args.notebook_predictions)
        nb = pd.read_json(p, lines=True) if p.suffix == ".jsonl" else pd.read_csv(p)
        nb = nb.drop_duplicates("clip_id").set_index("clip_id")
        main_pos = scope[scope.expected_suspicious & scope.clip_id.isin(nb.index)]
        arms = {}
        if "predicted_suspicious" in nb:
            f = nb.loc[main_pos.clip_id, "predicted_suspicious"].astype(str).str.lower().isin(["yes", "true"])
            arms["notebook flag yes/no"] = (f & nb.loc[main_pos.clip_id, "flag_valid"].astype(str).str.lower().eq("true")
                                           if "flag_valid" in nb else f).to_numpy(bool)
        if "p_yes_binary" in nb:
            arms[f"notebook threshold {args.notebook_threshold:.4f}"] = (
                pd.to_numeric(nb.loc[main_pos.clip_id, "p_yes_binary"], errors="coerce") >= args.notebook_threshold).to_numpy(bool)
        app_hit = (main_pos.valid & main_pos.parsed_flag.fillna(False).astype(bool)).to_numpy(bool)
        for name, arr in arms.items():
            r = paired_recall_delta(arr, app_hit, args.n_boot, rng)
            if r:
                both = int((arr & app_hit).sum()); only_nb = int((arr & ~app_hit).sum()); only_app = int((~arr & app_hit).sum())
                paired_lines.append(
                    f"- Aplikasi dikurangi {name}: selisih recall {r[0]:+.3f} (CI 95% {r[1]:+.3f} sampai {r[2]:+.3f}) pada "
                    f"{len(main_pos)} video positif; terdeteksi keduanya {both}, hanya notebook {only_nb}, hanya aplikasi {only_app}.")

    # ---- threshold sweep over the recorded p(yes)
    scored_any = bool(df.scored.any())
    sweep, sweep_lines, plot_note = {}, [], ""
    if scored_any:
        for (split, stratum), g in df.groupby(["evaluation_split", "stratum"]):
            c = curve(g)
            if len(c):
                sweep[(split, stratum)] = c
        if sweep:
            pd.concat([c.assign(split=k[0], stratum=k[1]) for k, c in sweep.items()], ignore_index=True) \
                .to_csv(out / "threshold_curve.csv", index=False)
        sweep_lines = threshold_section(df, sweep, agg, run, args, rng)
        curves, pts = {}, {}
        for split in ("calibration", "test"):
            c = sweep.get((split, "main"))
            if c is not None and c.false_alarm_rate.notna().any():
                curves[f"{split}/main"] = c
        t_main = agg[(agg.split == "test") & (agg.stratum == "main")]
        if len(t_main) and not missing(t_main.iloc[0].false_alarm_rate_valid):
            r0 = t_main.iloc[0]
            pts["Aplikasi (flag saat run)"] = (r0.false_alarm_rate_valid, r0.positive_recall_all)
        pts["Notebook, threshold 0.0049"] = NOTEBOOK_OPERATING_POINT
        if curves and plot_curves(curves, pts, out / "threshold_curve.png"):
            plot_note = "Grafik: `threshold_curve.png`. "

    # ---- latency
    lat = scope[scope.valid]
    latency = {}
    if len(lat) and "app_elapsed_seconds" in lat:
        e = pd.to_numeric(lat.app_elapsed_seconds, errors="coerce").dropna()
        if len(e):
            latency.update(median=float(e.median()), p90=float(e.quantile(0.9)), max=float(e.max()))
    frames = pd.to_numeric(lat.get("n_frames"), errors="coerce").dropna() if "n_frames" in lat else pd.Series(dtype=float)

    # ---- report
    L = []
    L.append("# Evaluasi pipeline aplikasi CCTV-LLM\n")
    s0 = run.get("settings", {})
    L.append("## Konfigurasi yang diuji\n")
    L.append(md_table(["Item", "Nilai"], [
        ("Mode / model", f"{s0.get('mode', '?')} / {s0.get('model', '?')}"),
        ("Endpoint", s0.get("endpoint", "?")),
        ("Person labels (YOLO)", s0.get("personLabels", "?")),
        ("Weapon / batch", f"{s0.get('weaponEnabled', '?')} / {s0.get('batchProcessing', '?')}"),
        ("Batas frame aplikasi", run.get("frame_limit", "?")),
        ("Rata-rata frame terkirim", f"{frames.mean():.1f}" if len(frames) else "n/a"),
        ("Manifest sha256", (run.get("manifest_sha256") or "?")[:16] + "..."),
        ("Mulai", run.get("started_at", "?")),
    ]))
    L.append("")

    L.append("## Hasil utama (held-out test)\n")
    L.append("Angka notebook dikutip dari output `evaluation.ipynb` (sel 16, 18, 20). Dua acuan dipakai karena "
             "angka 0,914 di slide berasal dari threshold 0,0049 pada skor notebook; baris 'flag yes/no' adalah acuan "
             "yang memakai flag yang dibangkitkan model.\n")
    if scored_any:
        ths = sorted({round(float(x), 6) for x in df.suspicious_threshold.dropna()}) if "suspicious_threshold" in df else []
        L.append("Baris **Aplikasi** memakai flag yang diputuskan aplikasi saat run"
                 + (f" (threshold p(yes) = {', '.join(str(x) for x in ths)})" if ths else "")
                 + ". Bagian 'Kurva threshold' di bawah menyapu semua threshold dari skor yang direkam.\n")
    else:
        L.append("Run ini tidak menyimpan skor p(yes) (`suspicious_score`), jadi hanya threshold yang dipakai aplikasi "
                 "saat run yang bisa dinilai. Jalankan ulang dengan `run_app_eval.py` versi terbaru untuk kurva threshold.\n")
    for stratum in ("main", "person_optional"):
        a = agg[(agg.split == "test") & (agg.stratum == stratum)]
        if a.empty:
            continue
        r = a.iloc[0]
        ci = ci_by_group[("test", stratum)]
        L.append(f"### test / {stratum} (n={int(r.n_total)}, positif={int(r.n_positive)}, Normal={int(r.n_negative)})\n")
        rows = []
        for name, ref in NOTEBOOK_REFERENCE.get(stratum, {}).items():
            m = from_counts(ref)
            rows.append((name, f"{m['recall']:.3f}", num(m["precision"]), num(m["F1"]), num(m["false_alarm_rate"]),
                         f"{m['review_rate']:.3f}", f"{m['TP']}/{m['FN']}/{m['FP']}/{m['TN']}"))
        rows.append(("**Aplikasi** (flag model)", with_ci(r.positive_recall_all, ci.get("positive_recall_all")),
                     with_ci(r.precision_valid, (r.precision_wilson_lo, r.precision_wilson_hi)),
                     with_ci(r.F1_valid, ci.get("F1_valid")),
                     with_ci(r.false_alarm_rate_valid, (r.false_alarm_wilson_lo, r.false_alarm_wilson_hi)),
                     with_ci(r.review_rate_all, ci.get("review_rate_all")),
                     f"{int(r.TP)}/{int(r.FN_valid)}/{int(r.FP)}/{int(r.TN)}"))
        L.append(md_table(["Pipeline", "Recall (semua positif)", "Precision", "F1", "False alarm (Normal)",
                           "Review rate", "TP/FN/FP/TN"], rows))
        L.append("\nBaris aplikasi menampilkan CI 95%: recall, F1 dan review rate dengan bootstrap "
                 f"({args.n_boot} resampel, positif dan Normal diambil terpisah); precision dan false alarm dengan "
                 "interval Wilson, karena bootstrap memberi interval selebar nol saat jumlah kejadiannya nol.")
        L.append(f"\nCoverage aplikasi: {pct(r.coverage)} ({int(r.n_valid)}/{int(r.n_total)} valid; "
                 f"{int(r.invalid_positive)} positif dan {int(r.invalid_negative)} Normal tidak valid). "
                 f"Recall dihitung atas semua positif, jadi output tidak valid dihitung sebagai terlewat.\n")
        rec = r.positive_recall_all
        if stratum == "main":
            fixed = from_counts(NOTEBOOK_REFERENCE["main"]["Notebook, flag yes/no (gated_8)"])["recall"]
            L.append(f"- Recall aplikasi {num(rec)}: {verdict(rec, ci.get('positive_recall_all'), fixed, 'notebook flag yes/no')}.")
            L.append(f"- Terhadap angka di slide: {verdict(rec, ci.get('positive_recall_all'), SLIDE_RECALL, 'angka di slide')}.")
            L.append(f"- Target operasional notebook adalah recall 0,90: "
                     f"{'tercapai' if rec >= 0.90 else 'belum tercapai'} pada titik estimasi.\n")
    L += sweep_lines
    if plot_note and sweep_lines:
        L.append(plot_note + "File `threshold_curve.csv` berisi seluruh tabel per threshold.\n")
    other = agg[agg.split != "test"]
    if len(other):
        L.append("### Split lain yang ikut dijalankan\n")
        L.append(md_table(["split", "stratum", "n", "recall (semua positif)", "precision", "false alarm", "coverage"],
                          [(r.split, r.stratum, int(r.n_total), num(r.positive_recall_all), num(r.precision_valid),
                            num(r.false_alarm_rate_valid), num(r.coverage)) for r in other.itertuples()]))
        L.append("")

    if paired_lines:
        L.append("## Perbandingan berpasangan dengan prediksi notebook\n")
        L += paired_lines
        L.append("")

    L.append("## Pemeriksaan flag selalu yes / selalu no\n")
    if yes_share_all is not None:
        L.append(f"- Di test, {pct(yes_share_all)} dari output valid ber-flag `yes`.")
    L.append("- " + ("; ".join(degenerate) if degenerate else f"Tidak ada stratum dengan {int(DEGENERATE_SHARE * 100)}% atau lebih flag yang sama."))
    L.append("- Prompt aplikasi berbunyi 'jika ragu, jawab yes', jadi false alarm pada kelas Normal adalah angka yang "
             "paling perlu dibaca di samping recall.\n")

    L.append("## Pengaruh gating orang\n")
    L.append("Aplikasi tidak memanggil model bila YOLO tidak menemukan orang; hasilnya `no` secara otomatis. "
             "Notebook memakai uniform fallback pada kasus yang sama, jadi ini perbedaan pipeline nyata, terutama "
             "untuk kelas person_optional (Arson, Explosion, RoadAccidents).\n")
    if np_note:
        L.append(md_table(["Video positif (test)", "n", "Terdeteksi", "Recall"],
                          [(a, b, c, num(d)) for a, b, c, d in np_note]))
    L.append("")

    L.append("## Output tidak valid dan penolakan\n")
    inv = df[~df.valid].invalid_reason.value_counts()
    L.append(md_table(["Alasan", "Jumlah"], [(k, int(v)) for k, v in inv.items()]) if len(inv)
             else "Semua output valid.")
    L.append("\nDetail per video ada di `invalid_outputs.csv`. Video >5 menit atau >500 MB ditolak aplikasi saat upload "
             "(`upload_rejected`) dan dihitung sebagai tidak valid, bukan dibuang. "
             "Aplikasi menandai JSON yang terpotong sebagai gagal dan tidak menyelamatkan isinya.\n")
    mism = int(scope.span_mismatch.sum())
    if mism:
        L.append(f"**Peringatan:** {mism} video punya rentang di manifest yang berbeda >2 detik dari durasi file. "
                 "Aplikasi menganalisis seluruh file, notebook menganalisis rentang manifest. Periksa kolom `span_mismatch`.\n")

    L.append("## Latensi\n")
    if latency:
        L.append(f"Waktu proses aplikasi per video: median {latency['median']:.1f} s, p90 {latency['p90']:.1f} s, "
                 f"maks {latency['max']:.1f} s (tidak termasuk waktu upload).\n")
    else:
        L.append("Tidak ada data waktu.\n")

    L.append("## Batasan yang perlu ditulis di handoff\n")
    tm = agg[(agg.split == "test") & (agg.stratum == "main")]
    size = (f"{int(tm.iloc[0].n_positive)} positif dan {int(tm.iloc[0].n_negative)} Normal pada test/main"
            if len(tm) else "jumlah video per stratum kecil")
    L += [
        f"- Benchmark kecil: {size}. Interval kepercayaan lebar; baca CI, bukan hanya titik estimasi.",
        "- Dataset UCF-Crime/UCA, bukan rekaman CCTV kantor. Hasil ini tidak menjawab kinerja di lapangan.",
        "- Perbedaan pipeline selain model: jumlah frame (sampai 150 vs 8), gating orang tanpa fallback, prompt, parsing JSON, dekode FFmpeg vs OpenCV.",
        ("- Nilai threshold notebook (0,0049) tidak bisa dipindahkan ke aplikasi: skala skor bergantung pada model, prompt dan "
         "jumlah frame. Bandingkan titik operasi (recall pada false alarm tertentu), bukan angka threshold."
         if scored_any else
         "- Run ini tidak merekam skor p(yes), jadi threshold tidak bisa dikalibrasi dari hasilnya."),
        "- Label benchmark mengikuti kelas anomali UCF (bukan penilaian kecurigaan oleh petugas).",
        "- Satu kali jalan dengan decoding greedy; variasi antar-jalan tidak diukur.",
    ]
    (out / "report.md").write_text("\n".join(L) + "\n", encoding="utf-8")
    print("\n".join(L))
    print(f"\nFiles written to {out}")


if __name__ == "__main__":
    main()