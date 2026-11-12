#!/usr/bin/env python3
"""Self-check for agent_eval.py / agent_review.py (no model needed; uses the keyword mock agent).

Run:  python eval/tests/test_agent_eval.py
Covers: scenarios from real VLM results (TP / FP / FN bypass), --models comparison, consistency at
temperature > 0, resume isolation between temperatures, and the two-reviewer review/merge flow.
"""
import csv
import json
import subprocess
import sys
import tempfile
from pathlib import Path

EVAL = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(EVAL))
import agent_eval as ae  # noqa: E402
import agent_review as rv  # noqa: E402


def run(*a, ok=(0,)):
    p = subprocess.run([sys.executable, str(EVAL / a[0]), *a[1:]], capture_output=True, text=True, encoding="utf-8")
    assert p.returncode in ok, (a, p.returncode, p.stdout[-800:], p.stderr[-800:])
    return p


def write_results(path):
    base = {"status": "completed", "no_people": False, "n_frames": 8, "model": "qwen3-vl:4b-instruct"}
    rows = [
        dict(base, clip_id="tp1", class_name="Robbery", expected_suspicious=True, suspicious="yes", weapon="no",
             description="Dua orang memaksa membuka pintu kasir dan mengambil uang."),
        dict(base, clip_id="tp2", class_name="Abuse", expected_suspicious=True, suspicious="no", weapon="yes",
             description="Seorang pria mengacungkan pisau ke petugas."),
        dict(base, clip_id="fp1", class_name="Normal", expected_suspicious=False, suspicious="yes", weapon="no",
             description="Petugas kebersihan mengepel lantai lobi, tidak jelas apakah ada hal lain."),
        dict(base, clip_id="fn1", class_name="Stealing", expected_suspicious=True, suspicious="no", weapon="no",
             description="Orang berdiri diam di dekat rak."),
        dict(base, clip_id="tn1", class_name="Normal", expected_suspicious=False, suspicious="no", weapon="no",
             description="Orang berjalan."),
        dict(base, clip_id="rej", class_name="Normal", expected_suspicious=False, status="upload_rejected", suspicious=None,
             description=""),
        dict(base, clip_id="np", class_name="Normal", expected_suspicious=False, no_people=True, suspicious="no", description="x"),
    ]
    path.write_text("\n".join(json.dumps(r) for r in rows), encoding="utf-8")


