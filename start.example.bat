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

rem Mempercepat tes di laptop (opsional):
rem set CCTV_MAX_FRAMES=16

dotnet run
