#!/usr/bin/env python3
"""Tests for the recall follow-ups: split classification, calibrated threshold, fair subset, scene mode.

Run:  python eval/tests/test_recall_followups.py

Three kinds of check:
  1. The numbers quoted in the write-up still reproduce from the committed runs (so nobody edits a run and the text
     silently goes stale).
  2. The helper scripts behave (make_subset.py, threshold_sweep.py, compare_runs.py) on small synthetic inputs.
  3. Checks on the NEW Gemma run (eval/runs/gemma_split/results.jsonl). They are skipped until that run exists and
     then guard the fairness of the comparison: same 47 clips, same settings, scene mode actually used.
Standard library only; no model server needed.
"""
import csv
import json
import subprocess
import sys
import tempfile
from pathlib import Path

EVAL = Path(__file__).resolve().parent.parent
RUNS = EVAL / "runs"
sys.path.insert(0, str(EVAL))
import threshold_sweep as ts  # noqa: E402

NEW_RUN = RUNS / "gemma_split" / "results.jsonl"


# ---------------------------------------------------------------- helpers
def read_jsonl(path):
    return [json.loads(l) for l in Path(path).read_text(encoding="utf-8").splitlines() if l.strip()]


def read_manifest(path):
    with open(path, newline="", encoding="utf-8") as f:
        return list(csv.DictReader(f))


def is_true(v):
    return str(v).strip().lower() == "true"


def recall(rows, manifest_rows, stratum=None):
    """Recall over the manifest's positive clips. A clip the run did not complete counts as NOT flagged,
    the same rule analyze_app_eval.py and compare_runs.py use."""
    by_id = {r["clip_id"]: r for r in rows}
    pos = [m for m in manifest_rows if is_true(m["expected_suspicious"]) and (stratum is None or m["stratum"] == stratum)]
    hit = sum(1 for m in pos if by_id.get(m["clip_id"], {}).get("status") == "completed"
              and by_id[m["clip_id"]].get("suspicious") == "yes")
    return hit, len(pos)


def run_cli(script, *args):
    return subprocess.run([sys.executable, str(EVAL / script), *map(str, args)], capture_output=True, text=True)


def check(cond, msg):
    assert cond, msg
    print("PASS ", msg)


# ---------------------------------------------------------------- 1. the numbers in the write-up
def test_reported_numbers_reproduce():
    full = read_manifest(EVAL / "manifest.csv")
    test_main = [m for m in full if m["evaluation_split"] == "test" and m["stratum"] == "main"]
    check(len(test_main) == 91, "the 'Gemma on 91 test clips' set is the test/main stratum of the manifest")

    gemma = read_jsonl(RUNS / "app_16f" / "results.jsonl")
    hit, pos = recall(gemma, test_main)
    check((hit, pos) == (8, 70) and round(hit / pos, 3) == 0.114,
          f"Gemma recall 0.114 = {hit}/{pos} (clips the app rejected for size/length count as missed)")

    completed = [r for r in gemma if r["status"] == "completed"]
    yes = sum(r["suspicious"] == "yes" for r in completed)
    check(len(completed) == 101 and yes == 16 and round(yes / len(completed), 3) == 0.158,
          f"Gemma flags {yes}/{len(completed)} = 15.8% of completed clips as yes (main + person_optional)")

    sub = read_manifest(RUNS / "subset47" / "subset_manifest.csv")
    sub_main = [m for m in sub if m["stratum"] == "main"]
    check(len(sub_main) == 38, "the 'Qwen on 38 clips' set is the main stratum of the 47-clip subset")
    for name, expect in (("qwen4b_instruct_16f", 20), ("qwen4b_instruct_8f", 14)):
        rows = read_jsonl(RUNS / name / "results.jsonl")
        hit, pos = recall(rows, sub_main)
        check((hit, pos) == (expect, 30), f"{name}: recall {hit}/{pos} = {hit / pos:.3f} on the 38 main clips")

    base = read_jsonl(RUNS / "subset47" / "baseline_subset.jsonl")
    hit, pos = recall(base, sub_main)
    check((hit, pos) == (4, 30),
          f"Gemma on the SAME 38 clips: {hit}/{pos} = {hit / pos:.3f}; this, not 0.114, is the number to put beside Qwen")


