#!/usr/bin/env python3
"""Uji kegagalan (fault injection) untuk aplikasi CCTV, tanpa kamera dan tanpa Ollama.

Yang dijalankan adalah APLIKASI ASLI (dotnet CctvDemo.dll). Yang dipalsukan hanya dunia luarnya:
  * model     : server mirip-OpenAI di localhost yang bisa dimatikan, error 500, mengirim JSON rusak, lambat
  * webhook   : penerima notifikasi yang bisa mati, membalas 500, atau menggantung
  * SMTP      : diarahkan ke port tertutup, jadi kanal email selalu gagal (untuk uji agen dan retry per kanal)
Aplikasi dijalankan dengan detektor orang "LLM", sehingga YOLO tidak diperlukan.

Setiap skenario memakai folder data baru dan proses aplikasi baru. Setelah itu dicek tiga hal:
  1. tidak ada alert yang hilang        (setiap video bertanda terkirim ke webhook)
  2. tidak ada alert ganda yang tak wajar (maksimal sekali per video, kecuali dinyatakan lain)
  3. aplikasi tetap hidup dan tidak ada pekerjaan yang macet

Skenario
  baseline        semua normal (kontrol; kalau ini gagal, hasil lain tidak berarti)
  model_down      model mati di tengah antrean, lalu hidup lagi dan video diulang
  model_bad       model mengirim JSON rusak / 500 / terpotong / memutus koneksi
  agent_faults    agen gagal / memilih ignore saat weapon=yes (guard) / kanal email mati
  webhook_outage  webhook mati atau membalas 500, lalu pulih
  crash_retrying  aplikasi di-kill (SIGKILL) atau dihentikan (SIGTERM) saat alert menunggu retry
  crash_waiting   aplikasi di-kill saat alert ditahan untuk digest (throttle)
  crash_midjob    aplikasi di-kill saat video sedang diproses
  corrupt_files   state.json dan pending-alerts.json rusak
  webhook_hang    [--slow] webhook menggantung lebih lama dari batas waktu kirim
  retry_expiry    [--slow] webhook mati lebih lama dari jendela retry (alert memang dibuang, sesuai README)

Contoh
    python eval/fault_injection.py --out eval/runs/fault_v1
    python eval/fault_injection.py --only baseline,crash_retrying --out eval/runs/fault_quick
    python eval/fault_injection.py --slow --out eval/runs/fault_full

Prasyarat: .NET 10 SDK, ffmpeg, Python 3.9+. Hanya pustaka standar Python. Waktu: sekitar 10 menit
tanpa --slow, karena aplikasi mencoba ulang alert dengan jeda 30 detik, 1 menit, dan seterusnya.
"""
import argparse
import glob
import json
import os
import shutil
import signal
import socket
import subprocess
import sys
import threading
import time
import traceback
import urllib.error
import urllib.parse
import urllib.request
from collections import Counter
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

for _s in (sys.stdout, sys.stderr):
    if hasattr(_s, "reconfigure"):
        _s.reconfigure(encoding="utf-8", errors="replace")

KILL = getattr(signal, "SIGKILL", signal.SIGTERM)      # Windows tidak punya SIGKILL; SIGTERM di sana = TerminateProcess
TERMINAL = {"completed", "failed", "cancelled"}
HERE = Path(__file__).resolve().parent
REPO = HERE.parent


def free_port():
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def lower_keys(x):
    if isinstance(x, dict):
        return {k[:1].lower() + k[1:]: lower_keys(v) for k, v in x.items()}
    if isinstance(x, list):
        return [lower_keys(v) for v in x]
    return x


def read_body(h):
    """Baca body permintaan. Klien .NET (PostAsJsonAsync) memakai Transfer-Encoding: chunked."""
    if "chunked" in (h.headers.get("Transfer-Encoding") or "").lower():
        data = b""
        while True:
            size = int((h.rfile.readline().strip() or b"0").split(b";")[0], 16)
            if size == 0:
                while h.rfile.readline().strip():       # trailer
                    pass
                return data
            data += h.rfile.read(size)
            h.rfile.readline()
    return h.rfile.read(int(h.headers.get("Content-Length", 0)))


# ------------------------------------------------------------------------- model palsu

