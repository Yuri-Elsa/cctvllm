#!/usr/bin/env python3
"""Tinjau jawaban yang diharapkan dari evaluasi agen oleh dua orang atau lebih, lalu gabungkan.

Jawaban yang diharapkan di agent_eval.py ditulis oleh satu orang. Skrip ini membuat lembar tinjauan
(CSV) yang bisa diisi tiap peninjau tanpa melihat jawaban penulis maupun keluaran agen (buta), menghitung
kesepakatan antar peninjau (Cohen's kappa), dan menghasilkan berkas skenario baru untuk agent_eval.py.

Alur
  1. Ekspor lembar. Untuk meninjau hanya keputusan yang tidak sesuai harapan dari run sebelumnya:
       python eval/agent_review.py export --out eval/runs/agent_review/sheet.csv \\
           --mismatch-from eval/runs/agent_v1/agent_results.jsonl
     (tanpa --mismatch-from: semua skenario dasar; varian injection mewarisi jawaban skenario dasarnya)
  2. Salin sheet.csv menjadi reviewer_a.csv dan reviewer_b.csv. Tiap orang mengisi kolom
       accept_actions : aksi yang dapat diterima, pisahkan dengan ';'
                        (send_whatsapp ; send_email ; ignore ; request_more_frames)
       accept_urgency : urgensi yang dapat diterima (low ; medium ; high ; critical)
       notes          : alasan singkat (opsional)
     Isi tanpa berdiskusi dulu. Aksi yang tidak tersedia di kolom `channels`/`can_request_more_frames` diabaikan.
     Urgensi dihitung `low` untuk ignore dan tidak dinilai untuk request_more_frames, seperti di agent_eval.py.
  3. Gabungkan:
       python eval/agent_review.py merge --sheet reviewer_a.csv reviewer_b.csv \\
           --out eval/runs/agent_review/scenarios_reviewed.json --report eval/runs/agent_review/review_report.md
     Aturan: pilihan diterima bila disetujui oleh lebih dari separuh peninjau yang mengisi baris itu
     (dengan dua peninjau: keduanya). Jika tidak ada aksi atau urgensi yang disetujui, baris menjadi KONFLIK:
     jawaban penulis dipertahankan dan baris dicantumkan untuk adjudikasi (peninjau ketiga atau diskusi).
  4. Hitung ulang angka tanpa memanggil model lagi (hasil lama dipakai ulang bila model dan temperature sama):
       python eval/agent_eval.py --scenarios eval/runs/agent_review/scenarios_reviewed.json --out eval/runs/agent_v1
     Pakai --out baru untuk run penuh dengan jawaban hasil tinjauan.

Hanya memakai pustaka standar Python.
"""
import argparse
import csv
import json
import sys
from itertools import combinations
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import agent_eval as ae  # noqa: E402

ACTIONS = ["send_whatsapp", "send_email", "ignore", "request_more_frames"]
URG = ae.URG
SHEET_COLS = ["id", "category", "camera", "time", "desc", "site_context", "channels", "can_request_more_frames",
              "suspicious", "weapon", "weapon_enabled", "score", "threshold", "accept_actions", "accept_urgency", "notes"]


def load_scenarios(path):
    return json.loads(Path(path).read_text(encoding="utf-8")) if path else ae.build_scenarios()


def mismatched_ids(scenarios, results_path):
    sc = {s["id"]: s for s in scenarios}
    ids = set()
    for line in Path(results_path).read_text(encoding="utf-8").splitlines():
        if not line.strip():
            continue
        r = json.loads(line)
        s = sc.get(r["id"])
        if not s or r.get("final") is None:
            continue
        m = ae.score(r, s)
        if not m["action_ok"] or m["urgency_ok"] is False:
            ids.add(s["pair_of"] or s["id"])      # varian injection -> tinjau skenario dasarnya
    return ids


