"""Blur faces in images and videos.

Two independent sources decide what gets blurred, and everything either one finds is blurred:

1. The head of every detected person. A person box from the YOLO detector is cut down to its top
   part, so a face is covered even when the face detector misses it (tiny, turned away, motion blur).
2. A face detector. YuNet (OpenCV's FaceDetectorYN) when `face_detection_yunet_2023mar.onnx` sits
   next to this file or `CCTV_FACE_MODEL` points at one; otherwise OpenCV's Haar cascades when the
   installed build ships them (many pip builds do not). With neither, only the head regions are used.

Blur is pixelation, not a Gaussian blur: a heavy Gaussian blur can be partly undone, whereas
averaging a face down to a handful of blocks throws the detail away for good.

This is a privacy aid, not a guarantee. A person nobody detected is not blurred.

Usage (the app calls it like track_people.py):
    python face_blur.py <manifest.json> <yolo weights>
Either manifest may also carry "face_model": a YuNet .onnx file that replaces the bundled one (the app passes
CCTV_FACE_MODEL this way, because child processes do not inherit CCTV_* variables).
manifest, images:  {"mode": "images", "paths": ["a.png", ...]}          blurred in place
manifest, video:   {"mode": "video", "video": in, "output": out.mp4, "ffmpeg": "ffmpeg", "fps": 25,
                    "detect_fps": 5}                                      writes a blurred H.264 copy
"""
import bisect, json, math, os, subprocess, sys
from pathlib import Path
import cv2
import numpy as np

HERE = Path(__file__).resolve().parent
DEFAULT_FACE_MODEL = HERE / "face_detection_yunet_2023mar.onnx"

PERSON_FLOOR = 0.05       # person boxes down to this confidence get their head blurred (a miss costs more than a false blur)
FACE_SCORE = 0.5          # YuNet score threshold; lower than its usual 0.9 on purpose, for the same reason
HEAD_FRACTION = 0.22      # a standing person's head is about 13-15% of the box height; the rest is margin
FACE_MARGIN = 0.35        # grow face boxes by this fraction on each side (detectors return tight boxes)
WIDE_BOX = 0.9            # width/height above this means crouching or lying down: blur the whole box
DETECT_SIZE = 640         # longest side used for detection
BLOCKS = 5                # a blurred head is reduced to about this many blocks across
TIME_WINDOW = 2           # video: a face seen on frame n also covers frames n-2 .. n+2


# ----------------------------------------------------------------------------- geometry

def head_region(box):
    """Head area of a person box (x1, y1, x2, y2)."""
    x1, y1, x2, y2 = box
    w, h = x2 - x1, y2 - y1
    if h <= 0 or w <= 0:
        return None
    if w / h > WIDE_BOX:
        return (x1, y1, x2, y2)
    head_h = HEAD_FRACTION * h
    head_w = min(0.75 * w, 1.3 * head_h)
    cx = (x1 + x2) / 2
    return (cx - head_w / 2, y1, cx + head_w / 2, y1 + head_h)


def grow(box, margin):
    x1, y1, x2, y2 = box
    w, h = x2 - x1, y2 - y1
    return (x1 - w * margin, y1 - h * margin, x2 + w * margin, y2 + h * margin)