class MockModel:
    """Server mirip-OpenAI. modes[kind] dengan kind person|description|agent:
    ok | http500 | garbage | truncated | drop. delays[kind] = detik tunda sebelum menjawab."""

    def __init__(self, port):
        self.port = port
        self.modes = {"person": "ok", "description": "ok", "agent": "ok"}
        self.delays = {"person": 0.0, "description": 0.0, "agent": 0.0}
        self.weapon = "yes"        # yes = alert critical, tidak kena throttle (satu video = satu alert)
        self.counts = Counter()
        self.httpd = None

    def start(self):
        if self.httpd:
            return
        owner = self

        class H(BaseHTTPRequestHandler):
            protocol_version = "HTTP/1.1"

            def log_message(self, *a):
                pass

            def _send(self, code, obj):
                data = json.dumps(obj).encode()
                self.send_response(code)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)

            def do_GET(self):
                self._send(200, {"data": [{"id": "mock-vlm", "object": "model"}], "models": [{"name": "mock-vlm"}]})

            def do_POST(self):
                body = json.loads(read_body(self) or b"{}")
                schema_name = (body.get("response_format", {}).get("json_schema", {}) or {}).get("name", "")
                kind = {"person_present": "person", "video_description": "description",
                        "notification_decision": "agent"}.get(schema_name, "other")
                owner.counts[kind] += 1
                if owner.delays.get(kind):
                    time.sleep(owner.delays[kind])
                mode = owner.modes.get(kind, "ok")
                try:
                    if mode == "drop":
                        self.close_connection = True
                        self.request.shutdown(socket.SHUT_RDWR)
                        return
                    if mode == "http500":
                        return self._send(500, {"error": {"message": "mock: kegagalan yang disengaja"}})
                    if mode == "garbage":
                        return self._send(200, {"choices": [{"message": {"content": "ini bukan json"}, "finish_reason": "stop"}]})
                    if mode == "truncated":
                        return self._send(200, {"choices": [{"message": {"content": "{\"activity_description\":\"seor"},
                                                             "finish_reason": "length"}]})
                    self._send(200, {"choices": [{"message": {"content": json.dumps(owner.answer(kind, body))},
                                                  "finish_reason": "stop"}]})
                except (BrokenPipeError, ConnectionResetError, OSError):
                    pass

        class Server(ThreadingHTTPServer):
            daemon_threads = True
            allow_reuse_address = True
            conns = None

            def get_request(self):
                sock, addr = super().get_request()
                if self.conns is None:
                    self.conns = []
                self.conns.append(sock)
                return sock, addr

        self.httpd = Server(("127.0.0.1", self.port), H)
        threading.Thread(target=self.httpd.serve_forever, daemon=True).start()

    def stop(self):
        """Mati sungguhan: tutup port DAN putus semua koneksi keep-alive yang sedang terbuka."""
        if self.httpd:
            self.httpd.shutdown()
            self.httpd.server_close()
            for sock in list(self.httpd.conns or []):
                try:
                    sock.shutdown(socket.SHUT_RDWR)
                except OSError:
                    pass
                try:
                    sock.close()
                except OSError:
                    pass
            self.httpd = None

    def answer(self, kind, body):
        if kind == "person":
            return {"person": "yes"}
        if kind == "description":
            return {"activity_description": "Seorang orang berjalan melintasi area dan berhenti di dekat pintu (data uji).",
                    "suspicious": "yes", "weapon": self.weapon}
        if kind == "agent":
            allowed = body["response_format"]["json_schema"]["schema"]["properties"]["action"]["enum"]
            action = ("ignore" if self.modes["agent"] == "ignore" and "ignore" in allowed else
                      "send_email" if "send_email" in allowed else "send_whatsapp" if "send_whatsapp" in allowed else "ignore")
            return {"reason": "uji", "action": action, "urgency": "critical" if self.weapon == "yes" else "high",
                    "summary": "Kamera uji: aktivitas tertandai (data uji)."}
        return {}


# ------------------------------------------------------------------------- webhook palsu

class MockWebhook:
    """mode: ok | http500 | hang. Mati total = stop() (koneksi ditolak)."""

    def __init__(self, port):
        self.port = port
        self.mode = "ok"
        self.hang_seconds = 40.0
        self.attempts = []      # semua percobaan yang masuk
        self.delivered = []     # yang dijawab 2xx
        self.lock = threading.Lock()
        self.httpd = None

    def start(self):
        if self.httpd:
            return
        owner = self

        class H(BaseHTTPRequestHandler):
            protocol_version = "HTTP/1.1"

            def log_message(self, *a):
                pass

            def do_POST(self):
                raw = read_body(self)
                try:
                    payload = json.loads(raw)
                except ValueError:
                    payload = {"_raw": raw.decode("utf-8", "replace")}
                entry = {"t": time.time(), "payload": payload}
                with owner.lock:
                    owner.attempts.append(entry)
                try:
                    if owner.mode == "hang":
                        time.sleep(owner.hang_seconds)
                    code = 500 if owner.mode == "http500" else 200
                    data = b"{}"
                    self.send_response(code)
                    self.send_header("Content-Length", str(len(data)))
                    self.end_headers()
                    self.wfile.write(data)
                    if code == 200:
                        with owner.lock:
                            owner.delivered.append(entry)
                except (BrokenPipeError, ConnectionResetError, OSError):
                    pass

        class Server(ThreadingHTTPServer):
            daemon_threads = True
            allow_reuse_address = True
            conns = None

            def get_request(self):
                sock, addr = super().get_request()
                if self.conns is None:
                    self.conns = []
                self.conns.append(sock)
                return sock, addr

        self.httpd = Server(("127.0.0.1", self.port), H)
        threading.Thread(target=self.httpd.serve_forever, daemon=True).start()

    def stop(self):
        """Mati sungguhan: tutup port DAN putus semua koneksi keep-alive yang sedang terbuka."""
        if self.httpd:
            self.httpd.shutdown()
            self.httpd.server_close()
            for sock in list(self.httpd.conns or []):
                try:
                    sock.shutdown(socket.SHUT_RDWR)
                except OSError:
                    pass
                try:
                    sock.close()
                except OSError:
                    pass
            self.httpd = None

    def of_kind(self, kind):
        with self.lock:
            return [e["payload"] for e in self.delivered if e["payload"].get("kind") == kind]


# ------------------------------------------------------------------------- proses aplikasi

