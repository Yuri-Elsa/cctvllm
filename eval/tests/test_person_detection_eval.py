#!/usr/bin/env python3
"""Self-check for eval/person_detection_eval.py.

Run:  python eval/tests/test_person_detection_eval.py

No GPU, model or ultralytics needed: synthetic videos, a mock OpenAI-compatible server that returns
log-probabilities, and a fake `ultralytics` module stand in for the real detectors. It verifies the
p(yes) port against the cases in tests/CctvDemo.Tests/ScoringTests.cs, that the prompts match the C#
source, the request shape, resume and error handling, and that the report's threshold and test
metrics equal an independent recomputation from the raw files.
"""
import csv
import json
import re
import math
import os
import subprocess
import sys
import tempfile
import threading
import zlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

import numpy as np

for _stream in (sys.stdout, sys.stderr):        # Windows consoles default to cp1252; messages may contain symbols
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

HERE = Path(__file__).resolve().parent
EVAL = HERE.parent
ROOT = EVAL.parent
sys.path.insert(0, str(EVAL))
import person_detection_eval as pde  # noqa: E402

SCRIPT = str(EVAL / "person_detection_eval.py")


def check(cond, msg):
    print(("PASS  " if cond else "FAIL  ") + msg)
    if not cond:
        raise SystemExit(1)


def close(a, b, tol=1e-6):
    return abs(a - b) <= tol


# ------------------------------------------------------------------ 1. p(yes) port and prompt parity

def tok(token, lp, *top):
    return (token, lp, list(top))


def answer(value):
    return [tok('{"activity_description":"x",', -0.1), tok('"suspicious"', -0.1), tok(":", -0.1), tok(' "', -0.1),
            value, tok('",', -0.1), tok('"weapon"', -0.1), tok(':"no"}', -0.1)]


def unit_tests():
    L = math.log
    check(close(pde.p_yes(answer(tok("yes", L(.8), ("yes", L(.8)), ("no", L(.2)))), "suspicious"), 0.8), "p_yes: yes share of yes/no")
    check(close(pde.p_yes(answer(tok("yes", L(.3), ("yes", L(.3)), ("no", L(.1)), ("maybe", L(.5)))), "suspicious"), 0.75),
          "p_yes: renormalises, ignores other tokens")
    check(close(pde.p_yes(answer(tok("Yes", L(.5), ("Yes", L(.3)), (" yes", L(.2)), ('"no', L(.5)))), "suspicious"), 0.5),
          "p_yes: case, whitespace and quote variants")
    check(pde.p_yes(answer(tok("yes", -0.01)), "suspicious") == 1.0 and pde.p_yes(answer(tok("no", -0.01)), "suspicious") == 0.0,
          "p_yes: no alternatives -> chosen token alone")
    two = [tok('{"suspicious":"', -0.1), tok("no", -0.1, ("no", L(.9)), ("yes", L(.1))),
           tok('","weapon":"', -0.1), tok("yes", -0.1, ("yes", L(.6)), ("no", L(.4))), tok('"}', -0.1)]
    check(close(pde.p_yes(two, "suspicious"), 0.1) and close(pde.p_yes(two, "weapon"), 0.6), "p_yes: reads the requested key")
    trap = [tok('{"activity_description":"Orang membaca papan \\"suspicious\\" di dinding.","suspicious":"', -0.1),
            tok("no", -0.1, ("no", L(.95)), ("yes", L(.05))), tok('"}', -0.1)]
    check(close(pde.p_yes(trap, "suspicious"), 0.05), "p_yes: key name inside the text is ignored")
    check(close(pde.p_yes(answer(tok("yes", -0.1, ("yes", L(.5)), ("no", float("nan")), ("no", L(.5)))), "suspicious"), 0.5),
          "p_yes: NaN logprobs skipped")
    check(pde.p_yes([], "suspicious") is None and pde.p_yes(answer(tok("yes", -.1)), "nokey") is None
          and pde.p_yes(answer(tok("maybe", -.1, ("maybe", -.1), ("perhaps", -.2))), "suspicious") is None
          and pde.p_yes([tok('{"suspicious"', -.1)], "suspicious") is None, "p_yes: None when undeterminable")
    check(pde.read_tokens({"message": {}}) is None, "read_tokens: None without logprobs")

    cs = (ROOT / "Infrastructure" / "Services" / "LlmPersonDetector.cs").read_text(encoding="utf-8").replace('\\"', '"')
    check(pde.SYSTEM_PROMPT in cs and pde.QUESTION in cs, "system prompt and question match LlmPersonDetector.cs")
    check(f"max_tokens = 30" in cs and "seed = 7" in cs and f"TopLogprobs" in cs, "request constants still match the C# detector")

    with tempfile.TemporaryDirectory() as d:        # a labels.csv re-saved by Excel (BOM, ';' separator)
        f = Path(d) / "labels.csv"
        f.write_bytes(("\ufeff" + "frame_id;path;video_id;class_name;timestamp;split;has_person;difficulty;notes\r\n"
                       "a;frames/a.png;v1;X;1.0;test;1;small;\r\nb;frames/b.png;v1;X;2.0;test;0;;\r\nc;frames/c.png;v2;X;3.0;test;;;\r\n"
                       ).encode("utf-8"))
        got = pde.read_labels(f)
        check([r["frame_id"] for r in got] == ["a", "b"] and [r["y"] for r in got] == [1, 0] and got[0]["difficulty"] == "small",
              "labels saved by Excel (BOM, semicolons) are read; blank labels ignored")

    # LLM probabilities saturate near 1: only a threshold taken from the data separates these classes
    ys = np.array([1] * 40 + [0] * 30)
    sl = np.array([0.999985] * 40 + [0.99995] * 30)
    params, reached, best = pde.select_cascade(ys, np.zeros(70), sl, 0.95, 0.2, 0.2)
    check(reached and best[3] == 1.0 and best[4] == 0.0 and 0.99995 < params["t_llm"] <= 0.999985,
          f"cascade grid follows the saturated LLM scores (picked t_llm {params['t_llm']})")
    check("untuk berapa pun D" in pde.cost_crossover("A", 1.0, 0.3, "B", 2.0, 0.5)
          and "tidak pernah lebih murah" in pde.cost_crossover("A", 3.0, 0.6, "B", 1.0, 0.5),
          "cost crossover: dominated and dominating cases")
    txt = pde.cost_crossover("LLM", 3.381, 0.32, "YOLO", 0.348, 0.55)
    check("D > 13.2 s" in txt, f"cost crossover: slow-but-selective detector wins when description is expensive ({txt[:70]})")
    check("D < 13.5 s" in pde.cost_crossover("A", 0.3, 0.5, "B", 3.0, 0.3), "cost crossover: fast-but-leaky detector wins when description is cheap")

    y, s = np.array([0, 0, 1, 1]), np.array([.1, .4, .35, .8])
    check(close(pde.roc_auc(y, s), 0.75) and close(pde.average_precision(y, s), 5 / 6), "AUC and AP on a known example")
    t, ok = pde.pick_threshold(y, s, 1.0)
    check(ok and t == 0.35, "pick_threshold: highest threshold reaching recall 1.0")
    t, ok = pde.pick_threshold(y, np.array([.1, .4, -1, .8]), 1.0)
    check(not ok and t == 0.0, "pick_threshold: invalid positive makes the target unreachable")


