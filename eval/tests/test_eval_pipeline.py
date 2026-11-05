#!/usr/bin/env python3
"""End-to-end self-check: mock app -> run_app_eval.py -> analyze_app_eval.py.

Run:  python eval/tests/test_eval_pipeline.py
Verifies upload/poll, rejection and failure handling, resume, and that the reported metrics match an
independent recomputation from the raw results.
"""
import csv
import json
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
EVAL = HERE.parent
sys.path.insert(0, str(HERE))
import mock_app_server as mock  # noqa: E402

CLASSES = ["Abuse", "Arrest", "Fighting", "Robbery", "Arson", "Explosion"]


def build(tmp):
    videos = tmp / "videos"
    videos.mkdir()
    rows = []
    for split, n_pos, n_norm in (("test", 6, 14), ("calibration", 2, 6)):
        for i in range(n_pos * len(CLASSES)):
            cls = CLASSES[i % len(CLASSES)]
            name = f"{cls.lower()}{split[:1]}{i:03d}x264"
            if split == "test" and i == 0:
                name = "abuse_toolong_x264"
            if split == "test" and i == 1:
                name = "arrest_broken_x264"
            if split == "test" and i == 2:
                name = "fighting_noperson_x264"
            rows.append((name, cls, split, True))
        for i in range(n_norm):
            rows.append((f"normalvideos{split[:1]}{i:03d}x264", "Normal", split, False))
    manifest = tmp / "manifest.csv"
    with manifest.open("w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(["clip_id", "relative_path", "class_name", "evaluation_split", "expected_suspicious",
                    "person_optional", "stratum", "start_sec", "end_sec"])
        for name, cls, split, pos in rows:
            (videos / f"{name}.mp4").write_bytes(b"fake video " + name.encode())
            opt = cls in ("Arson", "Explosion")
            w.writerow([name, f"{name}.mp4", cls, split, pos, opt, "person_optional" if opt else "main", 0, 30])
    return manifest, videos


def run(cmd):
    p = subprocess.run([sys.executable] + cmd, capture_output=True, text=True, encoding="utf-8", errors="replace")
    return p.returncode, p.stdout + p.stderr


def check(cond, msg):
    print(("PASS  " if cond else "FAIL  ") + msg)
    if not cond:
        raise SystemExit(1)


