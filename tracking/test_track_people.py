"""Tests for track_people.py: hybrid candidates, motion-only mode, frame choice.

YOLO is replaced by a stub that returns scripted boxes, and the video is a synthetic clip, so
this needs opencv and ultralytics importable but no real weights and no real person.
Run: python3 tracking/test_track_people.py
"""
import json, sys, tempfile, types
from pathlib import Path

import cv2
import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import track_people as tp

FAILED = []
def check(ok, name):
    print(("ok   " if ok else "FAIL ") + name)
    if not ok: FAILED.append(name)


class Boxes:
    def __init__(self, rows):
        self.xyxy = types.SimpleNamespace(tolist=lambda: [r[:4] for r in rows])
        self.conf = types.SimpleNamespace(tolist=lambda: [r[4] for r in rows])
    def __len__(self): return len(self.xyxy.tolist())


class StubYolo:
    """Returns the scripted detections for call number n (one call per sampled frame)."""
    script = []
    def __init__(self, weights): self.calls = 0
    def predict(self, frame, **kw):
        floor = kw["conf"]
        rows = [r for r in self.script[min(self.calls, len(self.script) - 1)] if r[4] >= floor]
        self.calls += 1
        return [types.SimpleNamespace(boxes=Boxes(rows) if rows else None)]


def make_video(path, seconds=4, fps=10):
    writer = cv2.VideoWriter(str(path), cv2.VideoWriter_fourcc(*"mp4v"), fps, (320, 240))
    for _ in range(seconds * fps):
        writer.write(np.full((240, 320, 3), 90, np.uint8))
    writer.release()


def run(script, **spec):
    StubYolo.script = script
    tp.YOLO = StubYolo
    with tempfile.TemporaryDirectory() as tmp:
        tmp = Path(tmp)
        make_video(tmp / "v.mp4")
        manifest = tmp / "in.json"
        manifest.write_text(json.dumps({"video": str(tmp / "v.mp4"), "fps": 10, "max_frames": 150,
                                        "sample_interval": 0.5, "model_min_gap": 1.0, **spec}))
        sys.argv = ["track_people.py", str(manifest), "unused.pt"]
        tp.main()
        result = json.loads((tmp / "tracking-result.json").read_text())
        pngs = sorted(p.name for p in (tmp / "tracked").glob("*.png"))
        return result, pngs


def person(x, conf, h=100):          # a box of height h centred at x
    return [x, 50, x + 40, 50 + h, conf]


# 8 samples (0.0 .. 3.5 s): none, low, low, high, high, none, low, high
NONE, LOW, HIGH = [], [person(10, 0.15)], [person(10, 0.8)]
script = [NONE, LOW, LOW, HIGH, HIGH, NONE, LOW, HIGH]

# 1. Plain YOLO gate (no verify_low): only >= conf frames survive, nothing is marked uncertain.
r, pngs = run(script, conf=0.5)
check([f["timestamp"] for f in r["frames"]] == [1.5, 3.5], "plain gate keeps only confident frames")
check(not any(f["uncertain"] for f in r["frames"]), "plain gate marks nothing uncertain")
check(not any("uncertain" in m for m in r["motion"]), "plain gate leaves motion entries unmarked")

# 2. Hybrid band: the 0.15 candidates appear, flagged, with their score; confident ones are not.
r, pngs = run(script, conf=0.5, verify_low=0.1)
by_t = {f["timestamp"]: f for f in r["frames"]}
check(sorted(by_t) == [0.5, 1.5, 3.5], "hybrid band keeps uncertain frames, but a confident one wins its gap (3.5 over 3.0)")
check(by_t[0.5]["uncertain"] is True and by_t[0.5]["score"] == 0.15, "uncertain frame carries its YOLO score")
check(by_t[1.5]["uncertain"] is False and by_t[1.5]["score"] == 0.8, "confident frame is not marked uncertain")
check([m["timestamp"] for m in r["motion"] if m.get("uncertain")] == [0.5, 1.0, 3.0], "motion marks uncertain samples")
check(len(r["samples"]) == 8, "score trace still covers every sample")

# 3. A verify_low below the old LOG_FLOOR still reaches YOLO (predict floor follows verify_low).
r, _ = run([[person(10, 0.03)]], conf=0.5, verify_low=0.02)
check(len(r["frames"]) >= 1 and r["frames"][0]["uncertain"], "verify_low under 0.05 lowers the predict floor")

# 4. verify_low above conf is clamped: behaves like the plain gate.
r, _ = run(script, conf=0.5, verify_low=0.9)
check([f["timestamp"] for f in r["frames"]] == [1.5, 3.5], "verify_low is clamped to conf")

# 5. Motion only: no frames, no images, motion + samples still produced with speeds.
walker = [[person(10 + 12 * i, 0.9)] for i in range(8)]   # 12 px / 0.5 s on a 100 px person
r, pngs = run(walker, conf=0.5, motion_only=True)
check(r["frames"] == [] and pngs == [], "motion_only writes no frames")
speeds = [m["fastest"] for m in r["motion"] if m["fastest"] is not None]
check(len(r["motion"]) == 8 and len(speeds) == 7, "motion_only still measures speed between samples")
check(all(m["motion"] in ("still", "walking") for m in r["motion"] if m["motion"]), "slow walker is not 'running'")
runner = [[person(10 + 130 * i, 0.9)] for i in range(5)]   # 1.3 body-heights per 0.5 s = 2.6/s
r, _ = run(runner, conf=0.5, motion_only=True)
check(any(m["motion"] == "running" for m in r["motion"]), "fast mover is classified as running")

# 6. choose_frames: a confirmed frame inside the gap replaces an uncertain one; otherwise first wins.
recs = [{"timestamp": 0.0, "uncertain": True}, {"timestamp": 0.5, "uncertain": False},
        {"timestamp": 1.0, "uncertain": True}, {"timestamp": 1.5, "uncertain": True}]
picked = tp.choose_frames(recs, 1.0)
check([p["timestamp"] for p in picked] == [0.5, 1.5], "confirmed frame takes the slot of an uncertain one in the same gap")
plain = [{"timestamp": t / 2} for t in range(6)]
check([p["timestamp"] for p in tp.choose_frames(plain, 1.0)] == [0.0, 1.0, 2.0], "plain frames keep the old first-in-gap behaviour")

print("\n%d failed" % len(FAILED) if FAILED else "\nall passed")
sys.exit(1 if FAILED else 0)