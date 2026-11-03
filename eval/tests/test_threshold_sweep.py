#!/usr/bin/env python3
"""Self-check for threshold_sweep.py and compare_runs.py on synthetic results.

Run:  python eval/tests/test_threshold_sweep.py
"""
import json
import subprocess
import sys
import tempfile
from pathlib import Path

EVAL = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(EVAL))
import threshold_sweep as ts  # noqa: E402
import compare_runs as cr  # noqa: E402


def test_functions():
    scored = [(0.9, True), (0.8, True), (0.3, True), (0.2, False), (0.1, False), (0.05, False)]
    assert ts.auc(scored) == 1.0 - 0.0 and ts.auc([(0.5, True), (0.5, False)]) == 0.5
    p = ts.point(scored, 0.3)
    assert (p["tp"], p["fp"], p["fn"], p["tn"]) == (3, 0, 0, 3), p
    pick = ts.pick_target(scored, 1.0)
    assert pick["threshold"] == 0.3 and pick["fp"] == 0, pick          # highest threshold that still catches all three
    bad = ts.pick_target([(0.1, True), (0.9, False)], 1.0)
    assert bad["threshold"] == 0.1 and bad["fp"] == 1, bad            # a hopeless score: full recall costs every false alarm
    assert cr.mcnemar_exact(0, 0) == 1.0 and abs(cr.mcnemar_exact(0, 5) - 0.0625) < 1e-9


def test_cli():
    with tempfile.TemporaryDirectory() as d:
        d = Path(d)
        rows = []
        for i in range(12):
            rows.append(dict(clip_id=f"p{i}", status="completed", stratum="main", expected_suspicious=True,
                             suspicious="yes" if i < 3 else "no", suspicious_score=0.05 + 0.07 * i))
        for i in range(8):
            rows.append(dict(clip_id=f"n{i}", status="completed", stratum="main", expected_suspicious=False,
                             suspicious="no", suspicious_score=0.02 + 0.04 * i))
        rows.append(dict(clip_id="old", status="completed", stratum="main", expected_suspicious=True, suspicious="no"))
        path = d / "results.jsonl"
        path.write_text("".join(json.dumps(r) + "\n" for r in rows), encoding="utf-8")
        out = subprocess.run([sys.executable, str(EVAL / "threshold_sweep.py"), "--results", str(path),
                              "--out", str(d / "o")], capture_output=True, text=True)
        assert out.returncode == 0, out.stderr
        assert "20 clips with a score" in out.stdout and "1 completed clips without one" in out.stdout, out.stdout
        assert "AUC" in out.stdout and "Cross-validated" in out.stdout
        assert (d / "o" / "threshold_curve.csv").exists()
        none = d / "none.jsonl"
        none.write_text(json.dumps(rows[-1]) + "\n", encoding="utf-8")
        bad = subprocess.run([sys.executable, str(EVAL / "threshold_sweep.py"), "--results", str(none)], capture_output=True, text=True)
        assert bad.returncode != 0 and "token probabilities" in (bad.stderr + bad.stdout)


if __name__ == "__main__":
    test_functions()
    test_cli()
    print("OK")