class App:
    def __init__(self, app_dir, data_dir, port, env, log_path):
        self.app_dir, self.data_dir, self.port, self.env, self.log_path = app_dir, data_dir, port, env, log_path
        self.proc = None
        self.base = f"http://127.0.0.1:{port}"

    def start(self, timeout=60):
        env = os.environ.copy()
        env.update(self.env)
        env.update({"CCTV_URL": self.base, "CCTV_DATA_DIR": str(self.data_dir),
                    "DOTNET_CLI_TELEMETRY_OPTOUT": "1", "DOTNET_NOLOGO": "1"})
        log = open(self.log_path, "ab")
        log.write(f"\n===== START {datetime.now().isoformat(timespec='seconds')} =====\n".encode())
        self.proc = subprocess.Popen(["dotnet", str(Path(self.app_dir) / "CctvDemo.dll")], cwd=self.app_dir,
                                     env=env, stdout=log, stderr=subprocess.STDOUT)
        end = time.time() + timeout
        while time.time() < end:
            if self.proc.poll() is not None:
                raise RuntimeError(f"aplikasi berhenti saat start (kode {self.proc.returncode}); lihat {self.log_path}")
            try:
                if self.call("GET", "/api/health", timeout=2)[0] == 200:
                    return
            except OSError:
                pass
            time.sleep(0.5)
        raise RuntimeError("aplikasi tidak siap tepat waktu")

    def alive(self):
        return self.proc is not None and self.proc.poll() is None

    def stop(self, sig=signal.SIGTERM, wait=25):
        if not self.proc:
            return
        if self.proc.poll() is None:
            self.proc.send_signal(sig)
            try:
                self.proc.wait(wait)
            except subprocess.TimeoutExpired:
                self.proc.kill()
                self.proc.wait()
        self.proc = None

    def kill(self):
        self.stop(KILL, 10)

    def call(self, method, path, body=None, timeout=15, raw=None, headers=None):
        data = raw if raw is not None else (json.dumps(body).encode() if body is not None else (b"" if method == "POST" else None))
        h = dict(headers or {})
        if raw is None and data:
            h["Content-Type"] = "application/json"
        req = urllib.request.Request(self.base + path, data=data, method=method, headers=h)
        try:
            with urllib.request.urlopen(req, timeout=timeout) as r:
                txt = r.read()
                return r.status, (json.loads(txt) if txt and txt[:1] in b"{[" else txt)
        except urllib.error.HTTPError as e:
            txt = e.read()
            try:
                return e.code, json.loads(txt)
            except ValueError:
                return e.code, txt


# ------------------------------------------------------------------------- konteks skenario

