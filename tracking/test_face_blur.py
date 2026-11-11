"""Tests for face_blur.py. YOLO and the face detector are replaced by stubs that return scripted boxes,
and the media are synthetic, so this needs opencv, numpy and ffmpeg but no real weights and no real person.

Set CCTV_TEST_FACE_IMAGE to a photo of a face to also run the real face detector on it.
Run: python3 tracking/test_face_blur.py
"""
import json, os, shutil, subprocess, sys, tempfile
from pathlib import Path

import cv2
import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import face_blur as fb

FAILED = []
def check(ok, name):
    print(("ok   " if ok else "FAIL ") + name)
    if not ok: FAILED.append(name)


class StubFaces:
    kind = "stub"
    def __init__(self, per_frame=None): self.per_frame, self.calls = per_frame or [], 0
    def find(self, frame):
        boxes = self.per_frame[min(self.calls, len(self.per_frame) - 1)] if self.per_frame else []
        self.calls += 1
        return boxes


def noise(h=240, w=320, seed=1):
    return np.random.default_rng(seed).integers(0, 256, (h, w, 3), dtype=np.uint8)

def colours(region):
    """Distinct colours in a region: a photo of a face has thousands, a pixelated one a few dozen at most."""
    return len({tuple(p) for p in region.reshape(-1, 3).tolist()})

def spread(region):
    """How much a region varies; a pixelated head is a few flat blocks, noise is not."""
    return float(region.astype(np.float32).std())


# --- geometry
standing = (100, 40, 140, 160)              # 40 x 120 box
head = fb.head_region(standing)
check(head[1] == 40 and head[3] - head[1] < 0.3 * 120 and head[0] >= 100 and head[2] <= 140, "head region is the top of a standing person")
check(fb.head_region((0, 0, 0, 10)) is None, "empty box has no head region")
check(fb.head_region((10, 100, 130, 160)) == (10, 100, 130, 160), "a wide (lying) box is blurred whole")
check(fb.grow((10, 10, 30, 30), 0.5) == (0, 0, 40, 40), "face margin grows the box")

# --- pixelate
frame = noise()
before = frame.copy()
check(fb.pixelate(frame, (50, 50, 110, 110)), "pixelate reports it changed a region")
check(spread(frame[50:110, 50:110]) < spread(before[50:110, 50:110]) / 2, "pixelated region loses its detail")
check(np.array_equal(frame[:50], before[:50]) and np.array_equal(frame[:, 120:], before[:, 120:]), "pixels outside the region are untouched")
edge = noise()
check(fb.pixelate(edge, (-30, -30, 20, 20)) and not fb.pixelate(edge, (400, 400, 500, 500)), "regions are clamped to the frame; off-frame ones are skipped")
check(not fb.pixelate(noise(), (10, 10, 11, 11)), "a one-pixel region is skipped")

# --- head blur works without any face detector
frame = noise(); original = frame.copy()
blurrer = fb.FaceBlurrer(persons=lambda f: [(100, 40, 140, 160)], faces=StubFaces())
count = blurrer.blur(frame)
check(count == 1, "one person with no face found still gets one blurred head")
check(spread(frame[40:60, 105:135]) < spread(original[40:60, 105:135]) / 2, "...and that head is pixelated")
check(np.array_equal(frame[100:], original[100:]), "...while the body is left alone")

# --- faces from the face detector are blurred too, with margin
frame = noise(); original = frame.copy()
blurrer = fb.FaceBlurrer(persons=None, faces=StubFaces([[(200, 100, 230, 130)]]))
blurrer.blur(frame)
check(spread(frame[100:130, 200:230]) < spread(original[100:130, 200:230]) / 2, "a detected face is pixelated")
check(not np.array_equal(frame[95:100, 200:230], original[95:100, 200:230]), "the margin around the face is pixelated as well")

# --- no detectors at all is reported, and never crashes
finder = fb.FaceFinder(model_path="/nonexistent/model.onnx")
check(finder.kind in ("haar", "none"), f"missing YuNet model falls back ({finder.kind})")
check(isinstance(finder.find(noise()), list), "fallback finder still returns a list")

# --- OpenCV 5 has no CascadeClassifier: no crash, the head blur still works
_cascade = getattr(cv2, "CascadeClassifier", None)
if _cascade is not None: del cv2.CascadeClassifier
try:
    bare = fb.FaceFinder(model_path="/nonexistent/model.onnx")
    check(bare.kind == "none" and bare.find(noise()) == [], "without CascadeClassifier (OpenCV 5) the finder reports 'none' instead of crashing")
finally:
    if _cascade is not None: cv2.CascadeClassifier = _cascade