# ------------------------------------------------------------------ 2. fixtures

FAKE_ULTRALYTICS = '''
import math
class _Conf(list):
    def tolist(self): return list(self)
class _Boxes:
    def __init__(self, c): self.conf = _Conf(c)
    def __len__(self): return len(self.conf)
class _Result:
    def __init__(self, c): self.boxes = _Boxes(c)
class YOLO:
    def __init__(self, weights): self.weights = weights
    def predict(self, frame, classes=None, conf=0.25, imgsz=640, device="cpu", verbose=False):
        assert classes == [0] and conf == 0.05 and max(frame.shape[:2]) <= 640, (classes, conf, frame.shape)
        c = 1 / (1 + math.exp(-(float(frame.mean()) - 128) / 25))
        return [_Result([c] if c >= conf else [])]
'''


class Mock(BaseHTTPRequestHandler):
    requests = []

    def log_message(self, *a):
        pass

    def do_POST(self):
        import base64
        import cv2
        body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        Mock.requests.append(body)
        if body.get("model") == "missing-model":
            out = json.dumps({"error": {"message": "model 'missing-model' not found", "type": "not_found_error"}}).encode()
            self.send_response(404)
            self.send_header("Content-Length", str(len(out)))
            self.end_headers()
            self.wfile.write(out)
            return
        url = body["messages"][1]["content"][0]["image_url"]["url"]
        payload = url.split(",", 1)[1]
        img = cv2.imdecode(np.frombuffer(base64.b64decode(payload), np.uint8), cv2.IMREAD_COLOR)
        crc = zlib.crc32(payload.encode())
        if crc % 17 == 0 or body.get("model") == "always-bad":   # injected failure: invalid answer
            content, logprobs = '{"person":"maybe"}', None
        else:
            z = (float(img.mean()) - 128) / 35 + ((crc % 100) / 100 - 0.5) * 1.5
            p = min(max(1 / (1 + math.exp(-z)), 1e-9), 1 - 1e-9)
            content = json.dumps({"person": "yes" if p >= .5 else "no"})
            logprobs = {"content": [
                {"token": '{"person":"', "logprob": -0.01, "top_logprobs": []},
                {"token": "yes" if p >= .5 else "no", "logprob": math.log(max(p, 1 - p)),
                 "top_logprobs": [{"token": "yes", "logprob": math.log(p)}, {"token": "no", "logprob": math.log(1 - p)}]},
                {"token": '"}', "logprob": -0.001, "top_logprobs": []}]}
        out = json.dumps({"choices": [{"message": {"content": content}, **({"logprobs": logprobs} if logprobs else {})}]}).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(out)))
        self.end_headers()
        self.wfile.write(out)