# ---------------------------------------------------------------- 2. subset 47 is a fair basis
def test_subset47_is_a_fair_basis():
    sub = read_manifest(RUNS / "subset47" / "subset_manifest.csv")
    ids = {m["clip_id"] for m in sub}
    check(len(sub) == 47 and len(ids) == 47, "subset47 has 47 distinct clips")
    check({m["evaluation_split"] for m in sub} == {"test"}, "every subset47 clip is in the test split (none from calibration)")
    check(sum(is_true(m["expected_suspicious"]) for m in sub) == 39, "subset47 = 39 anomaly + 8 Normal")
    for name, path in (("gemma baseline", RUNS / "subset47" / "baseline_subset.jsonl"),
                       ("qwen 16f", RUNS / "qwen4b_instruct_16f" / "results.jsonl"),
                       ("qwen 8f", RUNS / "qwen4b_instruct_8f" / "results.jsonl")):
        rows = read_jsonl(path)
        check({r["clip_id"] for r in rows} == ids and all(r["status"] == "completed" for r in rows),
              f"{name} covers exactly the 47 subset clips, all completed")


# ---------------------------------------------------------------- 3. scenes without people: the evidence for scene mode
def test_no_person_clips_never_reach_the_model_in_the_baseline():
    gemma = read_jsonl(RUNS / "app_16f" / "results.jsonl")
    no_people = [r for r in gemma if r["status"] == "completed" and r.get("no_people")]
    positives = [r for r in no_people if r["expected_suspicious"]]
    check(len(positives) == 3, f"exactly 3 positive clips had no person in the baseline run (found {len(positives)})")
    check(all(r["stratum"] == "person_optional" for r in positives), "those 3 are all person_optional (Arson/Explosion/RoadAccidents)")
    check(all(r["suspicious"] == "no" and r["n_frames"] == 0 for r in positives),
          "they were answered 'no' with 0 frames sent: recall on them is 0 by construction")


