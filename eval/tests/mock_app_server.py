#!/usr/bin/env python3
"""Minimal stand-in for the CCTV app's HTTP API, for dry-running the evaluation tools without a GPU.

It mimics only what run_app_eval.py uses: /api/health, /api/state, /api/cameras, /api/settings,
/api/queue/start, POST /api/videos (raw body + X-Filename) and POST /api/videos/{id}/{action}.
Behaviour is derived from the file name, so tests are deterministic:
  *toolong*  -> 422 "Video exceeds the 5-minute limit."
  *broken*   -> job ends as failed
  *noperson* -> job completes with noPeople=true and suspicious="no" (model never called)
  otherwise  -> positive-looking names (not 'normal') are flagged yes with probability P_POS, Normal with P_NEG
"""
import hashlib
import json
import queue
import threading
import time
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import unquote

P_POS, P_NEG = 0.7, 0.1
SETTINGS = {"mode": "local", "endpoint": "http://127.0.0.1:11434/v1", "model": "mock", "personLabels": True,
            "flagActivity": "", "siteContextEnabled": False, "siteContext": "", "weaponEnabled": True,
            "batchProcessing": False}


def unit(name, salt):
    return int(hashlib.sha256((salt + name).encode()).hexdigest()[:8], 16) / 0xFFFFFFFF


def decide(name):
    lower = name.lower()
    if "broken" in lower:
        return {"status": "failed", "stage": "Failed", "error": "Model returned malformed JSON."}
    if "noperson" in lower:
        return {"status": "completed", "stage": "Completed", "suspicious": "no", "weapon": "no", "noPeople": True,
                "description": "Person detection only", "timestamps": []}
    p = P_NEG if "normal" in lower else P_POS
    yes = unit(name, "flag") < p
    return {"status": "completed", "stage": "Completed", "suspicious": "yes" if yes else "no", "weapon": "no",
            "noPeople": False, "description": f"mock description for {name}", "timestamps": [float(i) for i in range(12)],
            "rawText": "{}", "model": "mock", "backendMode": "local", "personLabels": True}


class State:
    def __init__(self, delay, upload_delay=0.35, empty_pause=0.25):
        self.jobs, self.running, self.delay = {}, False, delay
        self.upload_delay, self.empty_pause = upload_delay, empty_pause
        self.order, self.lock, self.work = [], threading.Lock(), queue.Queue()
        self.upload_count = 0
        threading.Thread(target=self.loop, daemon=True).start()

    def pause_if_empty(self):
        with self.lock:
            if not any(j["status"] == "queued" for j in self.jobs.values()):
                self.running = False

    def loop(self):
        while True:
            job_id = self.work.get()
            while not self.running:
                time.sleep(0.05)
            with self.lock:
                job = self.jobs.get(job_id)
                if not job or job["status"] != "queued":
                    continue
                job.update(status="processing", stage="Describing")
            time.sleep(self.delay)
            with self.lock:
                job = self.jobs.get(job_id)
                if job and job["status"] == "processing":
                    job.update(decide(job["name"]), elapsed=round(self.delay, 1))
                if not any(j["status"] == "queued" for j in self.jobs.values()):
                    self.running = False  # queue drained: the real app pauses itself

    def snapshot(self):
        with self.lock:
            return {"jobs": [dict(self.jobs[i]) for i in self.order if i in self.jobs], "running": self.running,
                    "settings": SETTINGS, "linkedFolder": None, "frameLimit": 150}


def make_handler(state):
    class H(BaseHTTPRequestHandler):
        def log_message(self, *a):
            pass

        def send(self, code, obj):
            raw = json.dumps(obj).encode()
            self.send_response(code)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(raw)))
            self.end_headers()
            self.wfile.write(raw)

        def body(self):
            n = int(self.headers.get("Content-Length", 0))
            return self.rfile.read(n) if n else b""

        def do_GET(self):
            if self.path == "/api/health":
                return self.send(200, {"ok": True, "backend": "mock", "version": "0"})
            if self.path == "/api/state":
                return self.send(200, state.snapshot())
            if self.path == "/api/cameras":
                return self.send(200, [])
            self.send(404, {"detail": "not found"})

        def do_POST(self):
            data = self.body()
            parts = self.path.strip("/").split("/")
            if self.path == "/api/settings":
                SETTINGS.update(json.loads(data))
                return self.send(200, {"ok": True})
            if self.path == "/api/queue/start":
                state.running = True
                # Like QueueStore.Take(): the real app pauses itself as soon as it finds nothing queued.
                threading.Timer(state.empty_pause, state.pause_if_empty).start()
                return self.send(200, {"ok": True})
            if self.path == "/api/videos":
                name = unquote(self.headers.get("X-Filename", "video.mp4"))
                if "toolong" in name.lower():
                    return self.send(422, {"detail": "Video exceeds the 5-minute limit."})
                time.sleep(state.upload_delay)  # the real upload (ffprobe + poster) takes a while
                state.upload_count += 1
                job = {"id": uuid.uuid4().hex, "name": name, "status": "queued", "stage": "Queued", "duration": 30.0,
                       "fps": 25.0, "bytes": len(data), "timestamps": [], "noPeople": False, "weaponEnabled": True}
                with state.lock:
                    state.jobs[job["id"]] = job
                    state.order.append(job["id"])
                state.work.put(job["id"])
                return self.send(200, job)
            if len(parts) == 4 and parts[:2] == ["api", "videos"]:
                job_id, action = parts[2], parts[3]
                with state.lock:
                    if action == "delete":
                        state.jobs.pop(job_id, None)
                    elif action == "cancel" and job_id in state.jobs:
                        state.jobs[job_id].update(status="cancelled", stage="Stopped")
                return self.send(200, {"ok": True})
            self.send(404, {"detail": "not found"})

    return H


def serve(port=0, delay=0.05):
    state = State(delay)
    server = ThreadingHTTPServer(("127.0.0.1", port), make_handler(state))
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server, state


if __name__ == "__main__":
    import sys
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 8766
    srv, _ = serve(port, delay=1.0)
    print(f"mock app on http://127.0.0.1:{port}")
    threading.Event().wait()