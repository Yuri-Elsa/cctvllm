#!/usr/bin/env python3
"""Person-detection evaluation: YOLO vs the app's LLM detector.

Question answered: can the LLM person detector replace YOLO as the gate in front of the
description model, and at which threshold? The comparison is made on frames with human labels
("is at least one person visible?"), independent of the suspicious-activity labels.

Three steps (see README, section "Person-detection evaluation"):

  1. sample   cut frames from videos -> labels.csv to annotate by hand (has_person = 1 / 0)
     label    browser tool for that annotation: one frame per screen, keys 1 / 0, autosaves
  2. score    run YOLO and/or the LLM on every labeled frame -> scores.jsonl (resumable)
  3. analyze  threshold per detector chosen on the calibration split, frozen, then evaluated
              on the test split with video-level (cluster) bootstrap intervals, paired
              YOLO-vs-LLM deltas, video-level gate pass rate, latency, curves and a report

Design notes
  * Frames are saved at most 640 px on the long side with area interpolation, the same input
    the app gives both detectors (ffmpeg scale in VideoTools.SampleFrames, imgsz=640 in YOLO).
  * The LLM request, prompt and the p(yes) computation mirror LlmPersonDetector.cs and
    Scoring.cs. tests/test_person_detection_eval.py checks the prompts against the C# source.
  * YOLO scores are the maximum person confidence per frame, scored down to 0.05 like
    tracking/track_people.py, so any threshold >= 0.05 can be swept offline.
  * A threshold is a property of one detector. The app uses one PersonThreshold setting for
    both detectors, so set it to the value reported for the detector you select.
  * Do not pre-fill labels with either detector: it would bias the comparison.
"""
import argparse
import base64
import csv
import hashlib
import json
import math
import os
import random
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

import numpy as np

YOLO_FLOOR = 0.05          # same floor as tracking/track_people.py (LOG_FLOOR)
MAX_SIDE = 640             # same long side the app uses for both detectors
SYSTEM_PROMPT = ('Anda memeriksa apakah ada manusia pada sebuah gambar kamera pengawas. '
                 'Jawab hanya dengan JSON {"person": "yes" atau "no"}.')
QUESTION = ('Apakah ada setidaknya satu orang yang terlihat pada gambar ini, termasuk yang jauh, '
            'kecil, atau terlihat sebagian? Jawab yes atau no.')
TOP_LOGPROBS = 5           # Scoring.TopLogprobs
LABEL_FIELDS = ["frame_id", "path", "video_id", "class_name", "timestamp", "split",
                "has_person", "difficulty", "notes"]


# --------------------------------------------------------------------------- p(yes), ported from Scoring.cs

def _classify(token):
    t = token.strip().strip('"').strip().lower()
    return True if t == "yes" else False if t == "no" else None


def read_tokens(choice):
    """OpenAI-style choice -> [(token, logprob, [(alt_token, alt_logprob), ...]), ...] or None."""
    lp = choice.get("logprobs") if isinstance(choice, dict) else None
    if not isinstance(lp, dict) or not isinstance(lp.get("content"), list):
        return None

    def num(item):
        v = item.get("logprob")
        return float(v) if isinstance(v, (int, float)) and not isinstance(v, bool) else float("-inf")

    tokens = []
    for item in lp["content"]:
        if not isinstance(item, dict) or not isinstance(item.get("token"), str):
            return None
        top = [(a["token"], num(a)) for a in (item.get("top_logprobs") or [])
               if isinstance(a, dict) and isinstance(a.get("token"), str)]
        tokens.append((item["token"], num(item), top))
    return tokens


def p_yes(tokens, key):
    """Probability of "yes" for `key`, renormalised over the yes/no alternatives (None if unknown)."""
    if not tokens:
        return None
    starts, pos = [], 0
    for tok, _, _ in tokens:
        starts.append(pos)
        pos += len(tok)
    text = "".join(t for t, _, _ in tokens)
    at = text.rfind('"' + key + '"')
    if at < 0:
        return None
    colon = text.find(":", at + len(key) + 2)
    if colon < 0:
        return None
    quote = text.find('"', colon + 1)
    if quote < 0:
        return None
    value_start = quote + 1
    index = next((i for i, (tok, _, _) in enumerate(tokens)
                  if starts[i] <= value_start < starts[i] + len(tok)), -1)
    if index < 0:
        return None
    tok, lp, top = tokens[index]
    yes = no = 0.0
    for t, l in (top or [(tok, lp)]):
        if math.isnan(l):
            continue
        mass = math.exp(l)
        c = _classify(t)
        if c is True:
            yes += mass
        elif c is False:
            no += mass
    total = yes + no
    return yes / total if total > 0 else None


# --------------------------------------------------------------------------- detectors

def _image_b64(path):
    mime = "image/jpeg" if Path(path).suffix.lower() in (".jpg", ".jpeg") else "image/png"
    return f"data:{mime};base64," + base64.b64encode(Path(path).read_bytes()).decode()


def llm_request(model, image_url, scores):
    body = {
        "model": model,
        "messages": [
            {"role": "system", "content": SYSTEM_PROMPT},
            {"role": "user", "content": [{"type": "image_url", "image_url": {"url": image_url}},
                                         {"type": "text", "text": QUESTION}]},
        ],
        "response_format": {"type": "json_schema", "json_schema": {
            "name": "person_present", "strict": True,
            "schema": {"type": "object", "additionalProperties": False,
                       "properties": {"person": {"type": "string", "enum": ["yes", "no"]}},
                       "required": ["person"]}}},
        "max_tokens": 30, "temperature": 0, "seed": 7, "reasoning_effort": "none",
        "logprobs": scores,
    }
    if scores:
        body["top_logprobs"] = TOP_LOGPROBS
    return body


def _post(endpoint, api_key, body, timeout):
    req = urllib.request.Request(endpoint.rstrip("/") + "/chat/completions",
                                 data=json.dumps(body).encode(), method="POST",
                                 headers={"Content-Type": "application/json",
                                          **({"Authorization": f"Bearer {api_key}"} if api_key else {})})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return json.loads(r.read())
    except urllib.error.HTTPError as e:
        raise RuntimeError(f"HTTP {e.code}: {e.read().decode('utf-8', 'replace')[:300]}") from None
    except urllib.error.URLError as e:
        raise RuntimeError(f"cannot reach model server: {e.reason}") from None