def build_videos(tmp):
    import cv2
    rng = np.random.default_rng(1)
    videos = tmp / "videos"
    videos.mkdir()
    rows = []
    for cls, p_person in (("Alpha", .85), ("Beta", .1), ("Gamma", .5)):
        for i in range(8):
            name = f"{cls.lower()}{i:02d}"
            w = cv2.VideoWriter(str(videos / f"{name}.avi"), cv2.VideoWriter_fourcc(*"MJPG"), 5, (800, 600))
            for sec in range(10):
                person = rng.random() < p_person
                for _ in range(5):
                    g = rng.uniform(110, 250) if person else rng.uniform(5, 145)
                    f = np.full((600, 800, 3), g, np.uint8)
                    f[:16, :16] = 255 if person else 0          # ground-truth marker, read back when labeling
                    w.write(f)
            w.release()
            rows.append({"clip_id": name, "relative_path": f"{name}.avi", "class_name": cls, "start_sec": 0, "end_sec": 10})
    manifest = tmp / "manifest.csv"
    with open(manifest, "w", newline="") as f:
        wr = csv.DictWriter(f, fieldnames=list(rows[0]))
        wr.writeheader()
        wr.writerows(rows)
    return manifest, videos


def run(cmd, env=None):
    p = subprocess.run([sys.executable, SCRIPT] + cmd, capture_output=True, text=True, encoding="utf-8", errors="replace",
                       env={**os.environ, **(env or {})})
    return p.returncode, p.stdout + p.stderr


def jl(path):
    return [json.loads(x) for x in Path(path).read_text(encoding="utf-8").splitlines() if x.strip()]


# ------------------------------------------------------------------ 4. browser labeling tool

