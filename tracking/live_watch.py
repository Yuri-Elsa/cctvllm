"""Watch one live camera stream and save a short clip whenever a person is in view.

This is the "live mode" of the app. Instead of cutting the stream into fixed segments and checking each one
afterwards, this process looks at the stream all the time (a person detector a couple of times per second) and
keeps the last few seconds in memory. When a person shows up it starts a clip that already includes that
pre-roll, keeps recording while the person is there, and ends it a few seconds after the person has gone.
The finished clip lands in the camera's folder, where the app imports and describes it like any other video.

It talks to the app through stdout, one JSON object per line:
  {"event":"starting"}                                    the process is up (the model is loading)
  {"event":"hb","people":0,"recording":false}             every ~2 s while pictures arrive: the stream is alive
  {"event":"person","people":2}                           a person was just confirmed and a clip begins
  {"event":"clip","file":"20261028_153000.mp4","seconds":14.5}   a finished clip is in the camera folder
  {"event":"error","message":"..."}                       a clip could not be written
The stream address comes from the LIVE_RTSP_URL environment variable and never from the command line, where
other users of the machine could read it. A path to a video file works as well: it is replayed in real time,
which is how to try this without a camera.

Exit codes: 0 stopped, 2 bad setup, 3 ffmpeg cannot encode H.264, 4 the stream failed (the app restarts it).
"""
import argparse
import collections
import json
import os
import subprocess
import sys
import threading
import time
from datetime import datetime, timedelta
from pathlib import Path

os.environ.setdefault("YOLO_CONFIG_DIR", str(Path(__file__).resolve().parent / ".config"))
sys.path.insert(0, str(Path(__file__).resolve().parent))
from live_logic import ClipPlanner  # noqa: E402

HEARTBEAT_SECONDS = 2.0
NO_PICTURE_SECONDS = 30.0      # no first frame within this long: give up and let the app restart us
FROZEN_SECONDS = 10.0          # the newest frame is older than this: the stream froze


def emit(**event):
    print(json.dumps(event), flush=True)


def fail(message):
    print(message, file=sys.stderr, flush=True)


def is_stream(url):
    return url.lower().startswith(("rtsp://", "rtsps://"))


class Reader(threading.Thread):
    """Keeps only the newest frame.

    Reading in its own thread matters: while the detector is busy the camera keeps sending, and a plain read loop
    would work through a growing backlog, so the picture (and every alert built on it) would fall further and
    further behind real time.
    """

    def __init__(self, cv2, url, timeout_ms):
        super().__init__(daemon=True)
        self.cv2, self.url, self.timeout_ms = cv2, url, timeout_ms
        self.lock = threading.Lock()
        self.frame = None
        self.stamp = 0.0
        self.error = None
        self.stop = threading.Event()

    def run(self):
        cv2 = self.cv2
        cap = None
        try:
            try:
                cap = cv2.VideoCapture(self.url, cv2.CAP_FFMPEG,
                                       [cv2.CAP_PROP_OPEN_TIMEOUT_MSEC, self.timeout_ms,
                                        cv2.CAP_PROP_READ_TIMEOUT_MSEC, self.timeout_ms])
            except (TypeError, AttributeError):   # an OpenCV build without the timeout constants or the parameter list
                cap = cv2.VideoCapture(self.url, cv2.CAP_FFMPEG)
            if not cap.isOpened():
                self.error = "cannot open the stream"
                return
            replay_delay = 0.0
            if not is_stream(self.url):   # a video file: play it at its own speed instead of as fast as possible
                replay_delay = 1.0 / (cap.get(cv2.CAP_PROP_FPS) or 25.0)
            while not self.stop.is_set():
                ok, frame = cap.read()
                if not ok:
                    self.error = "the stream stopped"
                    return
                with self.lock:
                    self.frame, self.stamp = frame, time.monotonic()
                if replay_delay:
                    time.sleep(replay_delay)
        except Exception as ex:   # never let a URL (with its password) leak through an exception text
            self.error = f"reading failed ({type(ex).__name__})"
        finally:
            if cap is not None:
                cap.release()

    def latest(self):
        with self.lock:
            return self.frame, self.stamp