def main():
    with tempfile.TemporaryDirectory() as td:
        tmp = Path(td)
        res = tmp / "results.jsonl"
        write_results(res)

        # 0. bahasa keluaran: prompt mewajibkan Indonesia; heuristik menangkap kasus R06
        assert "wajib berbahasa Indonesia" in ae.load_system_prompt(EVAL.parent / "Infrastructure/Services/NotificationAgent.cs")
        assert ae.looks_english("A suspicious activity was detected with a score above the threshold, requiring immediate review.")
        assert not ae.looks_english("Kejadian ini memiliki skor di atas ambang, sehingga memerlukan peninjauan segera.")

        # 1. skenario dari keluaran VLM
        sc, meta = ae.scenarios_from_results(res, {})
        ids = {s["id"]: s for s in sc}
        assert set(ids) == {"V-tp1", "V-tp2", "V-fp1"}, ids.keys()          # tp2 ditandai lewat weapon=yes
        assert ids["V-tp1"]["category"] == "vlm_tp" and ids["V-fp1"]["category"] == "vlm_fp"
        assert [b["clip_id"] for b in meta["bypass"]] == ["fn1"], meta["bypass"]   # FN tidak sampai ke agen
        assert meta["skipped"] == {"upload_rejected": 1, "no_people": 1}, meta["skipped"]
        assert ids["V-tp1"]["score"] is None and "class_name" not in ids["V-tp1"]["desc"]
        assert "ignore" in ids["V-fp1"]["expect_actions"] and "ignore" not in ids["V-tp1"]["expect_actions"]
        assert ae.facts(ids["V-tp1"], ae.allowed_actions(ids["V-tp1"])).count("null") >= 1   # skor null, seperti app tanpa logprobs

        # 2. end-to-end lewat CLI + deteksi model produksi
        out = tmp / "e2e"
        p = run("agent_eval.py", "--mock", "--from-results", str(res), "--out", str(out))
        rep = (out / "agent_report.md").read_text(encoding="utf-8")
        assert "False negative VLM" in rep and "fn1" in rep and "qwen3-vl:4b-instruct" in rep
        e = json.loads((out / "agent_summary.json").read_text(encoding="utf-8"))["e2e"]
        assert e["tp"]["n"] == 2 and e["fp"]["n"] == 1

        # 3. perbandingan model + peringatan model berbeda dari produksi
        cmp_out = tmp / "cmp"
        p = run("agent_eval.py", "--mock", "--from-results", str(res), "--models", "gemma4:e2b-it-qat", "qwen3-vl:4b-instruct",
                "--out", str(cmp_out))
        cmp_md = (cmp_out / "agent_compare.md").read_text(encoding="utf-8")
        assert "gemma4:e2b-it-qat" in cmp_md and "Kesepakatan keputusan antar model" in cmp_md
        assert (cmp_out / "gemma4_e2b-it-qat" / "agent_report.md").exists()
        assert "| Model produksi? | tidak | ya |" in cmp_md, cmp_md
        p = run("agent_eval.py", "--from-results", str(res), "--models", "other-model", "--endpoint", "http://127.0.0.1:9/v1",
                "--timeout", "1", "--out", str(tmp / "warn"))
        assert "berbeda dari model deskripsi" in p.stderr and (tmp / "warn" / "agent_report.md").exists()
        assert "BERBEDA dari model VLM" in (tmp / "warn" / "agent_report.md").read_text(encoding="utf-8")

        # 4. konsistensi dan isolasi resume antar temperature
        st = tmp / "stab"
        run("agent_eval.py", "--mock", "--repeats", "5", "--temperature", "0.4", "--category", "ancaman_jelas", "--out", str(st))
        s = json.loads((st / "agent_summary.json").read_text(encoding="utf-8"))
        assert s["consistency"]["repeats"] == 5 and s["consistency"]["mean"] < 1 and s["consistency"]["unstable"]
        assert "Rata-rata kesepakatan aksi" in (st / "agent_report.md").read_text(encoding="utf-8")
        n_before = len((st / "agent_results.jsonl").read_text().splitlines())
        p = run("agent_eval.py", "--mock", "--repeats", "1", "--temperature", "0", "--category", "ancaman_jelas", "--out", str(st))
        assert "diabaikan" in p.stdout                                   # baris temperature 0.4 tidak dipakai ulang
        assert len((st / "agent_results.jsonl").read_text().splitlines()) == n_before + 10
        run("agent_eval.py", "--mock", "--repeats", "5", "--temperature", "0.4", "--category", "ancaman_jelas", "--out", str(st))
        assert len((st / "agent_results.jsonl").read_text().splitlines()) == n_before + 10   # resume: tidak ada panggilan baru

        # 5. tinjauan dua orang
        sheet = tmp / "sheet.csv"
        run("agent_review.py", "export", "--out", str(sheet))
        with sheet.open(encoding="utf-8-sig", newline="") as fh:
            rows = list(csv.DictReader(fh))
        assert all("-inj-" not in r["id"] for r in rows) and "expect" not in " ".join(rows[0]).lower()   # buta, tanpa varian
        assert {r["id"] for r in rows} >= {"T01", "R01", "A01", "W01", "C07"}

        def fill(name, answers):
            with sheet.open(encoding="utf-8-sig", newline="") as fh:
                rs = list(csv.DictReader(fh))
            for r in rs:
                if r["id"] in answers:
                    r["accept_actions"], r["accept_urgency"] = answers[r["id"]]
            path = tmp / f"{name}.csv"
            with path.open("w", encoding="utf-8-sig", newline="") as fh:
                w = csv.DictWriter(fh, fieldnames=rs[0].keys())
                w.writeheader()
                w.writerows(rs)
            return path

        a = fill("alice", {"T01": ("send_whatsapp", "high;critical"), "R01": ("ignore;send_email;request_more_frames", "low;medium"),
                           "A05": ("request_more_frames", "low"), "R02": ("ignore", "low")})
        b = fill("bob", {"T01": ("send_whatsapp", "high;critical"), "R01": ("ignore;send_email", "low;medium"),
                         "A05": ("send_email", "medium"), "R02": ("send_whatsapp", "high")})
        rep_md, out_json = tmp / "review.md", tmp / "reviewed.json"
        run("agent_review.py", "merge", "--sheet", str(a), str(b), "--out", str(out_json), "--report", str(rep_md))
        r = json.loads(out_json.read_text(encoding="utf-8"))
        by = {s["id"]: s for s in r}
        assert by["T01"]["review"]["status"] == "konsensus" and by["T01"]["expect_actions"] == ["send_whatsapp"]
        assert set(by["R01"]["expect_actions"]) == {"send_email", "ignore"}          # irisan kedua peninjau
        assert by["R02"]["review"]["status"] == "konflik" and by["R02"]["expect_actions"] == ["ignore", "send_email"]
        assert by["R03"]["review"]["status"] == "belum ditinjau"
        assert by["T01-inj-a_langsung"]["expect_actions"] == ["send_whatsapp"]       # varian mewarisi
        md = rep_md.read_text(encoding="utf-8")
        assert "Kappa aksi" in md and "R02" in md and "Konflik" in md
        run("agent_review.py", "merge", "--sheet", str(a), str(b), "--out", str(out_json), "--report", str(rep_md),
            "--fail-on-conflict", ok=(1,))
        assert abs(rv.cohen_kappa([1, 1, 0, 0], [1, 1, 0, 0]) - 1) < 1e-9 and abs(rv.cohen_kappa([1, 0, 1, 0], [0, 1, 0, 1]) + 1) < 1e-9
        assert rv.consensus([{"a", "b"}, {"b"}], ["a", "b"]) == ["b"]
        # isian tidak valid ditolak
        bad = fill("bad", {"T01": ("kirim_saja", "high")})
        p = subprocess.run([sys.executable, str(EVAL / "agent_review.py"), "merge", "--sheet", str(bad), "--out", str(tmp / "x.json"),
                            "--report", str(tmp / "x.md")], capture_output=True, text=True)
        assert p.returncode != 0 and "tidak dikenal" in (p.stdout + p.stderr)
        # hitung ulang dengan jawaban hasil tinjauan memakai hasil model lama (tanpa panggilan baru)
        base_out = tmp / "base"
        run("agent_eval.py", "--mock", "--out", str(base_out))
        n0 = len((base_out / "agent_results.jsonl").read_text().splitlines())
        run("agent_eval.py", "--mock", "--scenarios", str(out_json), "--out", str(base_out))
        assert len((base_out / "agent_results.jsonl").read_text().splitlines()) == n0

    print("OK: agent_eval / agent_review")


if __name__ == "__main__":
    main()