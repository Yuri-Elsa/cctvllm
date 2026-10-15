@echo off
rem Salin file ini menjadi start.bat (sudah di-gitignore), lalu isi nilai aslinya di sana.
cd /d "%~dp0"
set CCTV_FFMPEG=ffmpeg
set "CCTV_YOLO_PYTHON=%~dp0.venv\Scripts\python.exe"

rem Akses: secara default aplikasi hanya bisa dibuka dari komputer ini (127.0.0.1) tanpa login.
rem Kalau dibuka ke jaringan (CCTV_URL), WAJIB beri password; tanpa itu semua orang di jaringan bisa melihat video.
rem Password lewat HTTP biasa terkirim tanpa enkripsi: untuk jaringan, taruh di belakang HTTPS (reverse proxy).
rem set CCTV_URL=http://0.0.0.0:8766
rem set CCTV_UI_PASSWORD=ganti-dengan-password-panjang

rem Notifikasi: isi URL webhook sendiri. Jangan commit URL asli.
set CCTV_WEBHOOK_URL=

rem Email (SMTP). Port 587 dengan STARTTLS; port 465 tidak didukung. Gmail: pakai App Password, bukan password akun.
rem set CCTV_SMTP_HOST=smtp.gmail.com
rem set CCTV_SMTP_PORT=587
rem set CCTV_SMTP_USER=
rem set CCTV_SMTP_PASSWORD=
rem set CCTV_MAIL_FROM=
rem set CCTV_MAIL_TO=satpam@contoh.com

rem WhatsApp. Pilih satu provider: cloud (resmi dari Meta) atau fonnte (gateway pihak ketiga).
rem set CCTV_WA_PROVIDER=cloud
rem set CCTV_WA_TOKEN=
rem set CCTV_WA_TO=0812xxxxxxx,0857xxxxxxx
rem Khusus cloud: ID nomor pengirim, dan template yang SUDAH DISETUJUI Meta dengan 3 variabel:
rem   {{1}} nama kamera, {{2}} waktu, {{3}} ringkasan. Contoh isi template (variabel jangan di awal/akhir kalimat):
rem   "Peringatan CCTV dari kamera {{1}} pada {{2}}. Ringkasan: {{3}}. Mohon segera ditinjau."
rem Tanpa template, pesan teks bebas hanya sampai ke penerima yang membalas nomor bisnis dalam 24 jam terakhir.
rem set CCTV_WA_PHONE_ID=
rem set CCTV_WA_TEMPLATE=cctv_alert
rem set CCTV_WA_TEMPLATE_LANG=id

rem Folder watch: memantau folder yang di-link di UI.
set CCTV_WATCH=1
set CCTV_WATCH_INTERVAL=3
set CCTV_AUTORUN=1

rem Kamera RTSP (opsional): nama kamera, lalu satu URL per kamera (CCTV_RTSP_<NAMA>).
rem URL biasanya memuat password. Isi di start.bat saja, jangan di file ini.
rem set CCTV_CAMERAS=lobby,gudang
rem set CCTV_RTSP_LOBBY=rtsp://user:password@192.168.1.10:554/stream1
rem set CCTV_RTSP_GUDANG=rtsp://user:password@192.168.1.11:554/stream1
rem Segmen lebih pendek = peringatan lebih cepat (default 60). 20-30 detik masuk akal; makin pendek,
rem makin sering YOLO dijalankan dan makin besar kemungkinan kejadian terpotong di batas segmen.
rem set CCTV_RTSP_SEGMENT_SECONDS=20

rem Retensi segmen RTSP (nilai di bawah adalah default). Hanya berlaku untuk file keluaran RTSP.
rem Video dari folder yang di-link atau hasil upload tidak pernah dihapus.
rem Antrean kamera diproses dari yang terbaru. Isi CCTV_QUEUE_FIFO=1 untuk urutan lama (FIFO).
rem set CCTV_QUEUE_FIFO=1
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