# Privacy and data protection notes

The app records and describes people. This page lists what it stores, for how long, and who can see it, so the owner can
check it against Indonesia's Personal Data Protection Law (UU No. 27 Tahun 2022, "UU PDP") and their own policy.
It is a technical inventory, not legal advice: confirm the legal basis, signage and retention periods with your legal
adviser or data protection officer.

## What is stored

| Data | Where | Kept for |
| --- | --- | --- |
| Video clips from cameras (RTSP) | RTSP folder | Empty clips `CCTV_KEEP_EMPTY_MINUTES` (10), unflagged `CCTV_KEEP_HOURS` (24), flagged `CCTV_KEEP_FLAGGED_DAYS` (30) |
| Uploaded and linked-folder videos | data folder / your folder | Never deleted by the app. Delete them yourself |
| Descriptions, flags, verdicts, posters, frames | `state.json`, data folder | As long as the video record exists |
| Alerts (webhook, email, WhatsApp) | sent to those services | Outside the app. Alerts contain a description and a review link, not the clip |
| Audit log (user, address, action) | `audit.jsonl` | Rotated at 10 MB. Not deleted by the app |
| Accounts | `users.json` | Until you remove them. Passwords are stored as PBKDF2 hashes |

## Controls in the app

- Access: sign-in with per-user roles (viewer, reviewer, admin). Open access is only safe on loopback.
- Audit: `GET /api/audit` (admin) shows who changed a verdict or a setting, deleted a video, linked a folder, or signed in.
- Sessions: `GET /api/sessions` and `POST /api/sessions/{id}/revoke` (admin) end a sign-in from the server.
- Retention: the variables above. Lower them to keep less.
- Model: the description is produced by a local model (Ollama). No video leaves the machine unless you point the model
  endpoint or a notifier at an outside service.

## Not done yet

- Face blur on stored clips or in alerts. Today clips are stored and played as recorded.
- Deleting a person's data on request: there is no per-person search. Deleting the video record and file is the only tool.
- Purging old audit files: rotated `audit-*.jsonl` files stay until you delete them.

## For the owner to decide

- The lawful basis and purpose for recording, and the notice shown to people on site.
- A retention period for flagged clips that you can justify (the default is 30 days).
- Who gets which role, and a review of `GET /api/audit` at a regular interval.
- A process for access and deletion requests, and for reporting a breach (UU PDP sets a notification deadline).