# --- real YuNet loads and runs (blank image: nothing to find)
if fb.DEFAULT_FACE_MODEL.is_file() and hasattr(cv2, "FaceDetectorYN"):
    real = fb.FaceFinder()
    check(real.kind == "yunet", "bundled YuNet model loads")
    check(real.find(np.full((240, 320, 3), 128, np.uint8)) == [], "YuNet finds no face in a blank image")
    sample = os.environ.get("CCTV_TEST_FACE_IMAGE")
    if sample and Path(sample).is_file():
        photo = cv2.imread(sample)
        found = real.find(photo)
        check(len(found) >= 1, f"YuNet finds a face in {Path(sample).name} ({len(found)})")
        if found:
            marked = photo.copy()
            fb.FaceBlurrer(None, real).blur(marked)
            x1, y1, x2, y2 = (int(v) for v in found[0])
            check(colours(marked[y1:y2, x1:x2]) <= (fb.BLOCKS + 1) ** 2 < colours(photo[y1:y2, x1:x2]), "...and the face is reduced to a few blocks")

# --- blur_images: in place, no leftovers
with tempfile.TemporaryDirectory() as tmp:
    tmp = Path(tmp)
    paths = []
    for i in range(3):
        path = tmp / f"{i}.png"; cv2.imwrite(str(path), noise(seed=i)); paths.append(path)
    n = fb.blur_images(paths, fb.FaceBlurrer(lambda f: [(100, 40, 140, 160)], StubFaces()))
    check(n == 3, "blur_images blurs one head per image")
    after = cv2.imread(str(paths[0]))
    check(spread(after[40:60, 105:135]) < spread(noise(seed=0)[40:60, 105:135]) / 2, "the image on disk is the blurred one")
    check(not list(tmp.glob("*.part.png")), "no temp files are left behind")
    try:
        fb.blur_images([tmp / "missing.png"], fb.FaceBlurrer(None, StubFaces())); ok = False
    except RuntimeError:
        ok = True
    check(ok, "an unreadable image raises instead of being skipped")

# --- interpolation
mid = fb.interpolate([(0, 0, 10, 10)], [(30, 0, 40, 10)], 0.5)
check(len(mid) == 1 and abs(mid[0][0] - 15) < 1e-6, "a head that moves between detections is interpolated")
held = fb.interpolate([(0, 0, 10, 10)], [(500, 500, 510, 510)], 0.5)
check(len(held) == 2, "unpaired heads are both held (over-blur, never under-blur)")

# --- blur_video: a moving 'head' of noise on a flat background
ffmpeg = shutil.which(os.environ.get("CCTV_FFMPEG", "ffmpeg"))
if ffmpeg:
    with tempfile.TemporaryDirectory() as tmp:
        tmp = Path(tmp)
        fps, count, size = 10, 30, 40
        source, target = tmp / "in.mp4", tmp / "out.mp4"
        writer = cv2.VideoWriter(str(source), cv2.VideoWriter_fourcc(*"mp4v"), fps, (320, 240))
        patch = noise(size, size, seed=5)
        def head_x(n): return 20 + n * 8          # moves 8 px per frame
        for n in range(count):
            f = np.full((240, 320, 3), 60, np.uint8)
            f[50:50 + size, head_x(n):head_x(n) + size] = patch
            writer.write(f)
        writer.release()
        # The stub person's head sits exactly on the noise patch; people are only seen every 5th frame.
        def persons(frame):
            ys, xs = np.where((np.abs(frame.astype(int) - 60) > 40).any(axis=2))   # survives the codec's smearing of the flat background
            if len(xs) == 0: return []
            cx = (xs.min() + xs.max()) / 2
            h = size / fb.HEAD_FRACTION                 # a box tall enough that its head region is exactly the patch
            w = size / 0.75
            return [(float(cx - w / 2), float(ys.min()), float(cx + w / 2), float(ys.min() + h))]
        total, regions = fb.blur_video(source, target, ffmpeg, fb.FaceBlurrer(persons, StubFaces()), detect_fps=2, fallback_fps=fps)
        check(total == count and target.exists(), "blur_video writes a video with every frame")
        check(not list(tmp.glob("*.part.mp4")), "no temp video is left behind")
        probe = subprocess.run([ffmpeg, "-hide_banner", "-i", str(target)], capture_output=True, text=True).stderr
        check("h264" in probe and "yuv420p" in probe, "output is H.264 / yuv420p (plays in a browser)")
        check("Audio" not in probe, "output carries no audio")
        cap = cv2.VideoCapture(str(target))
        frames = []
        while True:
            ok, f = cap.read()
            if not ok: break
            frames.append(f)
        check(len(frames) == count, f"output has {count} frames ({len(frames)})")
        leaked = [n for n, f in enumerate(frames) if spread(f[55:85, head_x(n) + 5:head_x(n) + 35]) > 20]
        check(not leaked, f"the head stays blurred on every frame, including between detections (leaked on {leaked})")
        # Compression smears a little, so the flat background just has to stay roughly flat.
        check(abs(float(frames[0][200:230, 20:60].mean()) - 60) < 8, "the rest of the frame is not damaged")
        try:
            fb.blur_video(tmp / "nothing.mp4", tmp / "x.mp4", ffmpeg, fb.FaceBlurrer(None, StubFaces())); ok = False
        except RuntimeError:
            ok = True
        check(ok and not (tmp / "x.mp4").exists(), "an unreadable video raises and leaves no output")
else:
    print("skip video tests: ffmpeg not found")

print()
if FAILED:
    print("FAILED:", ", ".join(FAILED)); sys.exit(1)
print("all passed")
