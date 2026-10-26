# CCTV Description — ASP.NET Core

A local .NET 10 web app that reviews CCTV footage with a vision-language model. It takes video from uploads, a linked folder or live RTSP cameras, keeps only the moments where people appear, asks the model for a short narrative in Indonesian, and flags suspicious activity and visible weapons. Flagged videos can trigger notifications (WhatsApp, email, webhook) and are queued for a human to confirm or dismiss.

> **Every flag is an automatic suggestion from a model, not a conclusion.** Descriptions can be wrong, person IDs and speeds are estimates, and a human must review each alert. This is also stated in every notification.

- [How it works](#how-it-works)
- [Requirements](#requirements)
- [Quick start](#quick-start)
- [Using the app](#using-the-app)
- [Configuration reference](#configuration-reference)
- [Live cameras (RTSP)](#live-cameras-rtsp)
- [Notifications](#notifications)
- [Retention](#retention)
- [Security and access](#security-and-access)
- [Notebook GPU backend](#notebook-gpu-backend)
- [HTTP API](#http-api)
- [Project structure](#project-structure)
- [Tests](#tests)
- [Evaluation](#evaluation)
- [Known limitations](#known-limitations)
- [Troubleshooting](#troubleshooting)

## How it works

```
upload / linked folder / RTSP segments
        │
        ▼
   ffmpeg: inspect + poster
        │
        ▼
 person detection  ── nobody found ──►  job completed, "no people", model never called
 (YOLOv8n, or the LLM)
        │  frames with people, motion estimated per person
        ▼
 vision model (Ollama)  ──►  description + suspicious + weapon (JSON)
        │
        ├─ suspicious score from token log-probabilities, compared with a threshold
        ▼
 flagged?  ──►  notification agent  ──►  WhatsApp / email / webhook / log
        │
        ▼
 human review: Confirmed / False alarm
```

Processing details:

- **Person detection** runs every 0.5 s (2 fps) with YOLOv8n (`tracking/track_people.py`). Each person's speed is estimated in body-heights per second and described as still, walking, hurrying or running. Matching between samples is a simple nearest-neighbour association, not ByteTrack, so IDs are anonymous and can switch.
- **Frame selection:** only frames with people are kept. Frames sent to the model are at least 1 s apart, at most **150** per video (`CCTV_MAX_FRAMES`), chosen evenly in time. The measured speeds are passed as text, not extra images.
- **Prompt:** `activity-prompt.txt` is read in full for each generation and is the base prompt. The app appends deployment rules: output language (natural Indonesian), custom flag criteria, optional site/camera context and the weapon on/off switch.
- **Generation:** JSON output with `activity_description`, `suspicious` and `weapon`; up to 600 output tokens, temperature 0.35, top-p 0.9, frequency/presence penalties, fixed seed 7 and thinking disabled. A little temperature plus penalties is deliberate: pure greedy decoding made the small model fall into repeating loops.
- **Suspicious score:** when the server returns log-probabilities, the probability of `"yes"` is compared with the _suspicious threshold_ (default 0.5). Otherwise the model's own yes/no answer is used. Both are saved with the job.
- **Batching (optional):** off by default, one frame per request. When on, up to 10 frames per request. A request rejected for context size is split recursively, and no selected frame is silently dropped. Each request carries the full prompt plus a rolling summary, and any positive flag is kept across batches. Raw batch output is saved as `batch-responses.json` next to the video.
- **Malformed JSON** marks the job as failed and saves the raw text. It is not repaired.
- **One model request at a time.** Videos are processed sequentially.

## Requirements

| Needed for                 | Requirement                                                                                   |
| -------------------------- | --------------------------------------------------------------------------------------------- |
| Always                     | [.NET 10 SDK](https://dotnet.microsoft.com/download), [FFmpeg](https://ffmpeg.org/)           |
| Person detection (default) | Python 3 with `ultralytics` and `opencv-python`; weights are already in `tracking/yolov8n.pt` |
| Local model                | [Ollama](https://ollama.com) with a vision model (`qwen3-vl:4b` recommended)                  |
| Live cameras               | RTSP-capable cameras reachable from this machine                                              |
| Notifications              | Optional: SMTP account, WhatsApp provider, or a webhook URL                                   |

The page loads Bootstrap from a CDN, so the browser needs internet access for the styles to appear. Processing itself does not.

## Quick start

### 1. Python environment for YOLO

```bash
python -m venv .venv
# Windows
.venv\Scripts\pip install ultralytics opencv-python
# macOS / Linux
.venv/bin/pip install ultralytics opencv-python
```

The app looks for the interpreter in `CCTV_YOLO_PYTHON`. If that is not set it falls back to `../../work/yolo-env/bin/python`, which only exists on the original author's machine, so **set it** (see below).

### 2. Local model

```bash
sh tools/setup-model.sh          # macOS / Linux
tools\setup-model.bat            # Windows
```

This pulls `qwen3-vl:4b` and creates `qwen3-vl:4b-cctv`, the same model with a 16,384-token context (room for up to 10 frames plus the prompt). Pass a name to use a different one: `sh tools/setup-model.sh my-model`. The recipe is `tools/Modelfile.qwen3-vl-4b-cctv`.

> The built-in default model name in code is still `gemma4:e2b-it-qat`. **Choose your model in Settings → Model on first run.** The System check tells you when the selected model is not installed.

### 3. Run

**Windows**

1. Copy `start.example.bat` to `start.bat` (it is git-ignored, so secrets stay out of the repo).
2. Adjust the values in it. At minimum check `CCTV_FFMPEG` and `CCTV_YOLO_PYTHON`.
3. Run `start.bat`.

**macOS / Linux**

```bash
export CCTV_FFMPEG=/usr/bin/ffmpeg
export CCTV_YOLO_PYTHON="$PWD/.venv/bin/python"
dotnet run
```

`Start Demo.command` is the original macOS launcher. It expects a .NET SDK, Ollama and a YOLO environment in a `../../work/` folder next to the project, so it only works on the machine it was written for.

Open **http://127.0.0.1:8766**.

### 4. First-run checklist

1. Open **Settings → System check → Run check**. FFmpeg, Python YOLO, YOLO weights, Model server, Data folder, Notifiers and Access should be OK. Warnings are acceptable (for example, no notifier configured); failures need fixing.
2. Pick the model under **Settings → Model** and save.
3. Add a short clip (10–30 s) with people in it as a warm-up. The first request is slow while the model loads.

## Using the app

- **Add videos / Add folder:** upload files, or select a folder and queue every video in it (including subfolders, in filename order). Non-video files are skipped. Limits: 5 minutes and 500 MB per video. This is a one-time import.
- **Link a folder:** enter the folder's full path (`~` is supported). The server reads it in place, nothing is copied, and **Refresh** (or `CCTV_WATCH=1`) picks up new files. Removing a job never deletes the original file. The app's own `data/` folder cannot be linked.
- **Queue controls:** Generate descriptions starts the queue; pause, cancel, retry and remove work per video. At restart the queue is paused and interrupted jobs go back to _queued_.
- **Review panel:** video player, description, `Suspicious` / `Weapon` tags, the suspicious score, the notification and agent decision, and **Confirmed / False alarm** buttons for flagged videos. Pressing the active verdict clears it.
- **Filters:** all vs flagged, and by camera. The **Flagged for review** tile in the header jumps to flagged videos.
- **Export:** CSV or JSON of the queue (respects the camera and flagged filters through the API).
- **Cameras panel:** appears when RTSP cameras are configured and shows each camera's state, last data time and restart count.

### Model settings

| Setting               | What it does                                                                                            |
| --------------------- | ------------------------------------------------------------------------------------------------------- |
| Run model on          | _Ollama_ (local, loopback only) or _Notebook GPU_ (remote, see below)                                   |
| Server URL / Model    | OpenAI-compatible endpoint; the model list is read from the server                                      |
| Person labels (YOLO)  | Draw boxes and per-frame labels on the frames sent to the model                                         |
| Person detector       | `YOLO` (default, fast) or `LLM` (asks the vision model per frame; slower)                               |
| Person threshold      | Minimum detection confidence, 0–1 (default 0.25)                                                        |
| Suspicious threshold  | Cut-off for the `yes` probability, 0–1 (default 0.5)                                                    |
| Notification agent    | Let the model decide channel and urgency (Ollama mode only)                                             |
| Batch processing      | Up to 10 frames per request on stronger hardware                                                        |
| Flag activity         | Your own definition of what to flag (max 4,000 characters). It replaces the default suspicious criteria |
| Site / camera context | Background about the place and permitted activities. Treated as context, not evidence                   |
| Weapon flag           | Turn the weapon check on or off. Exports show `disabled` when off                                       |
| Test notification     | Sends a test message through every configured channel                                                   |
| System check          | Re-runs the setup checks                                                                                |

Settings apply to new runs.

## Configuration reference

All runtime configuration is through environment variables. In `start.bat` use `set NAME=value`.

### Core

| Variable           | Default                          | Purpose                                                                        |
| ------------------ | -------------------------------- | ------------------------------------------------------------------------------ |
| `CCTV_URL`         | `http://127.0.0.1:8766`          | Address to listen on. Binding to a network address requires `CCTV_UI_PASSWORD` |
| `CCTV_DATA_DIR`    | `<app>/data`                     | Uploads, posters, frames and `state.json`                                      |
| `CCTV_FFMPEG`      | `ffmpeg` on PATH                 | Path to the FFmpeg executable                                                  |
| `CCTV_YOLO_PYTHON` | `../../work/yolo-env/bin/python` | Python interpreter with ultralytics and OpenCV                                 |
| `CCTV_MAX_FRAMES`  | `150`                            | Maximum frames sent per video (minimum 2). Lower it to speed up laptops        |
| `CCTV_API_KEY`     | none                             | API key for the notebook backend. Never stored in the browser                  |
| `CCTV_UI_PASSWORD` | none                             | Turns on the sign-in page and protects every page and API call                 |
| `CCTV_REVIEW_URL`  | `CCTV_URL`                       | Base URL used in review links inside notifications                             |

### Folder watch

| Variable              | Default | Purpose                                                                                    |
| --------------------- | ------- | ------------------------------------------------------------------------------------------ |
| `CCTV_WATCH`          | off     | Keep checking the linked folder for new videos (`1`, `true` or `yes`)                      |
| `CCTV_WATCH_INTERVAL` | `10`    | Seconds between checks (minimum 2)                                                         |
| `CCTV_AUTORUN`        | off     | Start the queue automatically when new videos arrive                                       |
| `CCTV_QUEUE_FIFO`     | off     | Set to `1` or `true` to process camera videos oldest-first. By default the newest go first |

### RTSP, retention, watchdog

See the sections below for what these do.

| Variable                           | Default                  |
| ---------------------------------- | ------------------------ |
| `CCTV_CAMERAS`, `CCTV_RTSP_<NAME>` | none                     |
| `CCTV_RTSP_SEGMENT_SECONDS`        | `60` (clamped to 5–300)  |
| `CCTV_RTSP_STALL_SECONDS`          | `max(45, 3 × segment)`   |
| `CCTV_RTSP_DIR`                    | `<data dir>/rtsp`        |
| `CCTV_MAX_QUEUE_PER_CAMERA`        | `20` (0 = no limit)      |
| `CCTV_KEEP_EMPTY_MINUTES`          | `10`                     |
| `CCTV_KEEP_HOURS`                  | `24`                     |
| `CCTV_KEEP_FLAGGED_DAYS`           | `30`                     |
| `CCTV_MAX_DISK_GB`                 | `20` (0 = no limit)      |
| `CCTV_KEEP_ORPHAN_MINUTES`         | `60`                     |
| `CCTV_RETENTION_INTERVAL`          | `30` seconds (minimum 5) |
| `CCTV_CAMERA_OFFLINE_SECONDS`      | `60` (minimum 15)        |
| `CCTV_CAMERA_REMINDER_MINUTES`     | `30` (0 = no reminders)  |

### Notifications

| Variable                                                                                                           | Purpose                                                                                              |
| ------------------------------------------------------------------------------------------------------------------ | ---------------------------------------------------------------------------------------------------- |
| `CCTV_WEBHOOK_URL`                                                                                                 | POST target for every alert                                                                          |
| `CCTV_SMTP_HOST`, `CCTV_SMTP_PORT` (587), `CCTV_SMTP_USER`, `CCTV_SMTP_PASSWORD`, `CCTV_MAIL_FROM`, `CCTV_MAIL_TO` | Email. STARTTLS on port 587; port 465 is not supported. For Gmail use an App Password                |
| `CCTV_WA_PROVIDER`                                                                                                 | `cloud` (Meta WhatsApp Cloud API) or `fonnte`                                                        |
| `CCTV_WA_TOKEN`, `CCTV_WA_TO`                                                                                      | Provider token; comma-separated recipients (`08…`, `+62…` or a group ending `@g.us`)                 |
| `CCTV_WA_COUNTRY`                                                                                                  | Country code for numbers starting with `0` (default `62`)                                            |
| `CCTV_WA_PHONE_ID`, `CCTV_WA_TEMPLATE`, `CCTV_WA_TEMPLATE_LANG`, `CCTV_WA_API_VERSION`                             | Cloud API only: sender ID, approved template with 3 variables, template language (`id`), API version |

Never commit real tokens, passwords or RTSP URLs. Put them in `start.bat` (ignored by git).

## Live cameras (RTSP)

```bat
set CCTV_CAMERAS=lobby,gudang
set CCTV_RTSP_LOBBY=rtsp://user:password@192.168.1.10:554/stream1
set CCTV_RTSP_GUDANG=rtsp://user:password@192.168.1.11:554/stream1
set CCTV_RTSP_SEGMENT_SECONDS=20
```

- FFmpeg cuts each stream into short MP4 segments under `<dir>/<camera>/<yyyyMMdd_HHmmss>.mp4`. Camera names use letters, digits, `-` and `_`. The URL variable is the name upper-cased with `-` turned into `_`.
- Segments are imported as soon as they are finished and processed like any other video. The camera name comes from the sub-folder.
- Shorter segments mean faster alerts but more YOLO runs and a higher chance that an event is split at a boundary. 20–30 s is a reasonable range.
- FFmpeg is restarted if it reports no progress for the stall time. Stream URLs (which may contain passwords) come only from the environment and are removed from logs, errors and API output.
- **Camera watchdog:** a camera with no data for `CCTV_CAMERA_OFFLINE_SECONDS` sends an _offline_ alert, repeats a reminder every `CCTV_CAMERA_REMINDER_MINUTES`, and sends a _back online_ alert when data resumes.

## Notifications

Channels are `log` (always on), `webhook`, `email` and `whatsapp`. A channel is active only when its variables are set.

- **What triggers a message:** a flagged video (suspicious or weapon), a failed analysis, a camera going offline and a camera coming back.
- **Notification agent** (Ollama mode, on by default): for a flagged video the model chooses one of `send_whatsapp`, `send_email`, `ignore` or `request_more_frames`, with an urgency (`low` to `critical`), a two-sentence Indonesian summary and a reason. The decision is saved on the job and shown in the review panel.
  - A video with `weapon = yes` can never be ignored. The guard overrides the agent and sends a critical alert.
  - `request_more_frames` re-analyses the video once. If it is no longer flagged, nothing is sent.
  - If the agent fails or times out (60 s), the app falls back to the default behaviour: send through all active channels.
  - With the agent off, only one channel configured, or notebook mode, alerts go to every active channel.
- **Throttling:** at most one message per camera, alert type and channel set every 5 minutes. Extras are merged into one digest ("+N digabung"). _Critical_ alerts and camera offline/recovered alerts are never held back.
- **Delivery:** three attempts with exponential backoff. Failures are saved on the job (`notifyError`).
- **WhatsApp Cloud API** needs a Meta-approved template with three variables: camera, time, summary. Without a template, free-text messages only reach people who replied to the business number in the last 24 hours.
- `tools/smtp_check.py` sends a test email with the same variables the app uses: `python tools/smtp_check.py`.

## Retention

Applies **only to files in the RTSP folder**. Uploaded videos and files in a linked folder are never deleted by the app.

1. If a camera has more waiting videos than `CCTV_MAX_QUEUE_PER_CAMERA`, the oldest waiting ones are dropped (marked _Dropped_).
2. Segments with nobody detected are removed after `CCTV_KEEP_EMPTY_MINUTES`.
3. Segments with people that were not flagged, and failed or dropped ones, are removed after `CCTV_KEEP_HOURS`.
4. Flagged segments are removed only by age (`CCTV_KEEP_FLAGGED_DAYS`), never to free disk space.
5. Files in the RTSP folder that no record points to are removed after `CCTV_KEEP_ORPHAN_MINUTES`.
6. While the folder is over `CCTV_MAX_DISK_GB`, the oldest unflagged segments are removed first.

## Security and access

- By default the app listens on `127.0.0.1` with no sign-in.
- To reach it from other devices set `CCTV_URL` (for example `http://0.0.0.0:8766`) **and** `CCTV_UI_PASSWORD`. System check warns when a network address has no password.
- With a password set, every page and API call needs a sign-in (12-hour cookie). Five wrong attempts lock an address out for five minutes. Changing the password signs everyone out. API calls without a session get `401`.
- It is one shared password. The password is not encrypted over plain HTTP, so put the app behind HTTPS (a reverse proxy) before exposing it.
- State-changing requests from another origin are rejected, and the _local_ model server must be a loopback address.
- Only use trusted endpoints in notebook mode. Videos are sent to them.
- Per-user accounts and OpenID Connect are the planned next step (see `ADS-ARCHITECTURE.md`).

## Notebook GPU backend

For stronger models, run inference on a GPU host (for example Kaggle) and select **Notebook GPU** in Settings with the host's base URL.

The remote server must provide:

- `GET /api/capabilities` returning `screening_settings: true`
- `POST /api/describe` accepting multipart fields `file` and `settings`

The app sends the video with the flag activity, site context, weapon setting and person-label setting. Set the same `CCTV_API_KEY` on both sides. Person labels, the agent and the local YOLO gating do not apply in this mode, and cancelling only stops waiting locally (remote GPU work may continue).

The notebooks (`01_cctv_qwen3_ui.ipynb` for CUDA and `01_cctv_qwen3_ui_tpu_v5e8.ipynb` for the experimental TPU build) are **not part of this repository**. `evaluation.ipynb` is the evaluation notebook.

## HTTP API

The UI uses these endpoints, and they are what `eval/run_app_eval.py` drives.

| Method and path                                                     | Purpose                                               |
| ------------------------------------------------------------------- | ----------------------------------------------------- |
| `GET /api/state`                                                    | Jobs, settings, run state, linked folder, frame limit |
| `GET /api/health`                                                   | Liveness plus the last System-check result            |
| `GET /api/preflight?refresh=true`                                   | Full System-check report                              |
| `GET /api/models?endpoint=`                                         | List models from a local server                       |
| `POST /api/settings`                                                | Save model and screening settings                     |
| `POST /api/videos`                                                  | Upload one video (raw body, `X-Filename` header)      |
| `POST /api/folder`, `/api/folder/refresh`, `GET /api/folder/status` | Link, refresh and watch the linked folder             |
| `POST /api/queue/start`, `/api/queue/pause`                         | Run or pause the queue                                |
| `POST /api/videos/{id}/cancel`, `/retry`, `/delete`                 | Per-video actions                                     |
| `POST /api/videos/{id}/review`                                      | `{"state": "confirmed" \| "false_alarm" \| null}`     |
| `GET /api/videos/{id}/video`, `/poster.jpg`                         | Media (range requests supported)                      |
| `GET /api/cameras`                                                  | RTSP camera status                                    |
| `GET /api/export?format=csv\|json&camera=&flagged=true`             | Export jobs                                           |
| `POST /api/notifications/test`                                      | Send a test notification                              |

The export includes camera, segment time, description, flags, review state, notification result, model, timings, person-detection stats, suspicious score and threshold, and the agent's action and urgency.

## Project structure

```
Abstraction/        interfaces, entities and request/response models
Infrastructure/
  Helpers/          options read from environment variables, prompt builder, auth
  Services/         DescriptionWorker (queue + model calls), RtspIngestService,
                    FolderWatcherService, RetentionService, CameraWatchdog,
                    NotificationDispatcher / Agent / Notifiers, Preflight, Scoring, ...
EntityFramework/    repositories and queries (QueueStore is a JSON-backed repository)
Web/                MVC controllers and Razor views
wwwroot/            app.js and style.css
tracking/           track_people.py and yolov8n.pt
tools/              model setup scripts, Modelfile, smtp_check.py
eval/               benchmark runner, analysis and manifest
tests/              xUnit project (CctvDemo.Tests)
data/               runtime state, uploads, frames (git-ignored)
```

Persistence sits behind `IQueueRepository`. `QueueStore` keeps state in `data/state.json` and is meant for a single-machine deployment; swap in an EF Core implementation for production. `ADS-ARCHITECTURE.md` describes the layer boundaries, the Telerik/Kendo extension point and the authentication plan.

## Tests

```bash
dotnet test tests/CctvDemo.Tests
```

The unit tests cover authentication and login throttling, folder watching, export, retention, scoring, the notification dispatcher and agent, WhatsApp options and review/preflight logic. The evaluation scripts have their own tests with a mock server:

```bash
python eval/tests/test_eval_pipeline.py
```

## Evaluation

`eval/` measures the real application pipeline through its HTTP API, using the UCF-Crime benchmark listed in `eval/manifest.csv`. The videos themselves are not included.

```bash
# 1. run the app, then queue the manifest clips and record one result per video
python eval/run_app_eval.py --manifest eval/manifest.csv --video-root D:/ucf \
    --settings eval/settings.json --out eval/runs/app_v1

# 2. analyse (bootstrap confidence intervals included)
python eval/analyze_app_eval.py --results eval/runs/app_v1/results.jsonl \
    --manifest eval/manifest.csv --out eval/runs/app_v1/report
```

- Results are appended after every video, so an interrupted run resumes where it stopped.
- `--limit N` is a quick smoke test; `--split`, `--strata`, `--redo-failed` and `--timeout` control what runs.
- `eval/make_subset.py` picks a small balanced subset for quick comparisons of a second configuration.
- `eval/settings*.json` hold the settings applied before a run (`settings.example.json` is the template).
- Outputs go to `eval/runs/`, which is git-ignored.

## Known limitations

- Accuracy on real footage has not been established. Compare configurations on your own clips before relying on results, and expect differences from any notebook experiment (FFmpeg vs OpenCV decoding, Ollama quantisation, image preprocessing).
- A 4B model on a CPU or a small laptop is slow. Long videos and many frames can take minutes each.
- Person IDs and speeds come from a simple tracker at 2 fps and are heuristics, not measurements.
- A large image input can exceed the model's context. The app reports the error instead of dropping frames. Lower `CCTV_MAX_FRAMES` or enable batching.
- Description language is fixed to Indonesian by the prompt.
- The queue is single-user and single-machine, with one shared password for access.

## Troubleshooting

| Symptom                                         | Likely cause and fix                                                                                 |
| ----------------------------------------------- | ---------------------------------------------------------------------------------------------------- |
| System check: FFmpeg fails                      | Install FFmpeg and set `CCTV_FFMPEG` to the executable                                               |
| System check: Python YOLO fails                 | Set `CCTV_YOLO_PYTHON` to the venv's `python`, and install `ultralytics` and `opencv-python` in it   |
| "Person tracking is not installed"              | Same as above, or `tracking/yolov8n.pt` is missing                                                   |
| Model not installed / cannot reach model server | Start Ollama, run `tools/setup-model.*`, then select the model in Settings                           |
| First video takes very long                     | Cold model load. Warm up with a short clip                                                           |
| "no people detected" and no description         | Expected when nobody is in the clip. The model is not called, and the job still completes            |
| Page has no styling                             | The browser cannot reach the Bootstrap CDN                                                           |
| Cannot sign in after restart                    | Sessions are tied to the password. Changing `CCTV_UI_PASSWORD` signs everyone out                    |
| No alerts arrive                                | Run **Test notification**; check each channel's variables; WhatsApp Cloud needs an approved template |
| Alerts seem missing for one camera              | Throttling merges alerts within 5 minutes. Check the digest ("+N digabung")                          |
| "This video is no longer at its original path"  | The file was moved or deleted from the linked folder                                                 |