class Ctx:
    def __init__(self, name, args, root, video):
        self.name, self.args, self.video = name, args, video
        self.dir = root / name
        if self.dir.exists():
            shutil.rmtree(self.dir, ignore_errors=True)
        self.data = self.dir / "data"
        self.data.mkdir(parents=True)
        self.model = MockModel(free_port())
        self.hook = MockWebhook(free_port())
        self.app = None
        self.checks = []
        self.notes = []
        self.t0 = time.time()
        self.smtp = False
        self.extra_env = {}
        self.agent_enabled = False
        self.person_port = free_port()

    # --- pencatatan
    def check(self, name, ok, detail=""):
        self.checks.append({"name": name, "ok": bool(ok), "detail": str(detail)})
        print(f"    [{'OK ' if ok else 'GAGAL'}] {name}" + (f"  ({detail})" if detail and not ok else ""))
        return bool(ok)

    def note(self, text):
        self.notes.append(text)
        print(f"    catatan: {text}")

    def log(self, text):
        print(f"  - {text}")

    # --- siklus aplikasi
    def boot(self, smtp=False, agent=False, extra_env=None):
        self.smtp, self.agent_enabled = smtp, agent
        self.extra_env = dict(extra_env or {})
        self.model.start()
        self.hook.start()
        env = {"CCTV_FFMPEG": self.args.ffmpeg, "CCTV_WEBHOOK_URL": f"http://127.0.0.1:{self.hook.port}/hook",
               "CCTV_YOLO_PYTHON": "/nonexistent/python"}
        if smtp:
            env.update({"CCTV_SMTP_HOST": "127.0.0.1", "CCTV_SMTP_PORT": str(self.person_port),
                        "CCTV_MAIL_TO": "ops@example.test", "CCTV_MAIL_FROM": "cctv@example.test"})
        env.update(self.extra_env)
        self.app = App(self.args.app_dir, self.data, free_port(), env, self.dir / "app.log")
        self.app.start()
        self.apply_settings()

    def apply_settings(self):
        code, body = self.app.call("POST", "/api/settings", {
            "mode": "local", "endpoint": f"http://127.0.0.1:{self.model.port}/v1", "model": "mock-vlm",
            "personLabels": False, "personDetector": "llm", "personThreshold": 0.25, "suspiciousThreshold": 0.5,
            "agentEnabled": self.agent_enabled, "weaponEnabled": True, "llmConcurrency": 2})
        assert code == 200, f"pengaturan ditolak: {code} {body}"

    def restart(self, how="kill"):
        if how == "kill":
            self.app.kill()
        else:
            self.app.stop(signal.SIGTERM)
        self.app.start()

    def shutdown(self):
        try:
            if self.app:
                self.app.stop(KILL, 10)
        finally:
            self.model.stop()
            self.hook.stop()

    # --- operasi aplikasi
    def upload(self, name="klip.mp4"):
        raw = Path(self.video).read_bytes()
        code, body = self.app.call("POST", "/api/videos", raw=raw, timeout=60,
                                   headers={"X-Filename": urllib.parse.quote(name), "Content-Type": "application/octet-stream"})
        assert code == 200, f"upload gagal: {code} {body}"
        return body["id"]

    def link_video(self, name):
        """Video lewat folder terhubung (ingest otomatis). Hanya jalur ini yang memicu alert 'analisis gagal'."""
        watch = self.dir / "watch"
        watch.mkdir(exist_ok=True)
        shutil.copy(self.video, watch / name)
        if self.state().get("linkedFolder"):
            code, body = self.app.call("POST", "/api/folder/refresh")
        else:
            code, body = self.app.call("POST", "/api/folder", {"path": str(watch)})
        assert code == 200, f"folder tidak bisa ditautkan: {code} {body}"
        found = []
        assert self.wait(lambda: found.append(next((j["id"] for j in self.state()["jobs"] if j["name"] == name), None)) or found[-1],
                         60), f"video {name} tidak muncul di antrean"
        return found[-1]

    def start_queue(self):
        self.app.call("POST", "/api/queue/start")

    def state(self):
        return self.app.call("GET", "/api/state")[1]

    def job(self, jid):
        for j in self.state().get("jobs", []):
            if j["id"] == jid:
                return j
        return None

    def wait(self, cond, timeout, every=1.0):
        end = time.time() + timeout
        while time.time() < end:
            try:
                if cond():
                    return True
            except OSError:
                pass
            time.sleep(every)
        try:
            return bool(cond())
        except OSError:
            return False

    def wait_job(self, jid, timeout=120, statuses=TERMINAL):
        def done():
            j = self.job(jid)
            if j and j["status"] == "queued" and not self.state().get("running"):
                self.start_queue()
            return j is not None and j["status"] in statuses
        return self.wait(done, timeout, 1.0)

    def wait_completed(self, jid, timeout=120):
        return self.wait_job(jid, timeout) and (self.job(jid) or {}).get("status") == "completed"

    def flag_count(self, jid):
        return sum(1 for p in self.hook.of_kind("flag") if p.get("jobId") == jid)

    def failure_alerts(self):
        return len(self.hook.of_kind("job_failed"))

    def journal(self):
        p = self.data / "pending-alerts.json"
        if not p.exists():
            return []
        try:
            return lower_keys(json.loads(p.read_text(encoding="utf-8")))
        except ValueError:
            return None

    def healthy(self):
        try:
            return self.app.alive() and self.app.call("GET", "/api/health", timeout=5)[0] == 200
        except OSError:
            return False

    def stuck_processing(self):
        return [j["id"] for j in self.state().get("jobs", []) if j["status"] == "processing"]


# ------------------------------------------------------------------------- skenario

def sc_baseline(c):
    c.boot()
    jid = c.upload()
    c.start_queue()
    c.check("video selesai diproses (status completed)", c.wait_completed(jid, 90))
    j = c.job(jid)
    c.check("video ditandai suspicious", j and j.get("suspicious") == "yes", j and j.get("suspicious"))
    c.check("alert terkirim ke webhook tepat sekali", c.wait(lambda: c.flag_count(jid) >= 1, 30) and c.flag_count(jid) == 1,
            f"jumlah={c.flag_count(jid)}")
    c.check("jurnal alert kosong setelah terkirim", c.wait(lambda: c.journal() == [], 20), c.journal())
    j = c.job(jid)
    c.check("job mencatat notifiedAt dan tanpa notifyError", j and j.get("notifiedAt") and not j.get("notifyError"),
            j and (j.get("notifiedAt"), j.get("notifyError")))


def sc_model_down(c):
    c.boot()
    c.model.delays["person"] = 1.5
    ids = [c.link_video(f"klip{i}.mp4") for i in range(3)]
    c.start_queue()
    c.check("ada video yang mulai diproses", c.wait(lambda: any(j["status"] == "processing" for j in c.state()["jobs"]), 30))
    c.log("mematikan model di tengah antrean")
    c.model.stop()
    settled = c.wait(lambda: all(j["status"] in TERMINAL for j in c.state()["jobs"]), 120, 2)
    states = {j["id"]: j["status"] for j in c.state()["jobs"]}
    c.check("tidak ada video yang macet di 'processing' / antrean berhenti", settled and not c.stuck_processing(), states)
    failed = [i for i in ids if states.get(i) == "failed"]
    c.check("semua video yang terdampak berstatus failed dengan pesan error",
            len(failed) == len(ids) and all(c.job(i).get("error") for i in failed),
            [(c.job(i) or {}).get("error") for i in ids])
    c.check("aplikasi tetap hidup", c.healthy())
    c.check("minimal satu alert 'analisis gagal' terkirim (video dari folder terhubung)",
            c.wait(lambda: c.failure_alerts() >= 1, 40), c.failure_alerts())
    c.note("alert 'analisis gagal' hanya dibuat untuk video dari ingest otomatis (folder terhubung/RTSP); upload manual tidak "
           "memicunya (sengaja, lihat DescriptionWorker.cs)")
    c.check("tidak ada alert 'suspicious' palsu saat model mati", all(c.flag_count(i) == 0 for i in ids))
    c.log("menghidupkan model lagi dan mengulang video yang gagal")
    c.model.delays["person"] = 0
    c.model.start()
    for i in failed:
        c.app.call("POST", f"/api/videos/{i}/retry")
    c.start_queue()
    ok = c.wait(lambda: all((c.job(i) or {}).get("status") == "completed" for i in ids), 150, 2)
    c.check("semua video selesai setelah model pulih", ok, {i[:6]: (c.job(i) or {}).get("status") for i in ids})
    c.check("setiap video bertanda menghasilkan tepat satu alert",
            c.wait(lambda: all(c.flag_count(i) >= 1 for i in ids), 40) and all(c.flag_count(i) == 1 for i in ids),
            {i[:6]: c.flag_count(i) for i in ids})