def pixelate(frame, box):
    """Pixelate one region of `frame` in place. Returns False when the region is empty."""
    height, width = frame.shape[:2]
    x1, y1 = max(0, int(math.floor(box[0]))), max(0, int(math.floor(box[1])))
    x2, y2 = min(width, int(math.ceil(box[2]))), min(height, int(math.ceil(box[3])))
    if x2 - x1 < 2 or y2 - y1 < 2:
        return False
    region = frame[y1:y2, x1:x2]
    cols = max(1, min(BLOCKS, region.shape[1] // 2))
    rows = max(1, min(BLOCKS, region.shape[0] // 2))
    small = cv2.resize(region, (cols, rows), interpolation=cv2.INTER_AREA)
    frame[y1:y2, x1:x2] = cv2.resize(small, (region.shape[1], region.shape[0]), interpolation=cv2.INTER_NEAREST)
    return True


# ----------------------------------------------------------------------------- detectors

class FaceFinder:
    """Face boxes from YuNet or Haar cascades. `kind` says which one is in use ('yunet', 'haar', 'none')."""

    def __init__(self, model_path=None):
        self.kind, self._yunet, self._haar = "none", None, []
        path = Path(model_path or os.environ.get("CCTV_FACE_MODEL") or DEFAULT_FACE_MODEL)
        if path.is_file() and hasattr(cv2, "FaceDetectorYN"):
            try:
                self._yunet = cv2.FaceDetectorYN.create(str(path), "", (320, 320), FACE_SCORE, 0.3, 5000)
                self.kind = "yunet"
                return
            except cv2.error:
                self._yunet = None
        data = getattr(getattr(cv2, "data", None), "haarcascades", None)
        if data:
            for name in ("haarcascade_frontalface_default.xml", "haarcascade_profileface.xml"):
                cascade = cv2.CascadeClassifier(str(Path(data) / name))
                if not cascade.empty():
                    self._haar.append(cascade)
            if self._haar:
                self.kind = "haar"

    def find(self, frame):
        """Face boxes (x1, y1, x2, y2) in the pixel coordinates of `frame`."""
        height, width = frame.shape[:2]
        scale = min(1.0, DETECT_SIZE / max(height, width))
        small = cv2.resize(frame, (max(1, int(width * scale)), max(1, int(height * scale)))) if scale < 1 else frame
        boxes = []
        if self._yunet is not None:
            self._yunet.setInputSize((small.shape[1], small.shape[0]))
            _, found = self._yunet.detect(small)
            if found is not None:
                boxes = [(f[0], f[1], f[0] + f[2], f[1] + f[3]) for f in found]
        elif self._haar:
            gray = cv2.equalizeHist(cv2.cvtColor(small, cv2.COLOR_BGR2GRAY))
            for cascade in self._haar:
                for x, y, w, h in cascade.detectMultiScale(gray, 1.1, 3, minSize=(12, 12)):
                    boxes.append((x, y, x + w, y + h))
                # The profile cascade only finds faces looking one way; mirror to catch the other.
                if cascade is not self._haar[0]:
                    for x, y, w, h in cascade.detectMultiScale(cv2.flip(gray, 1), 1.1, 3, minSize=(12, 12)):
                        boxes.append((gray.shape[1] - x - w, y, gray.shape[1] - x, y + h))
        return [tuple(float(v) / scale for v in box) for box in boxes]


def yolo_person_boxes(weights):
    """A callable frame -> [(x1, y1, x2, y2)] using ultralytics YOLO (loaded lazily, CPU)."""
    os.environ.setdefault("YOLO_CONFIG_DIR", str(HERE / ".config"))
    from ultralytics import YOLO
    model = YOLO(weights)

    def detect(frame):
        result = model.predict(frame, classes=[0], conf=PERSON_FLOOR, imgsz=DETECT_SIZE, device="cpu", verbose=False)[0]
        boxes = result.boxes
        return [tuple(b) for b in boxes.xyxy.tolist()] if boxes is not None and len(boxes) else []
    return detect


class FaceBlurrer:
    """Finds what to blur on a frame and blurs it. `persons` is a callable frame -> person boxes, or None."""

    def __init__(self, persons=None, faces=None):
        self.persons = persons
        self.faces = faces if faces is not None else FaceFinder()

    def regions(self, frame, person_boxes=None):
        if person_boxes is None:
            person_boxes = self.persons(frame) if self.persons else []
        regions = [r for r in (head_region(b) for b in person_boxes) if r]
        regions += [grow(b, FACE_MARGIN) for b in self.faces.find(frame)]
        return regions

    def blur(self, frame, person_boxes=None):
        """Blur `frame` in place; returns how many regions were blurred."""
        return sum(pixelate(frame, r) for r in self.regions(frame, person_boxes))


# ----------------------------------------------------------------------------- images

def blur_images(paths, blurrer):
    blurred = 0
    for path in paths:
        frame = cv2.imread(str(path))
        if frame is None:
            raise RuntimeError(f"Cannot read image {path}")
        blurred += blurrer.blur(frame)
        # Write next to the original, then swap: a crash in the middle never leaves a half-written frame.
        part = Path(str(path) + ".part.png")
        if not cv2.imwrite(str(part), frame):
            raise RuntimeError(f"Cannot write image {path}")
        os.replace(part, path)
    return blurred


# ----------------------------------------------------------------------------- video

def _lerp(a, b, t):
    return tuple(a[i] + (b[i] - a[i]) * t for i in range(4))


def interpolate(before, after, t):
    """Head regions between two detections: boxes are paired by nearest centre and moved smoothly; unpaired boxes are held."""
    out, used = [], set()
    for box in before:
        cx, cy = (box[0] + box[2]) / 2, (box[1] + box[3]) / 2
        reach = 4 * max(box[2] - box[0], box[3] - box[1])
        best, best_d = None, reach
        for j, other in enumerate(after):
            if j in used:
                continue
            d = math.dist((cx, cy), ((other[0] + other[2]) / 2, (other[1] + other[3]) / 2))
            if d <= best_d:
                best, best_d = j, d
        if best is None:
            out.append(box)
        else:
            used.add(best)
            out += [_lerp(box, after[best], t)]
    out += [box for j, box in enumerate(after) if j not in used]
    return out


def blur_video(video, output, ffmpeg, blurrer, detect_fps=5.0, fallback_fps=25.0):
    """Write a face-blurred, browser-playable (H.264, no audio) copy of `video` to `output`."""
    cap = cv2.VideoCapture(str(video))
    if not cap.isOpened():
        raise RuntimeError("Cannot open video for face blur")
    fps = cap.get(cv2.CAP_PROP_FPS)
    fps = fps if fps and 1 <= fps <= 240 else fallback_fps
    step = max(1, round(fps / detect_fps))

    # Pass 1: faces on every frame, people every `step` frames. Only boxes are kept, never frames.
    heads, faces = {}, []
    index = 0
    while cap.grab():
        ok, frame = cap.retrieve()
        if not ok:
            raise RuntimeError("Could not decode a frame")
        faces.append([grow(b, FACE_MARGIN) for b in blurrer.faces.find(frame)])
        if index % step == 0 and blurrer.persons:
            heads[index] = [r for r in (head_region(b) for b in blurrer.persons(frame)) if r]
        last = frame
        index += 1
    cap.release()
    # Always look at the final frame too, so the frames after the last regular detection are interpolated, not held.
    if index and blurrer.persons and (index - 1) not in heads:
        heads[index - 1] = [r for r in (head_region(b) for b in blurrer.persons(last)) if r]
    total = index
    if total == 0:
        raise RuntimeError("Video has no frames")
    keys = sorted(heads)

    def heads_at(n):
        if not keys:
            return []
        if n <= keys[0]:
            return heads[keys[0]]
        if n >= keys[-1]:
            return heads[keys[-1]]
        at = bisect.bisect_left(keys, n)
        if keys[at] == n:
            return heads[n]
        lo, hi = keys[at - 1], keys[at]
        return interpolate(heads[lo], heads[hi], (n - lo) / (hi - lo))

    # Pass 2: decode again, blur, and pipe straight into ffmpeg.
    cap = cv2.VideoCapture(str(video))
    ok, frame = cap.read()
    if not ok:
        raise RuntimeError("Cannot reopen video for face blur")
    height, width = frame.shape[:2]
    part = Path(str(output) + ".part.mp4")
    command = [ffmpeg, "-y", "-loglevel", "error", "-f", "rawvideo", "-pix_fmt", "bgr24", "-s", f"{width}x{height}",
               "-r", f"{fps:.4f}", "-i", "-", "-an", "-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2",
               "-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-pix_fmt", "yuv420p",
               "-movflags", "+faststart", "-f", "mp4", str(part)]
    process = subprocess.Popen(command, stdin=subprocess.PIPE, stderr=subprocess.PIPE)
    regions_blurred = 0
    try:
        n = 0
        while ok and n < total:
            regions = list(heads_at(n))
            for m in range(max(0, n - TIME_WINDOW), min(total, n + TIME_WINDOW + 1)):
                regions += faces[m]
            regions_blurred += sum(pixelate(frame, r) for r in regions)
            process.stdin.write(frame.tobytes())
            n += 1
            ok, frame = cap.read()
        process.stdin.close()
        error = process.stderr.read().decode("utf-8", "replace")
        if process.wait() != 0 or not part.exists():
            raise RuntimeError("ffmpeg could not write the blurred video: " + error[-300:])
    except BaseException:
        process.kill()
        process.wait()
        part.unlink(missing_ok=True)
        raise
    finally:
        cap.release()
    os.replace(part, output)
    return total, regions_blurred


# ----------------------------------------------------------------------------- command line

def main():
    manifest, weights = Path(sys.argv[1]), sys.argv[2]
    spec = json.loads(manifest.read_text())
    blurrer = FaceBlurrer(yolo_person_boxes(weights), FaceFinder(spec.get("face_model")))
    if spec.get("mode") == "video":
        frames, regions = blur_video(spec["video"], spec["output"], spec.get("ffmpeg", "ffmpeg"), blurrer,
                                     float(spec.get("detect_fps", 5)), float(spec.get("fps", 25)))
        result = {"frames": frames, "regions": regions}
    else:
        paths = spec.get("paths", [])
        result = {"images": len(paths), "regions": blur_images(paths, blurrer)}
    result["face_detector"] = blurrer.faces.kind
    print(json.dumps(result))


if __name__ == "__main__":
    main()