def label_tool():
    import urllib.error
    import urllib.request
    import cv2
    with tempfile.TemporaryDirectory() as d:
        d = Path(d)
        (d / "frames").mkdir()
        (d.parent / "secret_outside.txt").write_text("top secret")
        for n in range(4):
            cv2.imwrite(str(d / "frames" / f"f{n}.png"), np.full((48, 64, 3), 40 * n, np.uint8))
        header = "frame_id;path;video_id;class_name;timestamp;split;has_person;difficulty;notes"
        lines = [f"f0;frames/f0.png;v1;Abuse;1.0;test;1;small;", f"f1;frames/f1.png;v1;Abuse;2.0;test;;;",
                 f"f2;frames/f2.png;v2;Normal;3.0;calibration;0;;", f"f3;frames/f3.png;v2;Normal;4.0;calibration;;;",
                 "evil;../secret_outside.txt;v3;Normal;5.0;test;;;"]
        csv_path = d / "labels.csv"
        csv_path.write_bytes(("\ufeff" + header + "\r\n" + "\r\n".join(lines) + "\r\n").encode("utf-8"))   # as saved by Excel
        server = pde.make_label_server(csv_path, 0)
        threading.Thread(target=server.serve_forever, daemon=True).start()
        base = f"http://127.0.0.1:{server.server_address[1]}"

        def get(path):
            try:
                with urllib.request.urlopen(base + path) as r:
                    return r.status, r.read()
            except urllib.error.HTTPError as e:
                return e.code, e.read()

        def post(obj):
            req = urllib.request.Request(base + "/api/label", data=json.dumps(obj).encode(), method="POST",
                                         headers={"Content-Type": "application/json"})
            try:
                with urllib.request.urlopen(req) as r:
                    return r.status, json.loads(r.read())
            except urllib.error.HTTPError as e:
                return e.code, json.loads(e.read())

        code, page = get("/")
        check(code == 200 and b"Pelabelan frame" in page and b'"small"' in page, "label page is served with the tag buttons")
        code, body = get("/api/state")
        items = json.loads(body)["items"]
        check(code == 200 and len(items) == 5 and items[0]["y"] == "1" and items[0]["tags"] == "small" and items[1]["y"] == "",
              "state lists every frame and keeps existing labels (Excel file with BOM and ';')")
        check("class_name" not in body.decode() and "Abuse" not in body.decode() and "video_id" not in body.decode(),
              "class and video are not sent to the browser (blind labeling)")
        code, png = get("/img/f2")
        check(code == 200 and png[:4] == b"\x89PNG", "frame image is served")
        check(get("/img/nope")[0] == 404 and get("/img/evil")[0] == 404, "unknown frame and path traversal -> 404")

        code, res = post({"id": "f1", "y": "1", "tags": "small|bogus|dark"})
        check(code == 200, "label saved")
        rows = pde.read_labels(csv_path)
        got = {r["frame_id"]: r for r in rows}
        check(got["f1"]["y"] == 1 and got["f1"]["difficulty"] == "small|dark", "label stored, unknown tags dropped")
        fields, raw = pde.read_csv_rows(csv_path)
        check(fields == header.split(";") and raw[1]["class_name"] == "Abuse" and raw[2]["has_person"] == "0",
              "all other columns and labels preserved when rewriting")
        code, _ = post({"id": "f2", "y": "", "tags": ""})
        check(code == 200 and "f2" not in {r["frame_id"] for r in pde.read_labels(csv_path)}, "clearing a label works")
        check(post({"id": "zzz", "y": "1"})[0] == 400, "unknown frame id -> 400")

        real = pde.os.replace
        def locked(*a, **k):
            raise PermissionError(13, "Permission denied")
        pde.os.replace = locked
        try:
            code, res = post({"id": "f3", "y": "1", "tags": ""})
        finally:
            pde.os.replace = real
        state = {i["id"]: i for i in json.loads(get("/api/state")[1])["items"]}
        check(code == 500 and "Excel" in res["error"] and state["f3"]["y"] == "", "file locked -> 500 with advice, change rolled back")
        check(post({"id": "f3", "y": "0", "tags": ""})[0] == 200, "saving works again once the file is free")
        server.shutdown()
        server.server_close()

        # ---- review mode: only the listed frames, blind, with the old label kept for traceability
        rev_csv = d / "labels_review.csv"
        rlines = ["f0;frames/f0.png;v1;Abuse;1.0;test;1;small;", "f1;frames/f1.png;v1;Abuse;2.0;test;;;",
                  "f2;frames/f2.png;v2;Normal;3.0;calibration;0;;", "f3;frames/f3.png;v2;Normal;4.0;calibration;0;;",
                  "f4;frames/f3.png;v3;Normal;5.0;test;1;;"]
        rev_csv.write_bytes(("\ufeff" + header + "\r\n" + "\r\n".join(rlines) + "\r\n").encode("utf-8"))
        only = d / "disagreements.csv"
        only.write_text("frame_id,path,split,has_person,yolo_score,llm_score,case\nf0,x,test,1,0.9,0.1,a\nf2,x,test,0,0.1,0.9,b\n"
                        "f4,x,test,1,0.0,0.9,c\nghost,x,test,1,0,0,d\n", encoding="utf-8")
        rsrv = pde.make_label_server(rev_csv, 0, review=only)
        threading.Thread(target=rsrv.serve_forever, daemon=True).start()
        base = f"http://127.0.0.1:{rsrv.server_address[1]}"
        check(rsrv.review_count == 3 and rsrv.review_skipped == 1, "review: 3 listed frames found, 1 unknown id skipped")
        body = get("/api/state")[1]
        st = json.loads(body)
        ids = sorted(i["id"] for i in st["items"])
        check(st["review"] is True and ids == ["f0", "f2", "f4"] and all(i["y"] == "" and i["before"] == "" for i in st["items"]),
              "review: only listed frames, earlier labels hidden")
        check("score" not in body.decode() and "Abuse" not in body.decode(), "review: no scores or class names are sent")
        check(post({"id": "f1", "y": "1", "tags": ""})[0] == 400, "review: frames outside the review set are rejected")
        check(post({"id": "f0", "y": "0", "tags": "small"})[0] == 200, "review: decision saved")
        fields, raw = pde.read_csv_rows(rev_csv)
        r_ = {r["frame_id"]: r for r in raw}
        check(r_["f0"]["has_person"] == "0" and r_["f0"]["label_before_review"] == "1" and "label_before_review" in fields,
              "review: new label stored, old label kept in label_before_review")
        check(r_["f1"]["has_person"] == "" and r_["f3"]["has_person"] == "0" and r_["f4"]["has_person"] == "1" and r_["f4"]["label_before_review"] == "",
              "review: other frames untouched")
        post({"id": "f2", "y": "0", "tags": ""})
        post({"id": "f4", "y": "", "tags": "dark"})                      # tags only: no decision yet
        raw = pde.read_csv_rows(rev_csv)[1]
        r_ = {r["frame_id"]: r for r in raw}
        check(r_["f2"]["label_before_review"] == "0" and r_["f4"]["difficulty"] == "dark" and r_["f4"]["has_person"] == "1"
              and r_["f4"]["label_before_review"] == "", "review: same label recorded as reviewed; tag-only edit is not a decision")
        items = {i["id"]: i for i in json.loads(get("/api/state")[1])["items"]}
        check(items["f0"]["y"] == "0" and items["f0"]["before"] == "1" and items["f4"]["y"] == "", "review: state shows reviewed frames only")
        post({"id": "f0", "y": "", "tags": "small"})                     # undo
        raw = pde.read_csv_rows(rev_csv)[1]
        r_ = {r["frame_id"]: r for r in raw}
        check(r_["f0"]["has_person"] == "1" and r_["f0"]["label_before_review"] == "", "review: clearing restores the old label")
        rsrv.shutdown()
        rsrv.server_close()


# ------------------------------------------------------------------ 5. analysis scenarios (no images needed)