def sc_model_bad(c):
    c.boot()
    first_failed = None
    for mode in ("garbage", "http500", "truncated", "drop"):
        c.model.modes["description"] = mode
        jid = c.link_video(f"{mode}.mp4")
        c.start_queue()
        done = c.wait_job(jid, 90)
        j = c.job(jid)
        c.check(f"[{mode}] job berakhir failed (tidak menggantung), error terisi",
                done and j["status"] == "failed" and bool(j.get("error")), j and (j["status"], j.get("error")))
        c.check(f"[{mode}] tidak ada alert 'suspicious' palsu", c.flag_count(jid) == 0)
        c.check(f"[{mode}] aplikasi tetap hidup", c.healthy())
        first_failed = first_failed or jid
    c.check("alert 'analisis gagal' terkirim", c.wait(lambda: c.failure_alerts() >= 1, 40), c.failure_alerts())
    c.log("model pulih; mengulang video pertama yang gagal")
    c.model.modes["description"] = "ok"
    c.app.call("POST", f"/api/videos/{first_failed}/retry")
    c.start_queue()
    c.check("video selesai setelah model pulih", c.wait_job(first_failed, 90) and c.job(first_failed)["status"] == "completed")
    c.check("alert terkirim tepat sekali setelah pulih",
            c.wait(lambda: c.flag_count(first_failed) >= 1, 40) and c.flag_count(first_failed) == 1, c.flag_count(first_failed))


def sc_agent_faults(c):
    c.boot(smtp=True, agent=True)
    cases = [
        ("agen normal", "ok", "yes", None),
        ("agen gagal 500 (harus fallback ke semua kanal)", "http500", "yes", "fallback"),
        ("agen memilih ignore padahal weapon=yes (harus dikoreksi guard)", "ignore", "yes", "guard"),
    ]
    jobs = []
    for label, mode, weapon, expect in cases:
        c.model.modes["agent"], c.model.weapon = mode, weapon
        jid = c.upload(f"{mode}.mp4")
        c.start_queue()
        c.check(f"[{label}] video selesai", c.wait_completed(jid, 90))
        c.check(f"[{label}] webhook menerima alert tepat sekali",
                c.wait(lambda: c.flag_count(jid) >= 1, 40) and c.flag_count(jid) == 1, c.flag_count(jid))
        j = c.job(jid) or {}
        agent = ((j.get("screening") or {}).get("agent")) or {}
        if expect == "fallback":
            c.check(f"[{label}] log agen mencatat fallbackUsed", agent.get("fallbackUsed") is True, agent)
        elif expect == "guard":
            c.check(f"[{label}] guard mengoreksi: guardOverride=true", agent.get("guardOverride") is True, agent)
            sent = [p for p in c.hook.of_kind("flag") if p.get("jobId") == jid]
            c.check(f"[{label}] urgensi alert yang terkirim = critical", sent and sent[0].get("urgency") == "critical",
                    sent and sent[0].get("urgency"))
        else:
            c.check(f"[{label}] agen memutuskan send_email", agent.get("action") == "send_email", agent)
        jobs.append(jid)
    c.log("menunggu satu siklus retry untuk kanal email (kanal ini sengaja mati)")
    c.wait(lambda: len(c.journal() or []) >= len(jobs) and all(e.get("nextTry") for e in (c.journal() or [])), 40)
    j_entries = c.journal() or []
    chans = [tuple((e.get("message") or {}).get("channels") or []) for e in j_entries]
    c.check("kanal email yang gagal disimpan di jurnal untuk retry (hanya email)",
            len(chans) >= 1 and all(ch == ("email",) for ch in chans), chans)
    before = {i: c.flag_count(i) for i in jobs}
    time.sleep(55)
    after = {i: c.flag_count(i) for i in jobs}
    c.check("retry email tidak mengirim ulang ke webhook yang sudah menerima (tanpa duplikat)", before == after, (before, after))
    c.check("aplikasi tetap hidup", c.healthy())