def main():
    server, state = mock.serve(0, delay=0.02)
    base = f"http://127.0.0.1:{server.server_address[1]}"
    with tempfile.TemporaryDirectory() as t:
        tmp = Path(t)
        manifest, videos = build(tmp)
        out = tmp / "run"
        common = ["--manifest", str(manifest), "--video-root", str(videos), "--out", str(out), "--base-url", base,
                  "--poll", "0.05"]
        settings = tmp / "settings.json"
        settings.write_text(json.dumps({**mock.SETTINGS, "model": "pinned-model"}), encoding="utf-8")

        code, log = run([str(EVAL / "run_app_eval.py"), *common, "--settings", str(settings), "--limit", "10"])
        check(code == 0, f"first partial run exits 0 ({log[-200:] if code else 'ok'})")
        n1 = len((out / "results.jsonl").read_text(encoding="utf-8").splitlines())
        check(n1 == 10, f"partial run recorded 10 clips (got {n1})")
        check(json.loads((out / "run.json").read_text(encoding="utf-8"))["settings"]["model"] == "pinned-model", "settings pinned in run.json")

        code, log = run([str(EVAL / "run_app_eval.py"), *common, "--settings", str(settings)])
        check(code == 0, "resume run exits 0")
        rows = [json.loads(x) for x in (out / "results.jsonl").read_text(encoding="utf-8").splitlines()]
        ids = [r["clip_id"] for r in rows]
        check(len(ids) == len(set(ids)), "no duplicate clips after resume")
        test_total = sum(1 for r in csv.DictReader(manifest.open(encoding="utf-8")) if r["evaluation_split"] == "test")
        check(len(ids) == test_total, f"all {test_total} test clips recorded (got {len(ids)})")
        by = {r["clip_id"]: r for r in rows}
        check(by["abuse_toolong_x264"]["status"] == "upload_rejected", "over-length video recorded as upload_rejected")
        check(by["arrest_broken_x264"]["status"] == "failed", "failed job recorded as failed")
        check(by["fighting_noperson_x264"]["no_people"] is True, "noPeople captured")
        check(state.upload_count == test_total - 1, "rejected video not counted by the app, others uploaded once")

        code, log = run([str(EVAL / "analyze_app_eval.py"), "--results", str(out / "results.jsonl"), "--manifest",
                         str(manifest), "--out", str(out / "report"), "--n-boot", "300"])
        check(code == 0, f"analysis exits 0 ({log[-300:] if code else 'ok'})")

        # independent recomputation of the main stratum
        main_rows = [r for r in rows if r["stratum"] == "main"]
        pos = [r for r in main_rows if r["expected_suspicious"]]
        neg = [r for r in main_rows if not r["expected_suspicious"]]
        yes = lambda r: r["status"] == "completed" and r.get("suspicious") == "yes"
        tp = sum(yes(r) for r in pos)
        fp = sum(yes(r) for r in neg)
        import pandas as pd
        m = pd.read_csv(out / "report" / "metrics_by_split_stratum.csv")
        row = m[(m.split == "test") & (m.stratum == "main")].iloc[0]
        check(int(row.n_total) == len(main_rows), "n_total matches")
        check(int(row.TP) == tp and int(row.FP) == fp, f"TP/FP match independent count (TP={tp}, FP={fp})")
        check(abs(row.positive_recall_all - tp / len(pos)) < 1e-9, "recall_all = TP / all positives (invalid counted as miss)")
        check(int(row.invalid_positive) == 2, "toolong + broken positives counted as invalid_positive")
        check(row.recall_wilson_lo < row.positive_recall_all < row.recall_wilson_hi, "Wilson interval brackets recall")
        check(row.positive_recall_all_ci_lo <= row.positive_recall_all <= row.positive_recall_all_ci_hi,
              "bootstrap interval brackets recall")
        report = (out / "report" / "report.md").read_text(encoding="utf-8")
        # this run is far smaller than the notebook's 91-clip test/main, so the hard-coded notebook rows are not comparable
        check("Notebook, flag yes/no" not in report and "Notebook, threshold" not in report,
              "hard-coded full-set notebook rows are left out of a smaller run")
        check("Baris notebook dihilangkan" in report and "--notebook-predictions" in report,
              "report says why the notebook rows are missing and how to get them")
        check("upload_rejected" in report and "failed" in report, "report lists invalid reasons")

        # ---- threshold sweep over the recorded p(yes)
        scored = [r for r in rows if r.get("suspicious_score") is not None]
        check(len(scored) > 0 and all(r["score_source"] == "logprobs" for r in scored), "suspicious_score recorded from the screening log")
        check(by["fighting_noperson_x264"].get("suspicious_score") is None, "no score for a video the model never saw")
        check(by["arrest_broken_x264"].get("suspicious_score") is None, "no score for a failed job")
        check(all(r["suspicious_threshold"] == 0.5 for r in scored), "app threshold recorded next to each score")
        rep_dir = out / "report"
        check((rep_dir / "threshold_curve.csv").exists(), "threshold_curve.csv written")
        cv = pd.read_csv(rep_dir / "threshold_curve.csv")
        ct = cv[(cv.split == "test") & (cv.stratum == "main")].sort_values("threshold")
        check(ct.recall_all.is_monotonic_decreasing and ct.false_alarm_rate.is_monotonic_decreasing,
              "recall and false-alarm rate never rise as the threshold rises")
        check(ct.iloc[0].recall_all >= ct.iloc[-1].recall_all and ct.iloc[-1].TP == 0 and ct.iloc[-1].FP == 0,
              "highest threshold flags nothing")
        # Flagging at the lowest score among 'yes' videos must reproduce the app's own flags exactly.
        yes_scores = [r["suspicious_score"] for r in main_rows if yes(r) and r.get("suspicious_score") is not None]
        at_app = ct[(ct.threshold - min(yes_scores)).abs() < 1e-12].iloc[0]
        check(int(at_app.TP) == tp and int(at_app.FP) == fp, f"sweep at the app's own cut reproduces TP/FP ({tp}/{fp})")
        check("Kurva threshold" in report and "OPTIMISTIS" in report and "AUC" in report,
              "report has the sweep, AUC and flags the test-chosen point as optimistic")
        check("Run ini tidak menyimpan skor" not in report, "no 'scores missing' notice when scores exist")

        # frozen threshold selected on the calibration split
        out2 = tmp / "run_cal"
        common2 = [a if a != str(out) else str(out2) for a in common]
        code, log = run([str(EVAL / "run_app_eval.py"), *common2, "--settings", str(settings), "--split", "calibration,test"])
        check(code == 0, f"calibration+test run exits 0 ({log[-200:] if code else 'ok'})")
        code, log = run([str(EVAL / "analyze_app_eval.py"), "--results", str(out2 / "results.jsonl"), "--manifest",
                         str(manifest), "--out", str(out2 / "report"), "--n-boot", "300", "--threshold", "0.6"])
        check(code == 0, f"analysis with calibration exits 0 ({log[-300:] if code else 'ok'})")
        rep2 = (out2 / "report" / "report.md").read_text(encoding="utf-8")
        check("Dipilih di calibration" in rep2 and "OPTIMISTIS" not in rep2, "threshold chosen on calibration, then frozen")
        check("Ditetapkan (--threshold)" in rep2, "explicit --threshold reported")
        check("Split calibration kecil" in rep2, "small calibration split is warned about")
        cv2 = pd.read_csv(out2 / "report" / "threshold_curve.csv")
        check(set(cv2.split) == {"calibration", "test"}, "curve written for both splits")

        # a run without recorded scores (older results) still analyses, and says so
        old_rows = tmp / "old_results.jsonl"
        with old_rows.open("w", encoding="utf-8") as f:
            for r in rows:
                f.write(json.dumps({k: v for k, v in r.items() if not k.startswith(("suspicious_", "score_", "person_"))}) + "\n")
        code, log = run([str(EVAL / "analyze_app_eval.py"), "--results", str(old_rows), "--manifest", str(manifest),
                         "--out", str(tmp / "old_report"), "--n-boot", "300", "--run-json", str(out / "run.json")])
        old_rep = (tmp / "old_report" / "report.md").read_text(encoding="utf-8") if code == 0 else ""
        check(code == 0 and "Run ini tidak menyimpan skor" in old_rep and "Kurva threshold" not in old_rep
              and not (tmp / "old_report" / "threshold_curve.csv").exists(),
              "results without scores analyse as before, with a notice and no curve")

        # paired comparison against synthetic notebook predictions
        nbp = tmp / "nb.jsonl"
        with nbp.open("w", encoding="utf-8") as f:
            for r in rows:
                f.write(json.dumps({"clip_id": r["clip_id"], "predicted_suspicious": "yes" if r["expected_suspicious"] else "no",
                                    "flag_valid": True, "p_yes_binary": 0.9 if r["expected_suspicious"] else 0.001}) + "\n")
        code, log = run([str(EVAL / "analyze_app_eval.py"), "--results", str(out / "results.jsonl"), "--manifest",
                         str(manifest), "--out", str(out / "report2"), "--n-boot", "300",
                         "--notebook-predictions", str(nbp)])
        rep_nb = (out / "report2" / "report.md").read_text(encoding="utf-8")
        check(code == 0 and "Perbandingan berpasangan" in rep_nb, "paired comparison section produced")

        # notebook rows are recomputed on the same clips (here every clip) from per-video predictions
        n_main_test = len(main_rows)
        nb_by = {r["clip_id"]: r for r in rows}
        check(f"Notebook, flag yes/no (gated_8; {n_main_test} klip yang sama)" in rep_nb
              and f"Notebook, threshold 0.0049 ({n_main_test} klip yang sama)" in rep_nb,
              "notebook rows are labelled with the number of shared clips")
        n_pos_main = sum(1 for r in main_rows if r["expected_suspicious"])
        n_neg_main = n_main_test - n_pos_main
        check(f"{n_pos_main}/0/0/{n_neg_main}" in rep_nb,
              f"perfect synthetic notebook gives TP/FN/FP/TN = {n_pos_main}/0/0/{n_neg_main} on the same clips")

        # a notebook that only ran some of the app's clips: rows and the app row are restricted to the overlap
        keep = {r["clip_id"] for r in main_rows[:12]}
        nb_part = tmp / "nb_part.jsonl"
        with nb_part.open("w", encoding="utf-8") as f:
            for line in nbp.read_text(encoding="utf-8").splitlines():
                if json.loads(line)["clip_id"] in keep:
                    f.write(line + "\n")
        code, log = run([str(EVAL / "analyze_app_eval.py"), "--results", str(out / "results.jsonl"), "--manifest",
                         str(manifest), "--out", str(out / "report3"), "--n-boot", "300",
                         "--notebook-predictions", str(nb_part)])
        rep_part = (out / "report3" / "report.md").read_text(encoding="utf-8") if code == 0 else log
        sub_main = [r for r in main_rows if r["clip_id"] in keep]
        check(code == 0 and f"({len(sub_main)} klip yang sama)" in rep_part and "dibatasi ke irisan" in rep_part,
              f"partial notebook coverage limits both sides to the {len(sub_main)} shared clips")
        check("Notebook, flag yes/no (gated_8)" not in rep_part and "(set penuh)" not in rep_part,
              "hard-coded rows never mix into a per-video comparison")

        # unit check of the reference chooser: full-size group keeps the hard-coded numbers, others do not
        import pandas as pd
        sys.path.insert(0, str(EVAL))
        import analyze_app_eval as aae  # noqa: E402
        def grp(n, pos):
            return pd.DataFrame({"clip_id": [f"c{i}" for i in range(n)], "expected_suspicious": [i < pos for i in range(n)]})
        full = aae.notebook_reference("main", grp(91, 70), None, "gated_8", 0.0049)
        check(full.source == "bawaan" and len(full.rows) == 4 and full.note is None, "full 91/70 test/main uses the hard-coded rows")
        check(abs(aae.from_counts(full.thr)["recall"] - 64 / 70) < 1e-12, "hard-coded threshold row recall is 64/70")
        small = aae.notebook_reference("main", grp(38, 30), None, "gated_8", 0.0049)
        check(small.rows == [] and small.note and "38 klip" in small.note, "38-clip group gets no hard-coded rows")
        same_n = aae.notebook_reference("main", grp(91, 60), None, "gated_8", 0.0049)
        check(same_n.rows == [], "91 clips with a different positive count are not treated as the notebook set")

        # incomplete results must be refused
        part = tmp / "partial.jsonl"
        part.write_text("\n".join((out / "results.jsonl").read_text(encoding="utf-8").splitlines()[:5]) + "\n", encoding="utf-8")
        code, log = run([str(EVAL / "analyze_app_eval.py"), "--results", str(part), "--manifest", str(manifest),
                         "--out", str(tmp / "x"), "--run-json", str(out / "run.json")])
        check(code != 0 and "no result" in log, "analysis refuses an incomplete run")

        # resuming with different settings must be refused
        changed = tmp / "settings2.json"
        changed.write_text(json.dumps({**mock.SETTINGS, "model": "another-model"}), encoding="utf-8")
        code, log = run([str(EVAL / "run_app_eval.py"), *common, "--settings", str(changed)])
        check(code != 0 and "settings_hash differs" in log, "resume with different settings is refused")
    print("\nAll checks passed.")


if __name__ == "__main__":
    main()