def llm_score(endpoint, model, path, api_key=None, timeout=300):
    """Returns (score, answer, has_logprobs). Mirrors LlmPersonDetector.Score."""
    image = _image_b64(path)
    try:
        doc = _post(endpoint, api_key, llm_request(model, image, True), timeout)
    except RuntimeError as e:                      # ModelCalls.Ask: retry without logprobs
        if "logprob" not in str(e).lower():
            raise
        doc = _post(endpoint, api_key, llm_request(model, image, False), timeout)
    choice = doc["choices"][0]
    message = choice.get("message") or {}
    raw = message.get("content") if isinstance(message.get("content"), str) else ""
    if not raw.strip():
        for k in ("reasoning_content", "reasoning", "thinking"):
            if isinstance(message.get(k), str) and message[k].strip():
                raw = message[k]
                break
    raw = raw.strip()
    if raw.startswith("```"):
        raw = raw.strip("`").removeprefix("json").strip()
    try:
        answer = str(json.loads(raw).get("person", "")).strip().lower()
    except (ValueError, AttributeError):
        answer = ""
    if answer not in ("yes", "no"):
        raise RuntimeError(f"invalid answer: {raw[:80]!r}")
    p = p_yes(read_tokens(choice), "person")
    return (p if p is not None else (1.0 if answer == "yes" else 0.0)), answer, p is not None


class YoloScorer:
    def __init__(self, weights, device):
        import cv2
        try:
            from ultralytics import YOLO        # imported late: only needed for --detectors yolo
        except ImportError as e:
            sys.exit(f"Cannot import ultralytics ({e}).\nRun this command with the Python of your YOLO environment "
                     "(the one in CCTV_YOLO_PYTHON), or score the LLM here with --detectors llm and YOLO there "
                     "with --detectors yolo: the results are appended to the same scores.jsonl.")
        self.cv2, self.device, self.model = cv2, device, YOLO(weights)

    def __call__(self, path):
        frame = self.cv2.imread(str(path))
        if frame is None:
            raise RuntimeError("cannot read image")
        h, w = frame.shape[:2]
        scale = min(1, MAX_SIDE / max(h, w))
        frame = self.cv2.resize(frame, (max(1, int(w * scale)), max(1, int(h * scale))))
        t0 = time.perf_counter()
        result = self.model.predict(frame, classes=[0], conf=YOLO_FLOOR, imgsz=640,
                                    device=self.device, verbose=False)[0]
        dt = time.perf_counter() - t0
        boxes = result.boxes
        confs = boxes.conf.tolist() if boxes is not None and len(boxes) else []
        return (max(confs) if confs else 0.0), dt


# --------------------------------------------------------------------------- sample

def split_of(video_id, seed, calib_frac):
    h = int(hashlib.sha1(f"{seed}:{video_id}".encode()).hexdigest()[:8], 16) / 0xFFFFFFFF
    return "calibration" if h < calib_frac else "test"