def _outage(c, kind):
    c.boot()
    if kind == "refused":
        c.hook.stop()
    else:
        c.hook.mode = "http500"
    jid = c.upload()
    c.start_queue()
    c.check("video selesai diproses (status completed)", c.wait_completed(jid, 90))
    c.check("alert masuk jurnal retry saat webhook tidak bisa dijangkau",
            c.wait(lambda: any(e.get("nextTry") for e in (c.journal() or [])), 60), c.journal())
    c.check("job mencatat notifyError", c.wait(lambda: bool((c.job(jid) or {}).get("notifyError")), 20),
            (c.job(jid) or {}).get("notifyError"))
    c.check("tidak ada pengiriman palsu yang tercatat berhasil", c.flag_count(jid) == 0)
    c.log("webhook dipulihkan")
    t = time.time()
    c.hook.mode = "ok"
    c.hook.start()
    ok = c.wait(lambda: c.flag_count(jid) >= 1, 150, 2)
    c.check("alert akhirnya terkirim setelah webhook pulih", ok, f"{time.time() - t:.0f} detik")
    if ok:
        c.note(f"waktu pulih sampai terkirim: {time.time() - t:.0f} detik")
    time.sleep(12)
    c.check("terkirim tepat sekali (tanpa duplikat)", c.flag_count(jid) == 1, c.flag_count(jid))
    c.check("jurnal kosong setelah terkirim", c.wait(lambda: c.journal() == [], 30), c.journal())
    j = c.job(jid) or {}
    c.check("notifiedAt terisi dan notifyError dibersihkan", j.get("notifiedAt") and not j.get("notifyError"),
            (j.get("notifiedAt"), j.get("notifyError")))


def sc_webhook_outage(c):
    _outage(c, "refused")


def sc_webhook_outage_500(c):
    _outage(c, "http500")


def _crash_retrying(c, how):
    c.boot()
    c.hook.stop()
    jid = c.upload()
    c.start_queue()
    c.check("video selesai diproses (status completed)", c.wait_completed(jid, 90))
    c.check("alert masuk jurnal retry", c.wait(lambda: any(e.get("nextTry") for e in (c.journal() or [])), 60), c.journal())
    before = c.journal()
    c.log("aplikasi dimatikan paksa (SIGKILL)" if how == "kill" else "aplikasi dihentikan dengan SIGTERM")
    c.restart(how)
    c.check("aplikasi hidup lagi setelah restart", c.healthy())
    after = c.journal()
    c.check("alert tetap ada di jurnal setelah restart",
            after and {e["key"] for e in (before or [])} <= {e["key"] for e in after}, after)
    c.log("webhook dipulihkan setelah restart")
    t = time.time()
    c.hook.start()
    ok = c.wait(lambda: c.flag_count(jid) >= 1, 170, 2)
    c.check("alert terkirim setelah restart dan webhook pulih", ok, f"{time.time() - t:.0f} detik")
    time.sleep(12)
    c.check("terkirim tepat sekali", c.flag_count(jid) == 1, c.flag_count(jid))
    c.check("jurnal kosong", c.wait(lambda: c.journal() == [], 30), c.journal())


def sc_crash_retrying(c):
    _crash_retrying(c, "kill")


def sc_crash_retrying_term(c):
    _crash_retrying(c, "term")


def sc_crash_waiting(c):
    c.model.weapon = "no"      # alert biasa -> kena throttle 5 menit per kamera, jadi yang kedua ditahan
    c.boot()
    j1, j2 = c.upload("a.mp4"), c.upload("b.mp4")
    c.start_queue()
    c.check("kedua video selesai", c.wait_completed(j1, 90) and c.wait_completed(j2, 90))
    c.check("alert pertama terkirim langsung", c.wait(lambda: c.flag_count(j1) + c.flag_count(j2) >= 1, 40))
    sent_first = [i for i in (j1, j2) if c.flag_count(i) == 1]
    held = [i for i in (j1, j2) if i not in sent_first]
    c.check("alert kedua ditahan untuk digest dan tercatat di jurnal (status waiting)",
            len(held) == 1 and any(not e.get("nextTry") for e in (c.journal() or [])), (held, c.journal()))
    c.log("aplikasi dimatikan paksa saat alert kedua masih ditahan")
    c.restart("kill")
    c.check("aplikasi hidup lagi setelah restart", c.healthy())
    ok = c.wait(lambda: all(c.flag_count(i) >= 1 for i in (j1, j2)), 60, 2)
    c.check("alert yang ditahan akhirnya terkirim setelah restart", ok, {i[:6]: c.flag_count(i) for i in (j1, j2)})
    time.sleep(5)
    c.check("masing-masing video tepat satu alert (tanpa duplikat, tanpa hilang)",
            all(c.flag_count(i) == 1 for i in (j1, j2)), {i[:6]: c.flag_count(i) for i in (j1, j2)})


def sc_crash_midjob(c):
    c.boot()
    c.model.delays["person"] = 6.0
    jid = c.upload()
    c.start_queue()
    c.check("video mulai diproses", c.wait(lambda: (c.job(jid) or {}).get("status") == "processing", 30))
    c.log("aplikasi dimatikan paksa di tengah pemrosesan")
    c.restart("kill")
    c.model.delays["person"] = 0
    j = c.job(jid)
    c.check("video kembali ke 'queued' (tidak macet di processing)", j and j["status"] == "queued", j and (j["status"], j.get("stage")))
    c.check("antrean dalam keadaan berhenti setelah restart (sesuai README)", not c.state().get("running"))
    c.check("tidak ada alert terkirim sebelum video selesai", c.flag_count(jid) == 0)
    c.start_queue()
    c.check("video selesai setelah antrean dijalankan lagi", c.wait_job(jid, 120) and c.job(jid)["status"] == "completed")
    c.check("alert terkirim tepat sekali",
            c.wait(lambda: c.flag_count(jid) >= 1, 40) and c.flag_count(jid) == 1, c.flag_count(jid))