class ClipWriter:
    """Encodes frames to H.264 mp4 through ffmpeg (browsers cannot play what OpenCV's own writer produces).

    The clip is written under a .part name and renamed only when it is complete, so the app never imports a
    half-written file.
    """

    def __init__(self, ffmpeg, out_dir, start, size, fps):
        self.width, self.height = size
        name = start.strftime("%Y%m%d_%H%M%S")
        while (out_dir / f"{name}.mp4").exists() or (out_dir / f"{name}.part").exists():
            start += timedelta(seconds=1)   # two clips in the same second: keep names unique and in order
            name = start.strftime("%Y%m%d_%H%M%S")
        self.final = out_dir / f"{name}.mp4"
        self.part = out_dir / f"{name}.part"
        self.frames = 0
        self.failed = False
        self.proc = subprocess.Popen(
            [ffmpeg, "-hide_banner", "-loglevel", "error", "-nostdin",
             "-f", "rawvideo", "-pix_fmt", "bgr24", "-s", f"{self.width}x{self.height}", "-r", str(fps), "-i", "-",
             "-an", "-c:v", "libx264", "-preset", "veryfast", "-crf", "26", "-pix_fmt", "yuv420p",
             "-movflags", "+faststart", "-f", "mp4", str(self.part)],
            stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)

    def write(self, frame):
        if self.failed or frame.shape[1] != self.width or frame.shape[0] != self.height:
            return   # the stream changed size in the middle of a clip: skip rather than corrupt it
        try:
            self.proc.stdin.write(frame.tobytes())
            self.frames += 1
        except (BrokenPipeError, OSError):
            self.failed = True

    def finish(self):
        """Closes the clip. Returns its final path, or None when it could not be written."""
        try:
            self.proc.stdin.close()
        except OSError:
            pass
        try:
            self.proc.wait(timeout=60)
        except subprocess.TimeoutExpired:
            self.proc.kill()
            self.proc.wait()
        detail = self.proc.stderr.read().decode("utf-8", "replace").strip().splitlines()
        if self.proc.returncode != 0 or self.failed or self.frames == 0 or not self.part.exists():
            remove(self.part)
            emit(event="error", message="a clip could not be encoded" + (": " + detail[-1] if detail else ""))
            return None
        for attempt in range(6):   # on Windows an antivirus scan can block the rename for a moment
            try:
                os.replace(self.part, self.final)
                return self.final
            except PermissionError:
                time.sleep(0.1 * (attempt + 1))
        emit(event="error", message="a finished clip could not be moved into place")
        return None


def remove(path):
    try:
        path.unlink(missing_ok=True)
    except OSError:
        pass   # on Windows a scanner can hold the file for a moment; the next start sweeps up leftovers


def has_h264_encoder(ffmpeg):
    try:
        out = subprocess.run([ffmpeg, "-hide_banner", "-encoders"], capture_output=True, text=True, timeout=30).stdout
    except (OSError, subprocess.SubprocessError):
        return False
    return "libx264" in out


def shrink(cv2, frame, max_side):
    """Scale so the longest side is at most max_side, and make both sides even (H.264 needs that)."""
    height, width = frame.shape[:2]
    scale = min(1.0, max_side / max(width, height))
    new_w, new_h = int(width * scale) // 2 * 2, int(height * scale) // 2 * 2
    if (new_w, new_h) == (width, height):
        return frame
    return cv2.resize(frame, (new_w, new_h), interpolation=cv2.INTER_AREA)


def count_people(model, frame, conf):
    result = model.predict(frame, classes=[0], conf=conf, imgsz=640, device="cpu", verbose=False)[0]
    return len(result.boxes)