def cmd_sample(a):
    import cv2
    root, out = Path(a.video_root), Path(a.out)
    (out / "frames").mkdir(parents=True, exist_ok=True)
    rng = random.Random(a.seed)
    index = {}                      # file name -> path, built once and only if a manifest path is not found

    def locate(rel):
        rel = rel.replace("\\", "/")          # manifests may carry Windows separators
        direct = root / rel
        if direct.is_file():
            return direct
        if not index:
            for q in root.rglob("*"):
                if q.is_file() and q.suffix.lower() in (".mp4", ".avi", ".mkv", ".mov", ".webm", ".m4v"):
                    index.setdefault(q.name.lower(), q)
        return index.get(Path(rel).name.lower())

    with open(a.manifest, newline="", encoding="utf-8") as f:
        manifest_rows = list(csv.DictReader(f))
    found = [(r, locate(r["relative_path"])) for r in manifest_rows]
    available = [(r, v) for r, v in found if v is not None]
    missing = len(found) - len(available)       # only videos that exist on disk are eligible
    print(f"{len(available)} of {len(found)} manifest videos found under {root}")
    by_class = {}
    for r, v in available:
        by_class.setdefault(r.get("class_name") or "?", []).append((r, v))
    rows, picked = [], []
    for cls in sorted(by_class):
        items = sorted(by_class[cls], key=lambda rv: rv[0]["clip_id"])
        rng.shuffle(items)
        picked += items[:a.per_class]
    unreadable = 0
    for r, video in picked:
        cap = cv2.VideoCapture(str(video))
        if not cap.isOpened():
            unreadable += 1
            print(f"found but OpenCV cannot decode it (codec?): {video}", file=sys.stderr)
            continue
        fps = cap.get(cv2.CAP_PROP_FPS) or 25.0
        total = cap.get(cv2.CAP_PROP_FRAME_COUNT) or 0
        duration = total / fps if total else float(r.get("end_sec") or 0)
        lo = float(r.get("start_sec") or 0)
        hi = min(float(r.get("end_sec") or duration), duration) if duration else float(r.get("end_sec") or 0)
        if hi <= lo:
            cap.release()
            continue
        vid = r["clip_id"]
        split = split_of(vid, a.seed, a.calib_frac)
        for i in range(a.per_video):
            t = lo + (i + 0.5) * (hi - lo) / a.per_video
            cap.set(cv2.CAP_PROP_POS_FRAMES, int(t * fps))
            ok, frame = cap.read()
            if not ok:
                continue
            h, w = frame.shape[:2]
            s = min(1, MAX_SIDE / max(h, w))
            if s < 1:
                frame = cv2.resize(frame, (max(1, int(w * s)), max(1, int(h * s))), interpolation=cv2.INTER_AREA)
            fid = f"{vid}_{int(t * 1000):07d}"
            cv2.imwrite(str(out / "frames" / f"{fid}.png"), frame)
            rows.append({"frame_id": fid, "path": f"frames/{fid}.png", "video_id": vid,
                         "class_name": r.get("class_name", ""), "timestamp": round(t, 2),
                         "split": split, "has_person": "", "difficulty": "", "notes": ""})
        cap.release()
    rng.shuffle(rows)       # annotate in random order so the class/video does not hint at the label
    with open(out / "labels.csv", "w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=LABEL_FIELDS)
        w.writeheader()
        w.writerows(rows)
    if not rows:
        sys.exit(f"No frames written: {missing} videos not found under {root}, {unreadable} found but not decodable.\n"
                 "Check --video-root: it must be the folder that contains the videos (subfolders are searched by file name).")
    if missing or unreadable:
        print(f"warning: {missing} videos not found, {unreadable} not decodable (skipped)", file=sys.stderr)
    print(f"{len(rows)} frames from {len({r['video_id'] for r in rows})} videos -> {out / 'labels.csv'}\n"
          "Fill has_person: 1 = at least one person visible (also far, small or partly hidden), 0 = nobody.\n"
          "Optional difficulty tag (e.g. small, partial, dark, crowd) enables the per-tag recall table.")


# --------------------------------------------------------------------------- label (local browser tool)

LABEL_TAGS = (("small", "q", "Kecil / jauh"), ("partial", "w", "Sebagian terlihat"),
              ("dark", "e", "Gelap / buram"), ("crowd", "r", "Ramai"))

LABEL_HTML = """<!doctype html><html lang="id"><head><meta charset="utf-8"><title>Pelabelan frame</title>
<meta name="viewport" content="width=device-width, initial-scale=1">
<style>
:root{--bg:#14161a;--panel:#1d2026;--line:#2c313a;--text:#e8eaed;--mute:#9aa3af;--yes:#2e9e5b;--no:#c4473a;--acc:#4c8dff}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:15px/1.4 system-ui,Segoe UI,Arial,sans-serif}
header{display:flex;gap:16px;align-items:center;padding:10px 18px;background:var(--panel);border-bottom:1px solid var(--line)}
header b{font-size:16px}.bar{flex:1;height:8px;background:var(--line);border-radius:4px;overflow:hidden}.bar i{display:block;height:100%;background:var(--acc);width:0}
#stats{color:var(--mute);font-size:13px;white-space:nowrap}
main{max-width:1000px;margin:0 auto;padding:14px 18px}
#guide{color:var(--mute);font-size:13px;margin-bottom:10px}
#stage{background:#000;border:1px solid var(--line);border-radius:8px;display:flex;justify-content:center;overflow:auto;position:relative}
#stage img{display:block;height:calc(100vh - 310px);min-height:260px;width:auto;max-width:100%;object-fit:contain}
#stage.zoom{max-height:calc(100vh - 310px);min-height:260px}#stage.zoom img{height:auto;width:1600px;max-width:none;min-height:0}
#badge{position:absolute;top:10px;left:10px;padding:4px 12px;border-radius:14px;font-weight:600;display:none}
.row{display:flex;gap:10px;margin-top:12px;flex-wrap:wrap}
button{font:inherit;color:var(--text);background:var(--panel);border:1px solid var(--line);border-radius:8px;padding:10px 16px;cursor:pointer}
button:hover{border-color:var(--acc)}button kbd{background:var(--bg);border:1px solid var(--line);border-radius:4px;padding:0 6px;margin-right:6px;color:var(--mute)}
#yes{background:var(--yes);border-color:var(--yes);flex:2;font-size:18px;font-weight:600}#no{background:var(--no);border-color:var(--no);flex:2;font-size:18px;font-weight:600}
.tag.on{background:var(--acc);border-color:var(--acc)}
#msg{margin-top:10px;color:#ff8a80;min-height:20px}#done{display:none;margin-top:14px;padding:14px;border:1px solid var(--line);border-radius:8px;background:var(--panel)}
.mute{color:var(--mute);font-size:13px}
</style></head><body>
<header><b>Pelabelan frame</b><div class="bar"><i id="fill"></i></div><div id="stats"></div></header>
<main>
<div id="guide"><b>Ada orang (1)</b> bila minimal satu manusia terlihat, termasuk yang jauh, kecil, atau sebagian terpotong. <b>Tidak ada (0)</b> bila tidak ada manusia sama sekali. Tag kesulitan bersifat opsional.</div>
<div id="stage"><span id="badge"></span><img id="img" alt="frame"></div>
<div class="row"><button id="no"><kbd>0</kbd>Tidak ada orang</button><button id="yes"><kbd>1</kbd>Ada orang</button></div>
<div class="row" id="tags"></div>
<div class="row"><button id="prev"><kbd>&larr;</kbd>Sebelumnya</button><button id="nextb"><kbd>&rarr;</kbd>Berikutnya</button>
<button id="unl"><kbd>U</kbd>Ke yang belum diisi</button><button id="clr"><kbd>Del</kbd>Hapus label</button><button id="zoom"><kbd>Z</kbd>Perbesar</button></div>
<div id="msg"></div>
<div id="done"></div>
<div class="mute" style="margin-top:14px">Tersimpan otomatis ke labels.csv setiap kali menekan tombol. Tutup file itu di Excel selama melabeli. Hentikan alat ini dengan Ctrl+C di terminal.</div>
</main>
<script>
const TAGS=__TAGS__;let items=[],i=0;
const $=id=>document.getElementById(id);
function tagsOf(it){return it.tags?it.tags.split('|').filter(Boolean):[]}
function render(){
 const n=items.length,it=items[i];if(!it)return;
 $('img').src='/img/'+encodeURIComponent(it.id);
 const done=items.filter(x=>x.y!=='').length;
 $('fill').style.width=(100*done/n)+'%';
 $('stats').textContent='Frame '+(i+1)+' / '+n+'  |  terlabel '+done+' / '+n;
 const b=$('badge');if(it.y===''){b.style.display='none'}else{b.style.display='block';b.textContent=it.y==='1'?'Ada orang':'Tidak ada orang';b.style.background=it.y==='1'?'var(--yes)':'var(--no)'}
 const t=tagsOf(it);document.querySelectorAll('.tag').forEach(e=>e.classList.toggle('on',t.includes(e.dataset.t)));
 for(const k of [i+1,i+2]){if(items[k]){new Image().src='/img/'+encodeURIComponent(items[k].id)}}
 const left=items.filter(x=>x.y==='').length;
 if(left===0){const c={};for(const x of items){const k=x.split+' '+x.y;c[k]=(c[k]||0)+1}
  $('done').style.display='block';
  $('done').innerHTML='<b>Semua frame sudah berlabel.</b><br>calibration: orang '+(c['calibration 1']||0)+', kosong '+(c['calibration 0']||0)+
   ' &nbsp;|&nbsp; test: orang '+(c['test 1']||0)+', kosong '+(c['test 0']||0)+
   '<br><span class="mute">Target kasar: minimal 30 frame orang dan 30 frame kosong di tiap split. Jika frame kosong kurang, tambahkan video yang jarang ada orangnya (Arson, Explosion, RoadAccidents). Lalu jalankan skor.</span>'}
 else{$('done').style.display='none'}
}
async function save(y,tags){
 const it=items[i];
 try{const r=await fetch('/api/label',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({id:it.id,y:y,tags:tags})});
  const j=await r.json();if(!r.ok){$('msg').textContent=j.error||'Gagal menyimpan';return false}
  it.y=y;it.tags=tags;$('msg').textContent='';return true}
 catch(e){$('msg').textContent='Server tidak terjangkau: '+e;return false}
}
function nextUnlabeled(from){for(let k=1;k<=items.length;k++){const j=(from+k)%items.length;if(items[j].y==='')return j}return -1}
async function answer(y){if(await save(y,items[i].tags)){const j=nextUnlabeled(i);if(j>=0)i=j;else if(i<items.length-1&&y!=='')i++;render()}}
async function toggle(t){const a=tagsOf(items[i]);const k=a.indexOf(t);if(k>=0)a.splice(k,1);else a.push(t);if(await save(items[i].y,a.join('|')))render()}
function go(j){if(j>=0&&j<items.length){i=j;render()}}
TAGS.forEach(([t,k,label])=>{const b=document.createElement('button');b.className='tag';b.dataset.t=t;b.innerHTML='<kbd>'+k.toUpperCase()+'</kbd>'+label;b.onclick=()=>toggle(t);$('tags').appendChild(b)});
$('yes').onclick=()=>answer('1');$('no').onclick=()=>answer('0');$('prev').onclick=()=>go(i-1);$('nextb').onclick=()=>go(i+1);
$('unl').onclick=()=>{const j=nextUnlabeled(i);if(j>=0)go(j)};$('clr').onclick=()=>answer('');
$('zoom').onclick=()=>$('stage').classList.toggle('zoom');
document.addEventListener('keydown',e=>{if(e.ctrlKey||e.metaKey||e.altKey)return;const k=e.key.toLowerCase();
 if(k==='1'||k==='y')answer('1');else if(k==='0'||k==='n')answer('0');
 else if(k==='arrowleft')go(i-1);else if(k==='arrowright')go(i+1);
 else if(k==='u'){const j=nextUnlabeled(i);if(j>=0)go(j)}
 else if(k==='delete'||k==='backspace')answer('');else if(k==='z')$('stage').classList.toggle('zoom');
 else{const t=TAGS.find(x=>x[1]===k);if(t)toggle(t[0])}});
fetch('/api/state').then(r=>r.json()).then(d=>{items=d.items;const j=items.findIndex(x=>x.y==='');i=j>=0?j:0;render()});
</script></body></html>"""


def read_csv_rows(path):
    with open(path, newline="", encoding="utf-8-sig") as f:
        header = f.readline()
        f.seek(0)
        rd = csv.DictReader(f, delimiter=";" if header.count(";") > header.count(",") else ",")
        return list(rd.fieldnames or []), list(rd)


def make_label_server(labels_path, port=8765):
    """HTTP server for labeling frames in the browser. Returns the (not yet serving) server."""
    import threading
    from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
    labels_path = Path(labels_path).resolve()
    root = labels_path.parent
    fields, rows = read_csv_rows(labels_path)
    for col in ("has_person", "difficulty"):
        if col not in fields:
            fields.append(col)
    by_id = {r["frame_id"]: r for r in rows}
    lock = threading.Lock()
    page = LABEL_HTML.replace("__TAGS__", json.dumps(LABEL_TAGS)).encode("utf-8")

    def norm(v):
        v = (v or "").strip().lower()
        return "1" if v in ("1", "true", "yes", "y") else "0" if v in ("0", "false", "no", "n") else ""

    def save():
        tmp = labels_path.with_name(labels_path.name + ".tmp")
        with open(tmp, "w", newline="", encoding="utf-8") as f:
            w = csv.DictWriter(f, fieldnames=fields, extrasaction="ignore")
            w.writeheader()
            w.writerows(rows)
        os.replace(tmp, labels_path)

    class H(BaseHTTPRequestHandler):
        def log_message(self, *args):
            pass

        def _send(self, code, body, ctype="application/json", cache="no-store"):
            self.send_response(code)
            self.send_header("Content-Type", ctype)
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", cache)
            self.end_headers()
            self.wfile.write(body)

        def _json(self, code, obj):
            self._send(code, json.dumps(obj).encode("utf-8"))

        def do_GET(self):
            if self.path in ("/", "/index.html"):
                return self._send(200, page, "text/html; charset=utf-8")
            if self.path == "/api/state":
                with lock:
                    # class_name and video_id are deliberately not sent: the annotator should judge the image only
                    items = [{"id": r["frame_id"], "y": norm(r.get("has_person")), "tags": r.get("difficulty") or "",
                              "split": r.get("split") or ""} for r in rows]
                return self._json(200, {"items": items})
            if self.path.startswith("/img/"):
                from urllib.parse import unquote
                row = by_id.get(unquote(self.path[5:]))
                if row is None:
                    return self._json(404, {"error": "unknown frame"})
                img = (root / row["path"]).resolve()
                if root not in img.parents or not img.is_file():
                    return self._json(404, {"error": "frame file not found"})
                ctype = "image/jpeg" if img.suffix.lower() in (".jpg", ".jpeg") else "image/png"
                return self._send(200, img.read_bytes(), ctype, "max-age=3600")
            self._json(404, {"error": "not found"})

        def do_POST(self):
            if self.path != "/api/label":
                return self._json(404, {"error": "not found"})
            try:
                body = json.loads(self.rfile.read(int(self.headers.get("Content-Length", 0))))
                row = by_id[body["id"]]
                y = norm(body.get("y"))
                known = {t for t, _, _ in LABEL_TAGS}
                tags = "|".join(t for t in str(body.get("tags") or "").split("|") if t in known)
            except (KeyError, ValueError, TypeError):
                return self._json(400, {"error": "bad request"})
            with lock:
                old = (row.get("has_person"), row.get("difficulty"))
                row["has_person"], row["difficulty"] = y, tags
                try:
                    save()
                except OSError as e:           # typically labels.csv is open in Excel on Windows
                    row["has_person"], row["difficulty"] = old
                    return self._json(500, {"error": f"Tidak bisa menyimpan {labels_path.name} ({e.strerror or e}). "
                                                     "Tutup file itu di Excel lalu coba lagi."})
            return self._json(200, {"ok": True})

    return ThreadingHTTPServer(("127.0.0.1", port), H)


def cmd_label(a):
    import webbrowser
    try:
        server = make_label_server(a.labels, a.port)
    except OSError as e:
        sys.exit(f"Cannot start on port {a.port}: {e}. Try --port {a.port + 1}.")
    url = f"http://127.0.0.1:{server.server_address[1]}/"
    print(f"Alat pelabelan berjalan di {url}\nKeyboard: 1 = ada orang, 0 = tidak ada, panah = pindah, U = ke yang belum diisi, "
          "Q/W/E/R = tag kesulitan.\nTekan Ctrl+C untuk berhenti; label tersimpan otomatis ke " + str(a.labels))
    if not a.no_browser:
        webbrowser.open(url)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("\nSelesai.")
    finally:
        server.server_close()


# --------------------------------------------------------------------------- score

def read_labels(path, require_labels=True):
    rows = []
    with open(path, newline="", encoding="utf-8-sig") as f:        # utf-8-sig: Excel may add a BOM
        header = f.readline()
        f.seek(0)
        # Excel with Indonesian/European regional settings saves CSV with ';' instead of ','
        delimiter = ";" if header.count(";") > header.count(",") else ","
        for r in csv.DictReader(f, delimiter=delimiter):
            v = (r.get("has_person") or "").strip().lower()
            if v in ("1", "true", "yes", "y"):
                r["y"] = 1
            elif v in ("0", "false", "no", "n"):
                r["y"] = 0
            else:
                r["y"] = None
            rows.append(r)
    if require_labels:
        labeled = [r for r in rows if r["y"] is not None]
        if len(labeled) < len(rows):
            print(f"note: {len(rows) - len(labeled)} of {len(rows)} frames have no has_person label and are ignored",
                  file=sys.stderr)
        return labeled
    return rows


def load_scores(path):
    out = {}
    p = Path(path)
    if p.exists():
        for line in p.read_text(encoding="utf-8").splitlines():
            if line.strip():
                r = json.loads(line)
                out[(r["frame_id"], r["detector"])] = r
    return out


def cmd_score(a):
    labels = read_labels(a.labels)
    if not labels:
        sys.exit("No labeled frames: fill the has_person column (1 / 0) in labels.csv first, save it, then run score again.")
    root = Path(a.labels).parent
    # only successful records count as done: failed frames are retried, and the newest record wins in `analyze`
    previous = load_scores(a.out)
    done = {k for k, v in previous.items() if v.get("score") is not None}
    detectors = [d.strip() for d in a.detectors.split(",") if d.strip()]
    api_key = os.environ.get("CCTV_API_KEY")
    scorers = {}
    if "yolo" in detectors:
        yolo = YoloScorer(a.weights, a.device)
        scorers[f"yolo:{Path(a.weights).stem}"] = lambda p: (*yolo(p), None, None)
    if "llm" in detectors:
        def run_llm(p):
            t0 = time.perf_counter()
            s, ans, has = llm_score(a.endpoint, a.model, p, api_key, a.timeout)
            return s, time.perf_counter() - t0, ans, has
        scorers[f"llm:{a.model}"] = run_llm
    if not scorers:
        sys.exit("--detectors must contain yolo and/or llm")
    out = Path(a.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    with open(out, "a", encoding="utf-8") as sink:
        for name, fn in scorers.items():
            todo = [r for r in labels if (r["frame_id"], name) not in done]
            print(f"{name}: {len(todo)} to score, {len(labels) - len(todo)} already done")
            if todo and not a.no_warmup:
                try:
                    fn(root / todo[0]["path"])      # first call loads the model; not recorded
                except Exception as e:                 # noqa: BLE001
                    msg = str(e)
                    if any(k in msg for k in ("HTTP 404", "HTTP 401", "HTTP 403", "cannot reach")):
                        sys.exit(f"{name}: the model server rejected the first request.\n  {msg}\n"
                                 "Check that Ollama is running, that the model exists (`ollama list`; create it with "
                                 "tools\\setup-model.bat) and that --model / --endpoint match. Nothing was recorded.")
                    print(f"  warm-up failed: {msg}", file=sys.stderr)
            streak = 0
            for i, r in enumerate(todo, 1):
                rec = {"frame_id": r["frame_id"], "detector": name}
                try:
                    score, latency, answer, has_lp = fn(root / r["path"])
                    rec.update(score=round(float(score), 8), latency_s=round(latency, 4),
                               answer=answer, has_logprobs=has_lp)
                except Exception as e:                 # noqa: BLE001 - recorded, not fatal
                    rec.update(score=None, error=str(e)[:300])
                sink.write(json.dumps(rec) + "\n")
                sink.flush()
                if rec["score"] is not None:
                    streak = 0
                elif (r["frame_id"], name) not in previous:      # retried frames that fail again do not count
                    streak += 1
                if streak >= 5:
                    sys.exit(f"{name}: 5 failures in a row, stopping. Last error: {rec['error']}\n"
                             "Fix the cause and run the same command again: only the failed frames are retried.")
                if i % 25 == 0 or i == len(todo):
                    print(f"  {name}: {i}/{len(todo)}")


# --------------------------------------------------------------------------- analysis

def counts(y, s, t):
    pred = s >= t
    tp, fp = int((pred & (y == 1)).sum()), int((pred & (y == 0)).sum())
    return tp, fp, int((y == 1).sum()) - tp, int((y == 0).sum()) - fp


def rates(tp, fp, fn, tn):
    def d(n, m):
        return n / m if m else float("nan")
    p, r = d(tp, tp + fp), d(tp, tp + fn)
    return {"recall": r, "precision": p, "fpr": d(fp, fp + tn),
            "f1": d(2 * p * r, p + r) if tp else (0.0 if tp + fp + fn else float("nan"))}


def curve(y, s):
    """Rows for every distinct valid score as threshold (pass = score >= threshold)."""
    pos, neg = np.sort(s[y == 1]), np.sort(s[y == 0])
    thr = np.unique(s[s >= 0])[::-1]
    rows = []
    for t in thr:
        tp = len(pos) - int(np.searchsorted(pos, t, "left"))
        fp = len(neg) - int(np.searchsorted(neg, t, "left"))
        rows.append((float(t), tp, fp, len(pos) - tp, len(neg) - fp))
    return rows


def average_precision(y, s):
    rows = curve(y, s)
    npos = int((y == 1).sum())
    if not rows or not npos:
        return float("nan")
    ap, prev_r = 0.0, 0.0
    for _, tp, fp, _, _ in rows:
        r, p = tp / npos, tp / (tp + fp) if tp + fp else 1.0
        ap += (r - prev_r) * p
        prev_r = r
    return ap


def roc_auc(y, s):
    pos, neg = s[y == 1], np.sort(s[y == 0])
    if not len(pos) or not len(neg):
        return float("nan")
    less = np.searchsorted(neg, pos, "left")
    equal = np.searchsorted(neg, pos, "right") - less
    return float((less + 0.5 * equal).sum() / (len(pos) * len(neg)))


def floor6(x):
    return math.floor(x * 1e6) / 1e6


def pick_threshold(y, s, target):
    """Highest threshold whose recall on this data is >= target. None if there are no positives."""
    if not (y == 1).any():
        return None, False
    for t, tp, fp, fn, tn in curve(y, s):                  # descending thresholds
        if tp / (tp + fn) >= target:
            return floor6(t), True
    return 0.0, False                                       # target unreachable (e.g. invalid answers)


def best_f1_threshold(y, s):
    best = (-1.0, None)
    for t, tp, fp, fn, tn in curve(y, s):
        f = rates(tp, fp, fn, tn)["f1"]
        if f > best[0] + 1e-12:
            best = (f, floor6(t))
    return best[1]


def per_video_counts(df, t):
    """One row per video: tp, fp, fn, tn, video has a person, gate passes the video."""
    out = []
    for _, g in df.groupby("video_id", sort=True):
        y, s = g["y"].to_numpy(), g["score"].to_numpy()
        tp, fp, fn, tn = counts(y, s, t)
        out.append([tp, fp, fn, tn, int((y == 1).any()), int((y == 1).any() and (s >= t).any())])
    return np.array(out, dtype=float)


def metrics_from(c):
    tp, fp, fn, tn, vp, vpass = c
    m = rates(tp, fp, fn, tn)
    m["video_recall"] = vpass / vp if vp else float("nan")
    return m


def pctl(x):
    x = np.asarray(x, dtype=float)
    x = x[~np.isnan(x)]
    return (float(np.percentile(x, 2.5)), float(np.percentile(x, 97.5))) if len(x) else (float("nan"),) * 2


def fmt(v, d=3):
    return "n/a" if v is None or (isinstance(v, float) and math.isnan(v)) else f"{v:.{d}f}"


def ci(v, lo_hi):
    return f"{fmt(v)} [{fmt(lo_hi[0])}–{fmt(lo_hi[1])}]"


def cmd_analyze(a):
    import pandas as pd
    labels = read_labels(a.labels)
    scores = load_scores(a.scores)
    names = sorted({d for _, d in scores})
    if not names:
        sys.exit("scores file has no records")
    ref = a.reference or next((n for n in names if n.startswith("yolo")), names[0])
    lab = pd.DataFrame(labels)
    if "split" not in lab or lab["split"].fillna("").eq("").any():
        lab["split"] = [r["split"] if r.get("split") else split_of(r["video_id"], a.seed, 0.3) for r in labels]
    lab["difficulty"] = lab["difficulty"].fillna("") if "difficulty" in lab else ""
    data, invalid = {}, {}
    ids = set(lab["frame_id"])
    for n in names:
        recs = {fid: scores[(fid, n)] for fid in ids if (fid, n) in scores}
        df = lab[lab["frame_id"].isin(recs)].copy()
        df["score"] = [(-1.0 if recs[f].get("score") is None else float(recs[f]["score"])) for f in df["frame_id"]]
        df["latency"] = [recs[f].get("latency_s") for f in df["frame_id"]]
        df["has_lp"] = [recs[f].get("has_logprobs") for f in df["frame_id"]]
        invalid[n] = int((df["score"] < 0).sum())
        data[n] = df
    common = set.intersection(*(set(d["frame_id"]) for d in data.values()))
    notes = []
    if len(common) < len(ids):
        notes.append(f"{len(ids) - len(common)} frame berlabel tidak punya skor dari salah satu detektor dan dikeluarkan "
                     "agar semua detektor dibandingkan pada frame yang sama.")
    for n in names:
        data[n] = data[n][data[n]["frame_id"].isin(common)].sort_values("frame_id").reset_index(drop=True)
    base = data[ref]
    cal = base["split"] == "calibration"
    tst = base["split"] == "test"
    out = Path(a.out)
    out.mkdir(parents=True, exist_ok=True)

    def arr(n, mask):
        d = data[n][mask.to_numpy()]
        return d["y"].to_numpy(), d["score"].to_numpy()

    for split, m in (("calibration", cal), ("test", tst)):
        y = base["y"][m].to_numpy()
        if (y == 1).sum() < 30 or (y == 0).sum() < 30:
            notes.append(f"{split}: {int((y == 1).sum())} frame berisi orang dan {int((y == 0).sum())} frame kosong; "
                         "jika salah satunya kurang dari 30, interval lebar dan kesimpulan hanya bersifat sementara.")
    if not cal.any() or not (base["y"][cal] == 1).any():
        sys.exit("The calibration split has no person frames; cannot choose thresholds.")
    if not tst.any():
        sys.exit("The test split is empty.")

    res = {}
    for n in names:
        yc, sc = arr(n, cal)
        t, reached = pick_threshold(yc, sc, a.recall_target)
        t_f1 = best_f1_threshold(yc, sc)
        d = data[n]
        res[n] = {"threshold": t, "target_reached_on_calibration": reached, "threshold_best_f1": t_f1,
                  "invalid": invalid[n]}
        yt, st = arr(n, tst)
        res[n]["auc_test"], res[n]["ap_test"] = roc_auc(yt, st), average_precision(yt, st)
        lat = d["latency"].dropna().to_numpy(dtype=float)
        res[n]["latency"] = ({"median_s": float(np.median(lat)), "p95_s": float(np.percentile(lat, 95)),
                              "frames_per_s": float(1 / lat.mean())} if len(lat) else None)
        if d["has_lp"].notna().any() and not d["has_lp"].dropna().astype(bool).all():
            notes.append(f"{n}: sebagian jawaban tanpa log-probabilities sehingga skornya hanya 0 atau 1; "
                         "threshold hampir tidak bisa disetel.")
        with open(out / f"curve_{n.replace(':', '_').replace('/', '_')}.csv", "w", newline="") as f:
            w = csv.writer(f)
            w.writerow(["threshold", "tp", "fp", "fn", "tn", "recall", "precision", "fpr"])
            y_all, s_all = d["y"].to_numpy(), d["score"].to_numpy()
            for t0, tp, fp, fn, tn in curve(y_all, s_all):
                r = rates(tp, fp, fn, tn)
                w.writerow([t0, tp, fp, fn, tn, fmt(r["recall"], 4), fmt(r["precision"], 4), fmt(r["fpr"], 4)])

    # ---- frozen evaluation on the test split, cluster bootstrap over videos
    rng = np.random.default_rng(a.seed)
    test_df = {n: data[n][tst.to_numpy()] for n in names}
    video_counts = {n: per_video_counts(test_df[n], res[n]["threshold"]) for n in names}
    nv = len(video_counts[ref])
    weights = [np.bincount(rng.integers(0, nv, nv), minlength=nv) for _ in range(a.n_boot)]
    boot = {n: [metrics_from(w @ video_counts[n]) for w in weights] for n in names}
    for n in names:
        point = metrics_from(video_counts[n].sum(axis=0))
        res[n]["test"] = {k: {"value": v, "ci": pctl([b[k] for b in boot[n]])} for k, v in point.items()}
        res[n]["test_counts"] = dict(zip(("tp", "fp", "fn", "tn"), map(int, video_counts[n].sum(axis=0)[:4])))
    deltas = {}
    for n in names:
        if n == ref:
            continue
        deltas[n] = {}
        for k in ("recall", "fpr", "video_recall"):
            d_point = res[n]["test"][k]["value"] - res[ref]["test"][k]["value"]
            deltas[n][k] = {"value": d_point, "ci": pctl([bn[k] - br[k] for bn, br in zip(boot[n], boot[ref])])}

    # ---- reference thresholds (point estimates on test) and per-difficulty recall
    reference_rows = []
    for t in a.reference_thresholds:
        for n in names:
            yt, st = arr(n, tst)
            reference_rows.append((n, t, rates(*counts(yt, st, t))))
    def tag_list(x):
        return [t.strip() for t in str(x).split("|") if t.strip()]
    tags = sorted({t for x in lab["difficulty"] for t in tag_list(x)})
    diff_rows = []
    for tag in tags:
        for n in names:
            d = test_df[n]
            sel = d[d["difficulty"].apply(lambda x: tag in tag_list(x)) & (d["y"] == 1)]
            if len(sel):
                diff_rows.append((tag, n, len(sel), float((sel["score"] >= res[n]["threshold"]).mean())))

    # ---- verdict
    verdict = []
    for n, dl in deltas.items():
        lo = dl["recall"]["ci"][0]
        ok = not math.isnan(lo) and lo >= -a.margin
        lat_n, lat_r = res[n]["latency"], res[ref]["latency"]
        slow = (lat_n["median_s"] / lat_r["median_s"]) if lat_n and lat_r and lat_r["median_s"] else float("nan")
        verdict.append((n, ok, slow))
        res[n]["recall_noninferior"] = ok
        res[n]["latency_ratio_vs_ref"] = slow

    # ---- plots
    try:
        import matplotlib
        matplotlib.use("Agg")
        import matplotlib.pyplot as plt
        fig, ax = plt.subplots(1, 2, figsize=(11, 4.5))
        for n in names:
            yt, st = arr(n, tst)
            cv = curve(yt, st)
            npos = max(1, int((yt == 1).sum()))
            nneg = max(1, int((yt == 0).sum()))
            (line,) = ax[0].plot([r[1] / npos for r in cv], [r[1] / (r[1] + r[2]) if r[1] + r[2] else 1 for r in cv], label=n)
            ax[1].plot([r[2] / nneg for r in cv], [r[1] / npos for r in cv], label=n, color=line.get_color())
            c = res[n]["test_counts"]
            ax[0].scatter([c["tp"] / npos], [c["tp"] / (c["tp"] + c["fp"]) if c["tp"] + c["fp"] else 1],
                          color=line.get_color(), zorder=5)
            ax[1].scatter([c["fp"] / nneg], [c["tp"] / npos], color=line.get_color(), zorder=5)
        ax[0].set(xlabel="recall", ylabel="precision", title="Precision-recall (test)")
        ax[1].set(xlabel="false positive rate", ylabel="recall", title="ROC (test); dots = frozen thresholds")
        ax[0].legend()
        fig.tight_layout()
        fig.savefig(out / "person_curves.png", dpi=130)
        plt.close(fig)
    except Exception as e:      # noqa: BLE001 - the plot is optional
        notes.append(f"plot skipped: {e}")

    # ---- report
    L = ["# Evaluasi deteksi manusia: YOLO vs LLM", ""]
    ncal = base[cal]
    ntst = base[tst]
    L += [f"- Frame berlabel yang dipakai: {len(base)} dari {base['video_id'].nunique()} video "
          f"(calibration: {len(ncal)} frame / {ncal['video_id'].nunique()} video, orang {int((ncal['y']==1).sum())}; "
          f"test: {len(ntst)} frame / {ntst['video_id'].nunique()} video, orang {int((ntst['y']==1).sum())}, "
          f"kosong {int((ntst['y']==0).sum())}).",
          f"- Detektor: {', '.join(names)}. Referensi: **{ref}**.",
          f"- Protokol: threshold tiap detektor dipilih di split *calibration* sebagai nilai tertinggi yang masih "
          f"mencapai recall ≥ {a.recall_target:.0%}, lalu **dibekukan** dan dinilai di split *test*. "
          f"Interval 95% memakai bootstrap per video ({a.n_boot}x), karena frame dalam satu video saling berkorelasi.",
          "- Recall diprioritaskan: orang yang tidak terdeteksi berarti video tidak pernah sampai ke model deskripsi. "
          "False positive hanya menambah beban kerja model.", ""]
    for n in notes:
        L.append(f"> Catatan: {n}")
    if notes:
        L.append("")
    L += ["## Hasil di split test pada threshold beku", "",
          "| Detektor | Threshold | Recall frame | Presisi | FPR | Recall video (gate) | AUC | AP | Invalid |",
          "|---|---|---|---|---|---|---|---|---|"]
    for n in names:
        t = res[n]["test"]
        flag = "" if res[n]["target_reached_on_calibration"] else " (target recall tidak tercapai)"
        L.append(f"| {n} | {res[n]['threshold']:.6f}{flag} | {ci(t['recall']['value'], t['recall']['ci'])} | "
                 f"{ci(t['precision']['value'], t['precision']['ci'])} | {ci(t['fpr']['value'], t['fpr']['ci'])} | "
                 f"{ci(t['video_recall']['value'], t['video_recall']['ci'])} | {fmt(res[n]['auc_test'])} | "
                 f"{fmt(res[n]['ap_test'])} | {res[n]['invalid']} |")
    L += ["", "*Recall video (gate)* = dari video yang berisi orang, berapa persen yang lolos gate (minimal satu frame sampel "
          "melewati threshold). *Invalid* = jawaban model gagal/tidak valid; dihitung sebagai tidak terdeteksi.", ""]
    if deltas:
        L += [f"## Selisih terhadap {ref} (berpasangan, bootstrap sama)", "",
              "| Detektor | Δ recall frame | Δ recall video | Δ FPR |", "|---|---|---|---|"]
        for n, dl in deltas.items():
            L.append(f"| {n} | {ci(dl['recall']['value'], dl['recall']['ci'])} | "
                     f"{ci(dl['video_recall']['value'], dl['video_recall']['ci'])} | {ci(dl['fpr']['value'], dl['fpr']['ci'])} |")
        L.append("")
    L += ["## Kecepatan", "", "| Detektor | Median/frame | P95/frame | Frame/detik |", "|---|---|---|---|"]
    for n in names:
        lt = res[n]["latency"]
        L.append(f"| {n} | {fmt(lt['median_s'], 3) + ' s' if lt else 'n/a'} | {fmt(lt['p95_s'], 3) + ' s' if lt else 'n/a'} | "
                 f"{fmt(lt['frames_per_s'], 2) if lt else 'n/a'} |")
    L += ["", "Latensi diukur di mesin dan server yang menjalankan `score`; ukur ulang di perangkat produksi.", ""]
    if verdict:
        L += ["## Kesimpulan otomatis", "",
              f"Kriteria: batas bawah interval 95% untuk Δ recall frame ≥ −{a.margin:.2f} (non-inferior terhadap {ref}).", ""]
        for n, ok, slow in verdict:
            speed = f"; {slow:.1f}x lebih lambat per frame" if not math.isnan(slow) and slow > 1 else \
                    f"; {1 / slow:.1f}x lebih cepat per frame" if not math.isnan(slow) and slow > 0 else ""
            L.append(f"- **{n}**: kriteria recall {'**terpenuhi**' if ok else '**tidak terbukti terpenuhi**'}{speed}. "
                     "Ini hanya satu sisi keputusan: pertimbangkan juga FPR, latensi pada perangkat nyata, dan "
                     "hilangnya estimasi kecepatan orang pada detektor LLM.")
        L.append("")
    L += ["## Threshold untuk Model settings → Person threshold", "",
          "| Detektor | Dipilih (recall ≥ target di calibration) | Alternatif: F1 tertinggi di calibration |", "|---|---|---|"]
    for n in names:
        L.append(f"| {n} | {res[n]['threshold']:.6f} | {res[n]['threshold_best_f1']} |")
    L += ["", "App memakai satu pengaturan *Person threshold* untuk kedua detektor; isi dengan nilai detektor yang dipilih.", ""]
    if reference_rows:
        L += ["## Pada threshold tetap (titik estimasi, test)", "", "| Detektor | Threshold | Recall | Presisi | FPR |", "|---|---|---|---|---|"]
        for n, t, r in reference_rows:
            L.append(f"| {n} | {t} | {fmt(r['recall'])} | {fmt(r['precision'])} | {fmt(r['fpr'])} |")
        L.append("")
    if diff_rows:
        L += ["## Recall per tag kesulitan (test, threshold beku)", "", "| Tag | Detektor | n frame orang | Recall |", "|---|---|---|---|"]
        for tag, n, k, r in diff_rows:
            L.append(f"| {tag} | {n} | {k} | {fmt(r)} |")
        L.append("")
    L += ["## Batasan", "",
          "- Label dibuat manual pada frame 640 px; kualitas kesimpulan mengikuti kualitas dan jumlah label.",
          "- Data publik (mis. UCF-Crime) tidak mewakili CCTV kantor. Ulangi `analyze` dengan sampel video kantor sebelum threshold dipakai.",
          "- Skor LLM bergantung pada model, prompt, kuantisasi dan server; ganti salah satunya berarti kalibrasi ulang.",
          "- Model `qwen*` di port 11434 dipanggil app lewat API native Ollama; skrip ini memakai endpoint `/v1`, "
          "jadi skor bisa sedikit berbeda dari di app.", ""]
    (out / "person_report.md").write_text("\n".join(L), encoding="utf-8")
    (out / "summary.json").write_text(json.dumps(
        {"reference": ref, "recall_target": a.recall_target, "margin": a.margin, "detectors": res,
         "deltas_vs_reference": deltas, "notes": notes}, indent=2, default=float), encoding="utf-8")
    print("\n".join(L))
    print(f"\nWritten to {out}/")


# --------------------------------------------------------------------------- CLI

def main(argv=None):
    for stream in (sys.stdout, sys.stderr):          # Windows consoles default to cp1252 and choke on the report's symbols
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")
        except (AttributeError, ValueError):
            pass
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)

    s = sub.add_parser("sample", help="cut frames from videos and write labels.csv to annotate")
    s.add_argument("--manifest", required=True, help="eval/manifest.csv (clip_id, relative_path, class_name, start_sec, end_sec)")
    s.add_argument("--video-root", required=True)
    s.add_argument("--out", required=True)
    s.add_argument("--per-class", type=int, default=8, help="videos per class_name")
    s.add_argument("--per-video", type=int, default=6, help="frames per video")
    s.add_argument("--calib-frac", type=float, default=0.3, help="share of videos in the calibration split")
    s.add_argument("--seed", type=int, default=20260929)
    s.set_defaults(fn=cmd_sample)

    lb = sub.add_parser("label", help="label frames in the browser (keyboard: 1 / 0), saves to labels.csv automatically")
    lb.add_argument("--labels", required=True)
    lb.add_argument("--port", type=int, default=8765)
    lb.add_argument("--no-browser", action="store_true")
    lb.set_defaults(fn=cmd_label)

    c = sub.add_parser("score", help="score every labeled frame with YOLO and/or the LLM")
    c.add_argument("--labels", required=True)
    c.add_argument("--out", required=True, help="scores.jsonl (appended; resumes automatically)")
    c.add_argument("--detectors", default="yolo,llm")
    c.add_argument("--weights", default=str(Path(__file__).resolve().parent.parent / "tracking" / "yolov8n.pt"))
    c.add_argument("--device", default="cpu")
    c.add_argument("--endpoint", default="http://127.0.0.1:11434/v1")
    c.add_argument("--model", default="qwen3-vl:4b-cctv")
    c.add_argument("--timeout", type=float, default=300)
    c.add_argument("--no-warmup", action="store_true")
    c.set_defaults(fn=cmd_score)

    z = sub.add_parser("analyze", help="choose thresholds on calibration, evaluate on test, write the report")
    z.add_argument("--labels", required=True)
    z.add_argument("--scores", required=True)
    z.add_argument("--out", required=True)
    z.add_argument("--reference", help="detector name to compare against (default: the YOLO one)")
    z.add_argument("--recall-target", type=float, default=0.95)
    z.add_argument("--margin", type=float, default=0.02, help="allowed recall loss for non-inferiority")
    z.add_argument("--reference-thresholds", type=float, nargs="*", default=[0.25, 0.5])
    z.add_argument("--n-boot", type=int, default=2000)
    z.add_argument("--seed", type=int, default=20260929)
    z.set_defaults(fn=cmd_analyze)

    a = ap.parse_args(argv)
    a.fn(a)


if __name__ == "__main__":
    main()