def sc_corrupt_files(c):
    c.boot()
    c.app.stop(signal.SIGTERM)
    (c.data / "pending-alerts.json").write_text("{ bukan json [[[", encoding="utf-8")
    (c.data / "state.json").write_text("\x00\x00 rusak", encoding="utf-8")
    try:
        c.app.start()
        started = True
    except RuntimeError as e:
        started = False
        c.note(str(e))
    c.check("aplikasi tetap bisa start dengan state.json dan jurnal rusak", started)
    if not started:
        return
    c.check("salinan state rusak disimpan untuk diperiksa", bool(glob.glob(str(c.data / "state.corrupt-*.json"))))
    c.check("salinan jurnal rusak disimpan untuk diperiksa", bool(glob.glob(str(c.data / "pending-alerts.json.corrupt-*"))))
    c.check("antrean dimulai kosong", c.state().get("jobs") == [], c.state().get("jobs"))
    c.apply_settings()
    jid = c.upload()
    c.start_queue()
    c.check("pipeline berfungsi normal setelah pemulihan", c.wait_job(jid, 90) and c.job(jid)["status"] == "completed")
    c.check("alert terkirim tepat sekali",
            c.wait(lambda: c.flag_count(jid) >= 1, 40) and c.flag_count(jid) == 1, c.flag_count(jid))


def sc_webhook_hang(c):
    c.boot()
    c.hook.mode = "hang"
    c.hook.hang_seconds = 45
    j1, j2 = c.upload("a.mp4"), c.upload("b.mp4")
    c.start_queue()
    c.check("video pertama selesai", c.wait_completed(j1, 90))
    c.check("video kedua tetap selesai diproses saat webhook menggantung", c.wait_completed(j2, 90))
    c.check("API tetap responsif saat webhook menggantung", c.healthy())
    time.sleep(20)
    with c.hook.lock:
        seen = {e["payload"].get("jobId") for e in c.hook.attempts}
    c.note(f"20 dtk setelah kedua video selesai, webhook yang menggantung baru menerima percobaan untuk {len(seen)} dari 2 alert"
           + (" (alert kedua belum dicoba: terblokir di belakang alert pertama, karena pengiriman berjalan satu per satu)"
              if len(seen) < 2 else " (kedua alert dicoba paralel)"))
    c.log("menunggu batas waktu kirim (30 dtk x 3 percobaan) habis")
    c.hook.mode = "ok"
    c.hook.hang_seconds = 0
    ok = c.wait(lambda: c.flag_count(j1) >= 1 and c.flag_count(j2) >= 1, 240, 3)
    c.check("kedua alert akhirnya terkirim setelah webhook normal", ok, {i[:6]: c.flag_count(i) for i in (j1, j2)})
    time.sleep(10)
    c.check("masing-masing tepat sekali", c.flag_count(j1) == 1 and c.flag_count(j2) == 1,
            {i[:6]: c.flag_count(i) for i in (j1, j2)})


def sc_retry_expiry(c):
    c.boot(extra_env={"CCTV_ALERT_RETRY_HOURS": "0.01"})      # jendela retry 36 detik
    c.hook.stop()
    jid = c.upload()
    c.start_queue()
    c.check("video selesai diproses (status completed)", c.wait_completed(jid, 90))
    c.check("alert sempat masuk jurnal retry", c.wait(lambda: any(e.get("nextTry") for e in (c.journal() or [])), 60))
    c.check("alert dibuang setelah jendela retry habis (perilaku yang tercatat di README)",
            c.wait(lambda: c.journal() == [], 150, 3), c.journal())
    c.check("job tetap mencatat notifyError (kegagalan terlihat di UI)", bool((c.job(jid) or {}).get("notifyError")),
            (c.job(jid) or {}).get("notifyError"))
    c.hook.start()
    time.sleep(40)
    c.check("tidak ada pengiriman 'hantu' setelah alert dibuang", c.flag_count(jid) == 0, c.flag_count(jid))
    c.check("aplikasi tetap hidup", c.healthy())
    c.note("alert yang melewati jendela retry hilang dari jalur notifikasi; hanya tersisa di UI. Ini perilaku terdokumentasi, "
           "tetapi layak disebut di laporan sebagai batas keandalan")


SCENARIOS = [
    ("baseline", sc_baseline, False),
    ("model_down", sc_model_down, False),
    ("model_bad", sc_model_bad, False),
    ("agent_faults", sc_agent_faults, False),
    ("webhook_outage", sc_webhook_outage, False),
    ("webhook_outage_500", sc_webhook_outage_500, False),
    ("crash_retrying", sc_crash_retrying, False),
    ("crash_retrying_term", sc_crash_retrying_term, False),
    ("crash_waiting", sc_crash_waiting, False),
    ("crash_midjob", sc_crash_midjob, False),
    ("corrupt_files", sc_corrupt_files, False),
    ("webhook_hang", sc_webhook_hang, True),
    ("retry_expiry", sc_retry_expiry, True),
]


# ------------------------------------------------------------------------- laporan

