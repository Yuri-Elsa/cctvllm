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
        check("0.629" in report and "0.914" in report, "report cites both notebook reference points")
        check("upload_rejected" in report and "failed" in report, "report lists invalid reasons")

        # paired comparison against synthetic notebook predictions
        nbp = tmp / "nb.jsonl"
        with nbp.open("w", encoding="utf-8") as f:
            for r in rows:
                f.write(json.dumps({"clip_id": r["clip_id"], "predicted_suspicious": "yes" if r["expected_suspicious"] else "no",
                                    "flag_valid": True, "p_yes_binary": 0.9 if r["expected_suspicious"] else 0.001}) + "\n")
        code, log = run([str(EVAL / "analyze_app_eval.py"), "--results", str(out / "results.jsonl"), "--manifest",
                         str(manifest), "--out", str(out / "report2"), "--n-boot", "300",
                         "--notebook-predictions", str(nbp)])
        check(code == 0 and "Perbandingan berpasangan" in (out / "report2" / "report.md").read_text(encoding="utf-8"),
              "paired comparison section produced")

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