@echo off
rem Salin file ini menjadi start.bat (sudah di-gitignore), lalu isi nilai aslinya di sana.
cd /d "%~dp0"
set CCTV_FFMPEG=ffmpeg
set "CCTV_YOLO_PYTHON=%~dp0.venv\Scripts\python.exe"

rem Notifikasi: isi URL webhook sendiri. Jangan commit URL asli.
set CCTV_WEBHOOK_URL=

rem Folder watch: memantau folder yang di-link di UI.
set CCTV_WATCH=1
set CCTV_WATCH_INTERVAL=10
set CCTV_AUTORUN=1

rem Kamera RTSP (opsional): nama kamera, lalu satu URL per kamera (CCTV_RTSP_<NAMA>).
rem URL biasanya memuat password. Isi di start.bat saja, jangan di file ini.
rem set CCTV_CAMERAS=lobby,gudang
rem set CCTV_RTSP_LOBBY=rtsp://user:password@192.168.1.10:554/stream1
rem set CCTV_RTSP_GUDANG=rtsp://user:password@192.168.1.11:554/stream1
rem set CCTV_RTSP_SEGMENT_SECONDS=60

rem Retensi segmen RTSP (nilai di bawah adalah default). Hanya berlaku untuk file keluaran RTSP.
rem Video dari folder yang di-link atau hasil upload tidak pernah dihapus.
rem set CCTV_MAX_QUEUE_PER_CAMERA=20
rem set CCTV_KEEP_EMPTY_MINUTES=10
rem set CCTV_KEEP_HOURS=24
rem set CCTV_KEEP_FLAGGED_DAYS=30
rem set CCTV_MAX_DISK_GB=20

rem Mempercepat tes di laptop (opsional):
rem set CCTV_MAX_FRAMES=16

rem Kurangi log HTTP supaya pesan penting terbaca:
set Logging__LogLevel__Microsoft.AspNetCore=Warning

dotnet run