def write_report(out, results, args):
    total = sum(len(r["checks"]) for r in results)
    failed = sum(1 for r in results for c in r["checks"] if not c["ok"])
    L = ["# Uji kegagalan (fault injection) aplikasi CCTV", "",
         f"- Dibuat: {datetime.now(timezone.utc).isoformat(timespec='seconds')}",
         f"- Aplikasi: `{args.app_dir}`", f"- Skenario dijalankan: {len(results)}",
         f"- Pemeriksaan: {total - failed}/{total} lolos", "",
         "Aplikasi asli dijalankan; hanya model, webhook, dan SMTP yang dipalsukan. Satu video bertanda = satu alert yang "
         "diharapkan (alert dibuat critical agar tidak kena throttle), kecuali di skenario `crash_waiting`.", "",
         "## Ringkasan", "", "| Skenario | Lolos | Gagal | Durasi |", "|---|---|---|---|"]
    for r in results:
        bad = sum(1 for c in r["checks"] if not c["ok"])
        L.append(f"| {r['name']} | {len(r['checks']) - bad} | {bad} | {r['seconds']:.0f} dtk |")
    L += ["", "## Rincian", ""]
    for r in results:
        L += [f"### {r['name']}", ""]
        for c in r["checks"]:
            mark = "✅" if c["ok"] else "❌"
            L.append(f"- {mark} {c['name']}" + ("" if c["ok"] or not c["detail"] else f" — `{c['detail'][:300]}`"))
        for n in r["notes"]:
            L.append(f"- ℹ️ {n}")
        if r.get("error"):
            L.append(f"- ⚠️ skenario berhenti karena error: `{r['error'][:300]}`")
        L.append("")
    L += ["## Cara membaca", "",
          "- ❌ berarti perilaku aplikasi berbeda dari yang diharapkan. Cek `app.log` di folder skenario sebelum menyimpulkan.",
          "- Waktu pulih di skenario webhook dipengaruhi jadwal retry aplikasi (30 dtk, 1 mnt, 2 mnt, ...) dan siklus 10 dtk.",
          "- Yang diuji hanya jalur notifikasi dan pemulihan. Ketepatan deteksi tidak diukur di sini (lihat `agent_eval.py` dan "
          "`run_app_eval.py`).",
          "- Pengiriman WhatsApp dan SMTP sungguhan tidak diuji; kanal email hanya dimatikan untuk menguji retry per kanal.", ""]
    (out / "fault_report.md").write_text("\n".join(L), encoding="utf-8")
    (out / "fault_results.json").write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8")
    return failed


def make_video(ffmpeg, path):
    if path.exists():
        return
    subprocess.run([ffmpeg, "-y", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc=duration=6:size=320x240:rate=10",
                    "-pix_fmt", "yuv420p", str(path)], check=True)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", default=str(HERE / "runs" / "fault_injection"))
    ap.add_argument("--app-dir", help="folder berisi CctvDemo.dll hasil build; jika kosong dibuild otomatis ke <out>/app")
    ap.add_argument("--ffmpeg", default=shutil.which("ffmpeg") or "ffmpeg")
    ap.add_argument("--only", help="daftar skenario dipisah koma")
    ap.add_argument("--slow", action="store_true", help="sertakan skenario yang memakan waktu lama")
    ap.add_argument("--list", action="store_true")
    args = ap.parse_args()
    if os.name == "nt":
        print("Catatan Windows: SIGTERM di Windows langsung mematikan proses (bukan shutdown halus), jadi "
              "crash_retrying_term sama kerasnya dengan crash_retrying.")

    if args.list:
        for name, _, slow in SCENARIOS:
            print(f"{name}{'  [--slow]' if slow else ''}")
        return
    out = Path(args.out).resolve()
    out.mkdir(parents=True, exist_ok=True)
    if not args.app_dir:
        args.app_dir = str(out / "app")
        if not (Path(args.app_dir) / "CctvDemo.dll").exists():
            print("Membangun aplikasi (dotnet build) ...")
            subprocess.run(["dotnet", "build", str(REPO / "CctvDemo.csproj"), "-c", "Release", "-o", args.app_dir,
                            "--nologo", "-v", "q"], check=True)
    args.app_dir = str(Path(args.app_dir).resolve())
    video = out / "klip-uji.mp4"
    make_video(args.ffmpeg, video)

    wanted = set(args.only.split(",")) if args.only else None
    todo = [(n, f) for n, f, slow in SCENARIOS if (wanted and n in wanted) or (not wanted and (args.slow or not slow))]
    if wanted and (missing := wanted - {n for n, _, _ in SCENARIOS}):
        raise SystemExit(f"skenario tidak dikenal: {sorted(missing)}")

    results = []
    for name, fn in todo:
        print(f"\n== {name}")
        ctx = Ctx(name, args, out / "scenarios", video)
        error = None
        try:
            fn(ctx)
        except Exception as e:           # skenario rusak tidak boleh menghentikan yang lain
            error = f"{type(e).__name__}: {e}"
            ctx.check("skenario berjalan sampai selesai", False, error)
            traceback.print_exc()
        finally:
            ctx.shutdown()
        results.append({"name": name, "checks": ctx.checks, "notes": ctx.notes, "error": error,
                        "seconds": time.time() - ctx.t0})
    failed = write_report(out, results, args)
    print(f"\nLaporan: {out / 'fault_report.md'}  ({'SEMUA LOLOS' if not failed else f'{failed} pemeriksaan gagal'})")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