def analysis_scenarios():
    rng = np.random.default_rng(5)
    with tempfile.TemporaryDirectory() as d:
        d = Path(d)
        rows, recs = [], []
        for v in range(60):
            split = "calibration" if v < 20 else "test"
            for k in range(4):
                fid, y = f"v{v:03d}_{k}", int(k < 2)
                rows.append({"frame_id": fid, "path": f"frames/{fid}.png", "video_id": f"v{v:03d}", "class_name": "X",
                             "timestamp": k, "split": split, "has_person": str(y), "difficulty": "", "notes": ""})
                if y:   # "yolo": half of the people get no score at all (below the logging floor)
                    ys = 0.0 if rng.random() < 0.5 else rng.uniform(0.5, 0.95)
                    ls = rng.uniform(0.9, 1.0) if rng.random() < 0.97 else rng.uniform(0, 0.2)
                else:
                    ys = 0.0 if rng.random() < 0.9 else rng.uniform(0.1, 0.4)
                    ls = rng.uniform(0, 0.1) if rng.random() < 0.85 else rng.uniform(0.8, 1.0)
                recs += [{"frame_id": fid, "detector": "yolo:x", "score": float(ys), "latency_s": 0.05},
                         {"frame_id": fid, "detector": "llm:good", "score": float(ls), "latency_s": 3.0},
                         {"frame_id": fid, "detector": "llm:broken", "score": None, "error": "HTTP 404"}]
        labels, scores = d / "labels.csv", d / "scores.jsonl"
        with open(labels, "w", newline="", encoding="utf-8") as f:
            wr = csv.DictWriter(f, fieldnames=pde.LABEL_FIELDS)
            wr.writeheader()
            wr.writerows(rows)
        scores.write_text("\n".join(json.dumps(r) for r in recs), encoding="utf-8")
        out = d / "report"
        code, log = run(["analyze", "--labels", str(labels), "--scores", str(scores), "--out", str(out), "--n-boot", "400",
                         "--fpr-targets", "0.1", "0.2"])
        check(code == 0, f"analyze on scenario data exits 0 ({log[-300:] if code else 'ok'})")
        report = (out / "person_report.md").read_text(encoding="utf-8")
        summary = json.loads((out / "summary.json").read_text())
        check(set(summary["detectors"]) == {"yolo:x", "llm:good"} and "llm:broken: tidak punya satu pun skor valid" in report,
              "a detector whose every call failed is excluded, with a note")
        check("yolo:x: pada target recall 95% threshold yang terpilih meloloskan hampir semua frame" in report
              and summary["detectors"]["yolo:x"]["test"]["fpr"]["value"] >= 0.95,
              "degenerate frozen threshold (YOLO misses people entirely) is flagged")

        by = {}
        for r in recs:
            by[(r["frame_id"], r["detector"])] = r["score"]
        lab = {r["frame_id"]: r for r in rows}
        for det in ("yolo:x", "llm:good"):
            neg = sorted((by[(f, det)] for f in lab if lab[f]["split"] == "calibration" and lab[f]["has_person"] == "0"))
            for target in (0.1, 0.2):
                best = None
                for tt in sorted(set(neg) | {by[(f, det)] for f in lab if lab[f]["split"] == "calibration"}, reverse=True):
                    if sum(1 for s in neg if s >= tt) / len(neg) > target + 1e-12:
                        break
                    best = tt
                expect = pde.floor6(best) if best is not None else 1.000001
                got = summary["detectors"][det]["fpr_ops"][str(target)]
                check(close(got["threshold"], expect, 1e-9), f"{det}: threshold for FPR<={target} equals independent value {expect}")
                tst = [f for f in lab if lab[f]["split"] == "test"]
                tp = sum(1 for f in tst if lab[f]["has_person"] == "1" and by[(f, det)] >= expect)
                fp = sum(1 for f in tst if lab[f]["has_person"] == "0" and by[(f, det)] >= expect)
                check(got["counts"]["tp"] == tp and got["counts"]["fp"] == fp, f"{det}: matched-FPR test counts tp={tp} fp={fp} match")
        d10 = summary["detectors"]["llm:good"]["fpr_ops"]["0.1"]["delta_vs_reference"]["recall"]
        check(d10["ci"][0] > 0 and "**lebih baik** dibanding yolo:x" in report,
              f"LLM recall at matched FPR is shown as better than YOLO (delta {d10['value']:.2f}, CI lower {d10['ci'][0]:.2f})")
        check("| Detektor | Threshold | Recall frame | Presisi | FPR | Recall video (gate) |" in report,
              "fixed-threshold table includes video-level recall")

        dis = list(csv.DictReader(open(out / "disagreements_llm_good.csv", encoding="utf-8")))
        expect_n = sum(1 for f in lab if (by[(f, "yolo:x")] >= 0.25) != (by[(f, "llm:good")] >= 0.5))
        check(len(dis) == expect_n and {"frame_id", "has_person", "case"} <= set(dis[0]), f"disagreements.csv lists {expect_n} frames")
        check("Kesepakatan detektor" in report, "agreement table in report")
        rsrv = pde.make_label_server(labels, 0, review=out / "disagreements_llm_good.csv")
        check(rsrv.review_count == len(dis) and rsrv.review_skipped == 0,
              f"the disagreements file produced by analyze is accepted by label --review ({rsrv.review_count} frames)")
        rsrv.server_close()

        code, log = run(["analyze", "--labels", str(labels), "--scores", str(scores), "--out", str(d / "r2"),
                         "--detectors", "yolo", "--n-boot", "100"])
        s2 = json.loads((d / "r2" / "summary.json").read_text())
        check(code == 0 and set(s2["detectors"]) == {"yolo:x"} and not s2["deltas_vs_reference"], "--detectors filters (prefix match)")

        # ---- cascade simulation (YOLO first, LLM for the uncertain band)
        for kind_params in ({"t_lo": 0.0, "t_hi": math.inf, "t_llm": 0.9},):
            sy = np.array([by[(f, "yolo:x")] for f in lab])
            sl = np.array([by[(f, "llm:good")] for f in lab])
            pas, call = pde.cascade_decision("cascade", kind_params, sy, sl)
            ref_pas, ref_call = pde.cascade_decision("llm", {"t": 0.9}, sy, sl)
            check((pas == ref_pas).all() and call.all(), "cascade with open band (0, inf) equals the LLM alone and calls it on every frame")
        pas, call = pde.cascade_decision("cascade", {"t_lo": 0.2, "t_hi": 0.8, "t_llm": 0.5}, np.array([0.1, 0.2, 0.5, 0.8, 0.9]),
                                         np.array([0.9, 0.4, 0.6, 0.0, 0.0]))
        check(list(pas) == [False, False, True, True, True] and list(call) == [False, True, True, False, False],
              "cascade rules: below band rejected, band asks the LLM, at or above the high threshold accepted without it")

        cdir = d / "cascade"
        code, log = run(["cascade", "--labels", str(labels), "--scores", str(scores), "--out", str(cdir), "--n-boot", "300"])
        check(code == 0, f"cascade exits 0 ({log[-300:] if code else 'ok'})")
        csum = json.loads((cdir / "cascade_summary.json").read_text())
        creport = (cdir / "cascade_report.md").read_text(encoding="utf-8")
        check("Biaya total per frame" in creport and "Titik impas" in creport and csum["yolo"]["yolo:x"]["cost_crossover"],
              "cascade report includes the total-cost table and break-even lines")
        check(csum["llm"] == "llm:good" and list(csum["yolo"]) == ["yolo:x"] and "Pembacaan otomatis" in creport
              and (cdir / "cascade_grid_yolo_x.csv").exists(), "cascade: broken detector ignored, report and grid written")
        strat = csum["yolo"]["yolo:x"]["strategies"]
        check(strat["llm:good saja"]["test"]["call"] == 1.0 and strat["yolo:x saja"]["test"]["call"] == 0.0,
              "LLM alone costs 1 call per frame, YOLO alone none")
        for label, e in strat.items():
            if e["kind"] != "cascade":
                continue
            p_ = e["params"]
            def run_split(split):
                tp = fp = fn = tn = cp = cn = npos = nneg = 0
                for f in lab:
                    if lab[f]["split"] != split:
                        continue
                    sy_, sl_ = by[(f, "yolo:x")], by[(f, "llm:good")]
                    c_ = p_["t_lo"] <= sy_ < p_["t_hi"]
                    ps_ = sy_ >= p_["t_hi"] or (c_ and sl_ >= p_["t_llm"])
                    if lab[f]["has_person"] == "1":
                        npos += 1; cp += c_; tp += ps_; fn += (not ps_)
                    else:
                        nneg += 1; cn += c_; fp += ps_; tn += (not ps_)
                return tp, fp, fn, tn, cp, cn, npos, nneg
            tp, fp, fn, tn, cp, cn, npos, nneg = run_split("test")
            t_ = e["test"]
            check(close(t_["recall"], tp / (tp + fn)) and close(t_["fpr"], fp / (fp + tn)) and close(t_["call_data"], (cp + cn) / (npos + nneg)),
                  f"{label}: recall, FPR and LLM-call share on test match an independent recomputation")
            check(close(t_["call"], 0.2 * cp / npos + 0.8 * cn / nneg) and close(t_["latency_s"], 0.05 + t_["call"] * 3.0, 1e-9),
                  f"{label}: person-share weighted calls and latency (0.05 s + calls x 3 s) are right")
            ctp, cfp, cfn, ctn, *_ = run_split("calibration")
            target = max(int(v) for v in re.findall(r"(\d+)%", label)) / 100          # the strictest target sharing this row
            within = ctp / (ctp + cfn) >= target - 1e-9 and cfp / (cfp + ctn) <= 0.2 + 1e-9
            check(within or "tidak tercapai di calibration" in creport, f"{label}: chosen configuration meets its constraints on the calibration split")

        code, log = run(["cascade", "--labels", str(labels), "--scores", str(scores), "--out", str(d / "c2"), "--person-share", "1.5"])
        check(code != 0 and "--person-share" in log and "Traceback" not in log, "cascade: invalid --person-share -> clear error")