def parse_args(argv=None):
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("--camera", required=True)
    p.add_argument("--out", required=True, help="the camera's folder; clips are written here as yyyyMMdd_HHmmss.mp4")
    p.add_argument("--ffmpeg", default="ffmpeg")
    p.add_argument("--weights", required=True)
    p.add_argument("--conf", type=float, default=0.25, help="person confidence threshold")
    p.add_argument("--fps", type=int, default=6, help="frames per second kept in the clips")
    p.add_argument("--preroll", type=float, default=5.0, help="seconds before the first detection that go into a clip")
    p.add_argument("--postroll", type=float, default=4.0, help="seconds without a person before a clip ends")
    p.add_argument("--max-clip", type=float, default=20.0, help="a clip is closed after this long while someone is in view")
    p.add_argument("--max-side", type=int, default=960, help="longest side of the saved picture, in pixels")
    p.add_argument("--confirm", type=int, default=2, help="detections in a row that confirm a person")
    p.add_argument("--detect-every", type=float, default=0.5, help="seconds between person detections")
    return p.parse_args(argv)


def main():
    args = parse_args()
    url = os.environ.get("LIVE_RTSP_URL", "")
    if not url:
        fail("LIVE_RTSP_URL is not set.")
        return 2
    out_dir = Path(args.out)
    out_dir.mkdir(parents=True, exist_ok=True)
    for leftover in out_dir.glob("*.part"):   # a clip that was open when the last run was killed
        remove(leftover)
    if not has_h264_encoder(args.ffmpeg):
        fail("This ffmpeg has no libx264 encoder, so live clips cannot be written. Use a full ffmpeg build.")
        return 3

    emit(event="starting")
    if is_stream(url):
        os.environ.setdefault("OPENCV_FFMPEG_CAPTURE_OPTIONS", "rtsp_transport;tcp")
    import cv2
    from ultralytics import YOLO
    model = YOLO(args.weights)
    emit(event="starting")

    began = time.monotonic()
    reader = Reader(cv2, url, 8000)
    reader.start()
    planner = ClipPlanner(args.confirm, args.postroll, args.max_clip)
    buffer = collections.deque()   # (monotonic time, wall clock time, frame): the pre-roll
    writer = None
    step = 1.0 / args.fps
    next_tick = time.monotonic()
    last_detect = -1e9
    last_beat = -1e9
    people = 0

    def close_clip():
        nonlocal writer
        if writer is None:
            return
        done, frames = writer, writer.frames
        writer = None
        path = done.finish()
        if path is not None:
            emit(event="clip", file=path.name, seconds=round(frames / args.fps, 1))

    def open_clip(now, keep):
        nonlocal writer
        frames = [item for item in buffer if item[0] >= now - keep] or [buffer[-1]]
        first = frames[0][2]
        writer = ClipWriter(args.ffmpeg, out_dir, frames[0][1], (first.shape[1], first.shape[0]), args.fps)
        for _, _, frame in frames:
            writer.write(frame)

    try:
        while True:
            now = time.monotonic()
            if now < next_tick:
                time.sleep(min(next_tick - now, 0.02))
                continue
            next_tick = max(next_tick + step, now - step)   # never try to catch up on more than one missed tick

            frame, stamp = reader.latest()
            if reader.error:
                fail(reader.error)
                return 4
            if frame is None:
                if now - began > NO_PICTURE_SECONDS:
                    fail("no picture from the stream")
                    return 4
                continue
            if now - stamp > FROZEN_SECONDS:
                fail("the picture froze")
                return 4

            small = shrink(cv2, frame, args.max_side)
            buffer.append((now, datetime.now(), small))
            while buffer and now - buffer[0][0] > args.preroll + 1.0:
                buffer.popleft()
            if writer is not None:
                writer.write(small)

            if now - last_detect >= args.detect_every:
                last_detect = now
                people = count_people(model, small, args.conf)
                for action in planner.observe(now, people):
                    if action in ("stop", "rollover"):
                        close_clip()
                    if action in ("start", "rollover"):
                        open_clip(now, args.preroll if action == "start" else 1.0)
                    if action == "start":
                        emit(event="person", people=people)

            if now - last_beat >= HEARTBEAT_SECONDS:
                last_beat = now
                emit(event="hb", people=people, recording=writer is not None)
    except KeyboardInterrupt:
        return 0
    finally:
        reader.stop.set()
        planner.reset()
        close_clip()


if __name__ == "__main__":
    sys.exit(main() or 0)