# ---------------------------------------------------------------- 4. make_subset.py
def _write_manifest(path, rows):
    with open(path, "w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=["clip_id", "class_name", "evaluation_split", "stratum", "expected_suspicious"])
        w.writeheader()
        w.writerows(rows)


def test_make_subset():
    with tempfile.TemporaryDirectory() as d:
        d = Path(d)
        rows, base = [], []
        for cls in ("Abuse", "Arson", "Burglary"):
            for i in range(6):
                rows.append(dict(clip_id=f"{cls}{i}", class_name=cls, evaluation_split="test", stratum="main", expected_suspicious="True"))
        for i in range(14):
            rows.append(dict(clip_id=f"Normal{i}", class_name="Normal", evaluation_split="test", stratum="main", expected_suspicious="False"))
        for i in range(5):   # calibration clips must never be picked
            rows.append(dict(clip_id=f"Cal{i}", class_name="Abuse", evaluation_split="calibration", stratum="main", expected_suspicious="True"))
        rejected = {"Abuse0", "Abuse1", "Normal0"}
        for r in rows:
            base.append(dict(clip_id=r["clip_id"], status="upload_rejected" if r["clip_id"] in rejected else "completed", suspicious="no"))
        _write_manifest(d / "manifest.csv", rows)
        (d / "base.jsonl").write_text("".join(json.dumps(b) + "\n" for b in base), encoding="utf-8")

        def pick(out):
            r = run_cli("make_subset.py", "--manifest", d / "manifest.csv", "--baseline-results", d / "base.jsonl",
                        "--out-dir", d / out, "--per-class", 2, "--normal", 3)
            assert r.returncode == 0, r.stderr
            return read_manifest(d / out / "subset_manifest.csv"), read_jsonl(d / out / "baseline_subset.jsonl")

        a, a_base = pick("a")
        b, _ = pick("b")
        ids = [m["clip_id"] for m in a]
        check(len(a) == 9 and sum(is_true(m["expected_suspicious"]) for m in a) == 6, "2 per anomaly class x 3 + 3 Normal = 9 clips (6 anomaly)")
        check(ids == [m["clip_id"] for m in b], "same seed gives the same clips (deterministic)")
        check(not rejected & set(ids), "clips the app rejected in the baseline are never picked")
        check(all(m["evaluation_split"] == "test" for m in a), "only the test split is used")
        check([r["clip_id"] for r in a_base] == sorted(ids), "baseline_subset.jsonl holds the baseline rows of exactly those clips")
        c = run_cli("make_subset.py", "--manifest", d / "manifest.csv", "--baseline-results", d / "base.jsonl",
                    "--out-dir", d / "c", "--per-class", 2, "--normal", 3, "--seed", 1)
        other = [m["clip_id"] for m in read_manifest(d / "c" / "subset_manifest.csv")]
        check(c.returncode == 0 and other != ids, "a different seed gives a different pick")


# ---------------------------------------------------------------- 5. threshold calibration (the 'model ignores if in doubt, yes' problem)
def _doubtful_model():
    """A model that is timid: it answers 'no' to most positives, but its p(yes) is still higher on positives."""
    rows = []
    for i in range(20):   # positives: p(yes) 0.20 .. 0.58, only the top few reach 0.5
        rows.append(dict(clip_id=f"p{i}", status="completed", stratum="main", expected_suspicious=True,
                         suspicious_score=round(0.20 + 0.02 * i, 3), classifier_score=round(0.20 + 0.02 * i, 3)))
    for i in range(20):   # Normal: p(yes) 0.02 .. 0.21
        rows.append(dict(clip_id=f"n{i}", status="completed", stratum="main", expected_suspicious=False,
                         suspicious_score=round(0.02 + 0.01 * i, 3), classifier_score=round(0.02 + 0.01 * i, 3)))
    return rows


def test_lower_threshold_recovers_recall_the_default_loses():
    rows = _doubtful_model()
    scored = [(r["classifier_score"], r["expected_suspicious"]) for r in rows]
    default = ts.point(scored, 0.5)
    check(default["recall"] < 0.4, f"at the app default 0.5 the timid model catches only {default['recall']:.2f} of positives")
    pick = ts.pick_target(scored, 0.9)
    check(pick["threshold"] < 0.5 and pick["recall"] >= 0.9,
          f"a calibrated threshold ({pick['threshold']:.3f}) reaches recall {pick['recall']:.2f} at false-alarm {pick['fpr']:.2f}")
    check(ts.auc(scored) > 0.9, "the score still separates the classes (AUC > 0.9), so re-thresholding is worth doing")
    # the honest number: threshold picked on other folds
    held, thresholds = ts.cross_validate(scored, 0.9, 5, 20260929)
    check(held["tp"] + held["fn"] == 20 and held["fp"] + held["tn"] == 20, "cross-validation holds out every clip exactly once")
    check(all(t < 0.5 for t in thresholds), "every fold picks a threshold below the 0.5 default")


def test_sweep_cli_score_field_and_old_runs():
    with tempfile.TemporaryDirectory() as d:
        d = Path(d)
        rows = _doubtful_model()
        rows.append(dict(clip_id="old", status="completed", stratum="main", expected_suspicious=True, suspicious="no"))   # run from before scores existed
        rows.append(dict(clip_id="scene", status="completed", stratum="person_optional", expected_suspicious=True, suspicious_score=0.9, classifier_score=0.9))
        (d / "r.jsonl").write_text("".join(json.dumps(r) + "\n" for r in rows), encoding="utf-8")

        r = run_cli("threshold_sweep.py", "--results", d / "r.jsonl", "--score-field", "classifier_score", "--stratum", "main", "--out", d / "o")
        check(r.returncode == 0 and "40 clips with a score" in r.stdout and "1 completed clips without one" in r.stdout,
              "--score-field classifier_score with --stratum main uses the 40 scored main clips and reports the one without a score")
        check((d / "o" / "threshold_curve.csv").exists(), "threshold_curve.csv is written")
        r = run_cli("threshold_sweep.py", "--results", d / "r.jsonl", "--score-field", "does_not_exist")
        check(r.returncode != 0 and "token probabilities" in r.stdout + r.stderr, "a missing score field stops with a message instead of a wrong curve")


def test_committed_runs_cannot_be_rethresholded():
    """None of the committed Gemma/Qwen runs recorded p(yes): the calibration experiment needs a NEW run."""
    for name in ("app_16f", "qwen4b_instruct_16f", "qwen4b_instruct_8f"):
        rows = [r for r in read_jsonl(RUNS / name / "results.jsonl") if r["status"] == "completed"]
        check(all(r.get("suspicious_score") is None for r in rows), f"{name}: no clip has suspicious_score (nothing to sweep)")
    r = run_cli("threshold_sweep.py", "--results", RUNS / "app_16f" / "results.jsonl")
    check(r.returncode != 0, "threshold_sweep.py refuses a run without scores")


# ---------------------------------------------------------------- 6. compare_runs.py: the fair comparison
def test_compare_runs_on_the_47_clip_subset():
    r = run_cli("compare_runs.py", "--manifest", RUNS / "subset47" / "subset_manifest.csv", "--stratum", "main",
                f"gemma={RUNS / 'subset47' / 'baseline_subset.jsonl'}",
                f"qwen16={RUNS / 'qwen4b_instruct_16f' / 'results.jsonl'}")
    out = r.stdout + r.stderr
    check(r.returncode == 0, "compare_runs.py runs on the committed subset")
    check("4/30" in out and "20/30" in out, "it reports Gemma 4/30 and Qwen 20/30 on the same 30 positives")


def test_compare_runs_counts_a_missing_clip_as_not_flagged():
    with tempfile.TemporaryDirectory() as d:
        d = Path(d)
        _write_manifest(d / "m.csv", [dict(clip_id=f"p{i}", class_name="A", evaluation_split="test", stratum="main", expected_suspicious="True") for i in range(4)])
        full = [dict(clip_id=f"p{i}", status="completed", stratum="main", expected_suspicious=True, suspicious="yes") for i in range(4)]
        (d / "ref.jsonl").write_text("".join(json.dumps(x) + "\n" for x in full), encoding="utf-8")
        (d / "short.jsonl").write_text("".join(json.dumps(x) + "\n" for x in full[:3]), encoding="utf-8")   # the 4th clip never ran
        r = run_cli("compare_runs.py", "--manifest", d / "m.csv", f"ref={d / 'ref.jsonl'}", f"short={d / 'short.jsonl'}")
        check(r.returncode == 0 and "4/4" in r.stdout and "3/4" in r.stdout,
              "a run that skipped a clip is scored 3/4, not 3/3: an incomplete Gemma run cannot look better than it is")


# ---------------------------------------------------------------- 7. the NEW Gemma run (skipped until it exists)
def test_new_gemma_run():
    if not NEW_RUN.exists():
        print(f"SKIP  new Gemma run not found at {NEW_RUN.relative_to(EVAL.parent)}; these checks start once it exists")
        return
    sub = read_manifest(RUNS / "subset47" / "subset_manifest.csv")
    ids = {m["clip_id"] for m in sub}
    rows = read_jsonl(NEW_RUN)
    done = {r["clip_id"] for r in rows if r["status"] == "completed"}
    check(done == ids, f"the new run completed all 47 subset clips (missing: {sorted(ids - done)[:5]}, extra: {sorted(done - ids)[:5]})")
    check(len({r["settings_hash"] for r in rows}) == 1, "one settings hash across the whole run (no clip was run with other settings)")
    check({r.get("model") for r in rows if r.get("model") and r["model"] != "Person detection only"} == {"gemma4:e2b-it-qat"},
          "the model is the same Gemma as the baseline, so only the settings differ")
    scored = [r for r in rows if r["status"] == "completed" and r.get("classifier_score") is not None]
    check(len(scored) > 0, "the separate classifier call recorded classifier_score (split classification really ran)")
    check(all(r.get("score_source") == "logprobs" for r in scored), "scores come from logprobs, so threshold_sweep.py can use them")

    base = {r["clip_id"]: r for r in read_jsonl(RUNS / "subset47" / "baseline_subset.jsonl")}
    was_empty = [c for c, r in base.items() if r.get("no_people")]
    new = {r["clip_id"]: r for r in rows}
    for c in was_empty:
        r = new[c]
        check(r.get("scene_mode") is True and not r.get("no_people") and 0 < r["n_frames"] <= 8,
              f"{c}: had no person in the baseline; the new run used scene mode with {r['n_frames']} frame(s)")


def main():
    tests = [v for k, v in sorted(globals().items()) if k.startswith("test_") and callable(v)]
    for t in tests:
        print(f"\n== {t.__name__}")
        t()
    print("\nAll checks passed.")


if __name__ == "__main__":
    main()