# ------------------------------------------------------------------ 3. end to end

def e2e():
    import cv2
    server = ThreadingHTTPServer(("127.0.0.1", 0), Mock)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    base = f"http://127.0.0.1:{server.server_address[1]}/v1"
    with tempfile.TemporaryDirectory() as t:
        tmp = Path(t)
        manifest, videos = build_videos(tmp)
        fake = tmp / "fakepkg" / "ultralytics"
        fake.mkdir(parents=True)
        (fake / "__init__.py").write_text(FAKE_ULTRALYTICS)
        env = {"PYTHONPATH": str(tmp / "fakepkg")}
        gt = tmp / "gt"

        code, log = run(["sample", "--manifest", str(manifest), "--video-root", str(videos), "--out", str(gt),
                         "--per-class", "8", "--per-video", "6"])
        check(code == 0, f"sample exits 0 ({log[-200:] if code else 'ok'})")
        rows = list(csv.DictReader(open(gt / "labels.csv", encoding="utf-8")))
        check(len(rows) == 144, f"sample wrote 144 frames (got {len(rows)})")
        check(all((gt / r["path"]).exists() for r in rows), "every frame file exists")
        img = cv2.imread(str(gt / rows[0]["path"]))
        check(max(img.shape[:2]) == 640, f"frames are scaled to 640 px on the long side (got {img.shape[:2]})")
        check({r["split"] for r in rows} == {"calibration", "test"}, "both splits present")
        by_video = {}
        for r in rows:
            by_video.setdefault(r["video_id"], set()).add(r["split"])
        check(all(len(v) == 1 for v in by_video.values()), "each video sits in exactly one split")
        check(all(r["has_person"] == "" for r in rows), "labels start blank (no detector pre-fill)")

        # manifest paths that do not match the folder layout are resolved by file name; a wrong root fails loudly
        alt = tmp / "manifest_nested.csv"
        with open(manifest, newline="") as f_in, open(alt, "w", newline="") as f_out:
            rd = csv.DictReader(f_in)
            wr2 = csv.DictWriter(f_out, fieldnames=rd.fieldnames)
            wr2.writeheader()
            for r_ in rd:
                wr2.writerow({**r_, "relative_path": "Some\\Other\\Nesting\\" + r_["relative_path"]})
        code, log = run(["sample", "--manifest", str(alt), "--video-root", str(videos), "--out", str(tmp / "gt2"),
                         "--per-class", "1", "--per-video", "2"])
        check(code == 0 and len(list(csv.DictReader(open(tmp / "gt2" / "labels.csv")))) == 6,
              f"sample finds videos by file name when the manifest path differs ({log[-150:] if code else 'ok'})")
        code, log = run(["sample", "--manifest", str(alt), "--video-root", str(tmp / "nowhere"), "--out", str(tmp / "gt3"),
                         "--per-class", "1", "--per-video", "2"])
        check(code != 0 and "not found" in log and "--video-root" in log, "wrong --video-root -> clear error, non-zero exit")

        # annotate from the marker block, with two tags on some positives
        for r in rows:
            im = cv2.imread(str(gt / r["path"]))
            r["has_person"] = "1" if im[:8, :8].mean() > 128 else "0"
            if r["has_person"] == "1" and zlib.crc32(r["frame_id"].encode()) % 4 == 0:
                r["difficulty"] = "partial"
        with open(gt / "labels.csv", "w", newline="", encoding="utf-8") as f:
            wr = csv.DictWriter(f, fieldnames=pde.LABEL_FIELDS)
            wr.writeheader()
            wr.writerows(rows)
        npos = sum(r["has_person"] == "1" for r in rows)
        check(30 < npos < 114, f"fixture has both classes (positives {npos}/144)")

        blank = gt / "labels_blank.csv"
        with open(blank, "w", newline="", encoding="utf-8") as f:
            wr = csv.DictWriter(f, fieldnames=pde.LABEL_FIELDS)
            wr.writeheader()
            wr.writerows([{**r, "has_person": ""} for r in rows])
        code, log = run(["score", "--labels", str(blank), "--out", str(gt / "x.jsonl"), "--endpoint", base], env)
        check(code != 0 and "No labeled frames" in log and "Traceback" not in log, "score with no labels -> clear message, no traceback")
        noyolo = tmp / "noyolo" / "ultralytics"
        noyolo.mkdir(parents=True)
        (noyolo / "__init__.py").write_text("raise ImportError('not installed')")
        code, log = run(["score", "--labels", str(gt / "labels.csv"), "--out", str(gt / "y.jsonl"), "--detectors", "yolo"],
                        {"PYTHONPATH": str(tmp / "noyolo")})
        check(code != 0 and "CCTV_YOLO_PYTHON" in log and "Traceback" not in log, "missing ultralytics -> clear message, no traceback")

        scores = gt / "scores.jsonl"
        common = ["score", "--labels", str(gt / "labels.csv"), "--out", str(scores), "--endpoint", base,
                  "--model", "mock-vl", "--weights", "fake.pt"]
        code, log = run(common, env)
        check(code == 0, f"score exits 0 ({log[-300:] if code else 'ok'})")
        recs = jl(scores)
        check(len(recs) == 288 and {r["detector"] for r in recs} == {"yolo:fake", "llm:mock-vl"},
              f"288 records for two detectors (got {len(recs)})")
        req = next(r for r in Mock.requests if r.get("logprobs"))
        check(req["messages"][0]["content"] == pde.SYSTEM_PROMPT and req["top_logprobs"] == 5 and req["temperature"] == 0
              and req["response_format"]["json_schema"]["strict"] is True and req["max_tokens"] == 30,
              "LLM request has the prompt, schema, logprobs and constants of the app")
        errors = [r for r in recs if r["detector"].startswith("llm") and r["score"] is None]
        check(0 < len(errors) < 30, f"invalid answers are recorded, not fatal ({len(errors)} injected failures)")
        check(all(r["score"] is not None and r["latency_s"] >= 0 for r in recs if r["detector"].startswith("yolo")), "YOLO scores and latency recorded")
        n_requests = len(Mock.requests)
        code, log = run(common + ["--no-warmup"], env)
        retried = len(Mock.requests) - n_requests
        check(code == 0 and retried == len(errors), f"rerun retries only the failed frames ({retried} of {len(errors)}); successful ones are not scored twice")
        check(len(jl(scores)) == 288 + len(errors), "retried frames are appended (the newest record wins), nothing else is duplicated")

        # model/server problems stop immediately with advice instead of writing hundreds of errors
        bad = gt / "bad.jsonl"
        code, log = run(["score", "--labels", str(gt / "labels.csv"), "--out", str(bad), "--endpoint", base,
                         "--model", "missing-model", "--detectors", "llm"], env)
        check(code != 0 and "ollama list" in log and "Traceback" not in log and not bad.exists() or (bad.exists() and not bad.read_text().strip()),
              "model not found -> stops at once with advice, nothing recorded")
        code, log = run(["score", "--labels", str(gt / "labels.csv"), "--out", str(bad), "--endpoint", base,
                         "--model", "always-bad", "--detectors", "llm", "--no-warmup"], env)
        check(code != 0 and "5 failures in a row" in log and len(jl(bad)) == 5, "five failures in a row -> stops after five records")

        out = gt / "report"
        code, log = run(["analyze", "--labels", str(gt / "labels.csv"), "--scores", str(scores), "--out", str(out),
                         "--n-boot", "300", "--recall-target", "0.9"])
        check(code == 0, f"analyze exits 0 ({log[-400:] if code else 'ok'})")
        for name in ("person_report.md", "summary.json", "person_curves.png", "curve_yolo_fake.csv", "curve_llm_mock-vl.csv"):
            check((out / name).exists(), f"{name} written")
        summary = json.loads((out / "summary.json").read_text())
        report = (out / "person_report.md").read_text(encoding="utf-8")
        check("Selisih terhadap yolo:fake" in report and "Kecepatan" in report and "Person threshold" in report, "report has the expected sections")

        # independent recomputation
        lab = {r["frame_id"]: r for r in rows}
        sc = {}
        for r in recs:
            sc[(r["frame_id"], r["detector"])] = -1.0 if r["score"] is None else r["score"]
        for det in ("yolo:fake", "llm:mock-vl"):
            cal = [(int(lab[f]["has_person"]), sc[(f, det)]) for f in lab if lab[f]["split"] == "calibration"]
            pos = sorted((s for y, s in cal if y == 1), reverse=True)
            k = math.ceil(0.9 * len(pos) - 1e-9)
            expect_t = pde.floor6(pos[k - 1]) if pos[k - 1] >= 0 else 0.0
            got = summary["detectors"][det]
            check(close(got["threshold"], expect_t, 1e-9), f"{det}: threshold {got['threshold']} equals independent value {expect_t}")
            tst = [f for f in lab if lab[f]["split"] == "test"]
            tp = sum(1 for f in tst if lab[f]["has_person"] == "1" and sc[(f, det)] >= expect_t)
            fp = sum(1 for f in tst if lab[f]["has_person"] == "0" and sc[(f, det)] >= expect_t)
            npos_t = sum(1 for f in tst if lab[f]["has_person"] == "1")
            check(got["test_counts"]["tp"] == tp and got["test_counts"]["fp"] == fp, f"{det}: test counts tp={tp} fp={fp} match")
            check(close(got["test"]["recall"]["value"], tp / npos_t), f"{det}: test recall matches")
            vids = {}
            for f in tst:
                v = vids.setdefault(lab[f]["video_id"], [False, False])
                if lab[f]["has_person"] == "1":
                    v[0] = True
                if sc[(f, det)] >= expect_t:
                    v[1] = True
            has = [v for v in vids.values() if v[0]]
            vr = sum(1 for v in has if v[1]) / len(has)
            check(close(got["test"]["video_recall"]["value"], vr), f"{det}: video-level gate recall matches ({vr:.3f})")
            lo, hi = got["test"]["recall"]["ci"]
            check(lo <= got["test"]["recall"]["value"] <= hi, f"{det}: bootstrap interval contains the point estimate")
        expect_invalid = len(errors)
        check(summary["detectors"]["llm:mock-vl"]["invalid"] == expect_invalid, f"invalid LLM answers counted ({expect_invalid})")
        d = summary["deltas_vs_reference"]["llm:mock-vl"]["recall"]["value"]
        check(close(d, summary["detectors"]["llm:mock-vl"]["test"]["recall"]["value"] - summary["detectors"]["yolo:fake"]["test"]["recall"]["value"]),
              "paired delta equals the difference of the two point estimates")
        check("partial" in report, "per-difficulty table appears when tags exist")

        # a calibration split without person frames must stop with a clear message
        broken = gt / "labels_broken.csv"
        with open(broken, "w", newline="", encoding="utf-8") as f:
            wr = csv.DictWriter(f, fieldnames=pde.LABEL_FIELDS)
            wr.writeheader()
            for r in rows:
                wr.writerow({**r, "has_person": "0" if r["split"] == "calibration" else r["has_person"]})
        code, log = run(["analyze", "--labels", str(broken), "--scores", str(scores), "--out", str(gt / "r2"), "--n-boot", "50"])
        check(code != 0 and "no person frames" in log, "no positives in calibration -> clear error")
    server.shutdown()


if __name__ == "__main__":
    unit_tests()
    label_tool()
    analysis_scenarios()
    e2e()
    print("\nAll checks passed.")