def cmd_export(args):
    scenarios = load_scenarios(args.scenarios)
    base = [s for s in scenarios if s["pair_of"] is None]
    if args.mismatch_from:
        keep = mismatched_ids(scenarios, args.mismatch_from)
        base = [s for s in base if s["id"] in keep]
    agent = {}
    if args.show_agent:
        if not args.mismatch_from:
            raise SystemExit("--show-agent butuh --mismatch-from (berkas hasil agen).")
        for line in Path(args.mismatch_from).read_text(encoding="utf-8").splitlines():
            if line.strip():
                r = json.loads(line)
                if r.get("final"):
                    agent.setdefault(r["id"], []).append(f"{r['final']['action']}/{ae.eff_urgency(r['final']) or '-'}: {r['raw']['reason'][:100]}")
    cols = SHEET_COLS + (["agent_decisions"] if args.show_agent else []) + (["author_actions", "author_urgency"] if args.show_author else [])
    out = Path(args.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    with out.open("w", encoding="utf-8-sig", newline="") as fh:
        w = csv.DictWriter(fh, fieldnames=cols)
        w.writeheader()
        for s in base:
            row = {"id": s["id"], "category": s["category"], "camera": s["camera"], "time": s["time"], "desc": s["desc"],
                   "site_context": s["site_context"] or "", "channels": ";".join(s["channels"]),
                   "can_request_more_frames": "ya" if s["can_more"] else "tidak", "suspicious": s["suspicious"],
                   "weapon": s["weapon"] if s["weapon_enabled"] else "dinonaktifkan",
                   "weapon_enabled": s["weapon_enabled"], "score": "" if s["score"] is None else s["score"],
                   "threshold": s["threshold"], "accept_actions": "", "accept_urgency": "", "notes": ""}
            if args.show_agent:
                row["agent_decisions"] = " || ".join(agent.get(s["id"], [])[:5])
            if args.show_author:
                row["author_actions"] = ";".join(s["expect_actions"])
                row["author_urgency"] = ";".join(s["expect_urgency"])
            w.writerow(row)
    print(f"{len(base)} skenario ditulis ke {out}. Salin untuk tiap peninjau dan isi accept_actions / accept_urgency.")


def parse_set(text, universe, label, rid, path):
    toks = [t.strip().lower() for t in text.replace(",", ";").replace("|", ";").split(";") if t.strip()]
    bad = [t for t in toks if t not in universe]
    if bad:
        raise SystemExit(f"{path}: baris {rid}: {label} tidak dikenal {bad}; pilihan: {universe}")
    return set(toks)


def read_sheet(path):
    """-> {id: (actions:set | None, urgency:set | None)}; baris kosong = belum ditinjau."""
    got = {}
    with Path(path).open(encoding="utf-8-sig", newline="") as fh:
        for row in csv.DictReader(fh):
            a = (row.get("accept_actions") or "").strip()
            u = (row.get("accept_urgency") or "").strip()
            if not a and not u:
                continue
            rid = row["id"]
            got[rid] = (parse_set(a, ACTIONS, "aksi", rid, path) if a else None,
                        parse_set(u, URG, "urgensi", rid, path) if u else None)
    return got


def cohen_kappa(x, y):
    n = len(x)
    if n == 0:
        return None
    po = sum(a == b for a, b in zip(x, y)) / n
    px, py = sum(x) / n, sum(y) / n
    pe = px * py + (1 - px) * (1 - py)
    if pe == 1:
        return 1.0 if po == 1 else 0.0
    return (po - pe) / (1 - pe)


def kappa_label(k):
    if k is None:
        return "n/a"
    for lim, name in ((0.8, "hampir sempurna"), (0.6, "substansial"), (0.4, "sedang"), (0.2, "lemah")):
        if k > lim:
            return name
    return "sangat lemah"


def consensus(votes, universe):
    """votes: list[set]. Pilihan diterima bila > separuh peninjau menyetujuinya."""
    n = len(votes)
    return [o for o in universe if sum(o in v for v in votes) * 2 > n]


def cmd_merge(args):
    scenarios = load_scenarios(args.scenarios)
    by_id = {s["id"]: s for s in scenarios}
    names = args.names or [Path(p).stem for p in args.sheet]
    if len(names) != len(args.sheet):
        raise SystemExit("--names harus sama banyaknya dengan --sheet")
    if len(args.sheet) < 2:
        print("PERINGATAN: hanya satu peninjau; tidak ada kesepakatan antar peninjau yang bisa dihitung.", file=sys.stderr)
    sheets = {n: read_sheet(p) for n, p in zip(names, args.sheet)}
    for n, sh in sheets.items():
        unknown = [i for i in sh if i not in by_id]
        if unknown:
            raise SystemExit(f"{n}: id tidak ada di skenario: {unknown[:5]}")

    changed, conflicts, confirmed, unreviewed = [], [], [], []
    base_ids = [s["id"] for s in scenarios if s["pair_of"] is None]
    new_expect = {}
    for sid in base_ids:
        s = by_id[sid]
        avail = ae.allowed_actions(s)
        voters = [(n, sh[sid]) for n, sh in sheets.items() if sid in sh]
        if not voters:
            unreviewed.append(sid)
            continue
        a_votes = [(n, (v[0] or set()) & set(avail)) for n, v in voters if v[0] is not None]
        u_votes = [(n, v[1]) for n, v in voters if v[1] is not None]
        acts = consensus([v for _, v in a_votes], [a for a in ACTIONS if a in avail]) if a_votes else list(s["expect_actions"])
        urgs = consensus([v for _, v in u_votes], URG) if u_votes else list(s["expect_urgency"])
        only_more = set(acts) == {"request_more_frames"}      # urgensi tidak dinilai untuk request_more_frames
        if not acts or (not urgs and not only_more):
            conflicts.append((sid, {n: (sorted(v[0] or []), sorted(v[1] or [])) for n, v in voters}))
            continue
        if not urgs:
            urgs = list(s["expect_urgency"])
        new_expect[sid] = (acts, urgs)
        old = (list(s["expect_actions"]), list(s["expect_urgency"]))
        if set(acts) != set(old[0]) or set(urgs) != set(old[1]):
            changed.append((sid, old, (acts, urgs)))
        else:
            confirmed.append(sid)

    conflict_ids = {c[0] for c in conflicts}
    out_sc = []
    for s in scenarios:
        s = dict(s)
        key = s["pair_of"] or s["id"]
        if key in new_expect:
            s["expect_actions"], s["expect_urgency"] = list(new_expect[key][0]), list(new_expect[key][1])
            s["review"] = {"status": "konsensus", "reviewers": names}
        elif key in conflict_ids:
            s["review"] = {"status": "konflik", "reviewers": names}
        else:
            s["review"] = {"status": "belum ditinjau"}
        out_sc.append(s)
    out = Path(args.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(out_sc, ensure_ascii=False, indent=2), encoding="utf-8")

    # kesepakatan antar peninjau: tiap (skenario, pilihan) satu penilaian biner
    L = ["# Tinjauan jawaban yang diharapkan", "", f"Peninjau: {', '.join(names)}. Skenario dasar: {len(base_ids)}; "
         f"ditinjau: {len(base_ids) - len(unreviewed)}; belum ditinjau: {len(unreviewed)}.", "",
         "## Kesepakatan antar peninjau", ""]
    if len(sheets) >= 2:
        L += ["| Pasangan | Skenario bersama | Kesepakatan set aksi persis | Kappa aksi | Kappa urgensi |", "|---|---|---|---|---|"]
        for a, b in combinations(names, 2):
            common = [sid for sid in base_ids if sid in sheets[a] and sid in sheets[b]]
            ax, ay, ux, uy, exact = [], [], [], [], 0
            for sid in common:
                avail = ae.allowed_actions(by_id[sid])
                sa, sb = sheets[a][sid], sheets[b][sid]
                if sa[0] is not None and sb[0] is not None:
                    exact += (sa[0] & set(avail)) == (sb[0] & set(avail))
                    for o in avail:
                        ax.append(o in sa[0]); ay.append(o in sb[0])
                if sa[1] is not None and sb[1] is not None:
                    for o in URG:
                        ux.append(o in sa[1]); uy.append(o in sb[1])
            ka, ku = cohen_kappa(ax, ay), cohen_kappa(ux, uy)
            L.append(f"| {a} vs {b} | {len(common)} | {exact}/{len(common)} | "
                     f"{'n/a' if ka is None else f'{ka:.2f} ({kappa_label(ka)})'} | "
                     f"{'n/a' if ku is None else f'{ku:.2f} ({kappa_label(ku)})'} |")
        L += ["", "Kappa dihitung atas keputusan biner (diterima / tidak) tiap pilihan aksi dan tiap tingkat urgensi.", ""]
    else:
        L += ["Hanya satu peninjau.", ""]
    L += ["## Hasil penggabungan", "",
          f"- Sama dengan jawaban penulis: {len(confirmed)}",
          f"- Berubah dari jawaban penulis: {len(changed)}",
          f"- Konflik, perlu adjudikasi (jawaban penulis dipertahankan sementara): {len(conflicts)}",
          f"- Belum ditinjau: {len(unreviewed)}", ""]
    if changed:
        L += ["### Berubah", "", "| Skenario | Aksi: penulis → konsensus | Urgensi: penulis → konsensus |", "|---|---|---|"]
        for sid, o, n in changed:
            L.append(f"| {sid} | {', '.join(o[0])} → {', '.join(n[0])} | {', '.join(o[1])} → {', '.join(n[1])} |")
        L.append("")
    if conflicts:
        L += ["### Konflik", "", "| Skenario | Deskripsi | " + " | ".join(names) + " |", "|---|---|" + "---|" * len(names)]
        for sid, v in conflicts:
            cells = [f"{', '.join(v[n][0]) if n in v else '-'} / {', '.join(v[n][1]) if n in v else '-'}" for n in names]
            L.append(f"| {sid} | {by_id[sid]['desc'][:80].replace('|', '/')} | " + " | ".join(cells) + " |")
        L.append("")
    L += ["Jalankan ulang: `python eval/agent_eval.py --scenarios " + str(out) + " --out <folder run>`."]
    Path(args.report).parent.mkdir(parents=True, exist_ok=True)
    Path(args.report).write_text("\n".join(L), encoding="utf-8")
    print(f"Skenario hasil tinjauan: {out}\nLaporan: {args.report}\n"
          f"sama={len(confirmed)} berubah={len(changed)} konflik={len(conflicts)} belum={len(unreviewed)}")
    return 1 if conflicts and args.fail_on_conflict else 0


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    e = sub.add_parser("export", help="buat lembar tinjauan CSV (buta)")
    e.add_argument("--out", required=True)
    e.add_argument("--scenarios", help="JSON skenario (default: skenario bawaan agent_eval.py)")
    e.add_argument("--mismatch-from", metavar="AGENT_RESULTS_JSONL", help="hanya skenario yang punya keputusan tidak sesuai harapan di run ini")
    e.add_argument("--show-agent", action="store_true", help="sertakan keputusan agen (hanya untuk adjudikator, bukan peninjau buta)")
    e.add_argument("--show-author", action="store_true", help="sertakan jawaban penulis (hanya untuk adjudikator)")
    e.set_defaults(fn=cmd_export)
    m = sub.add_parser("merge", help="gabungkan lembar peninjau")
    m.add_argument("--sheet", nargs="+", required=True, help="lembar yang sudah diisi, satu per peninjau")
    m.add_argument("--names", nargs="+", help="nama peninjau (default: nama berkas)")
    m.add_argument("--scenarios", help="JSON skenario yang sama dengan saat ekspor")
    m.add_argument("--out", required=True, help="skenario hasil tinjauan (untuk agent_eval.py --scenarios)")
    m.add_argument("--report", required=True)
    m.add_argument("--fail-on-conflict", action="store_true", help="exit code 1 bila ada konflik")
    m.set_defaults(fn=cmd_merge)
    args = ap.parse_args()
    sys.exit(args.fn(args) or 0)


if __name__ == "__main__":
    main()