"""Tes pengiriman email dengan variabel lingkungan yang SAMA seperti aplikasi cctvllm.

Pakai ini untuk memastikan SMTP benar sebelum menjalankan aplikasi:
    python tools/smtp_check.py

Variabel: CCTV_SMTP_HOST, CCTV_SMTP_PORT (587), CCTV_SMTP_USER, CCTV_SMTP_PASSWORD,
CCTV_MAIL_FROM, CCTV_MAIL_TO. Aplikasi memakai STARTTLS (EnableSsl) dan tidak mendukung port 465,
jadi skrip ini berperilaku sama. CCTV_SMTP_TLS=0 hanya untuk server uji lokal.
Password tidak pernah dicetak.
"""
import os, smtplib, ssl, sys
from email.message import EmailMessage

host = os.environ.get("CCTV_SMTP_HOST", "").strip()
port = int(os.environ.get("CCTV_SMTP_PORT") or 587)
user = os.environ.get("CCTV_SMTP_USER", "").strip()
password = os.environ.get("CCTV_SMTP_PASSWORD", "")
to = os.environ.get("CCTV_MAIL_TO", "").strip()
sender = os.environ.get("CCTV_MAIL_FROM", "").strip() or user or "cctv@localhost"
tls = os.environ.get("CCTV_SMTP_TLS", "1") != "0"

missing = [n for n, v in (("CCTV_SMTP_HOST", host), ("CCTV_MAIL_TO", to)) if not v]
if missing:
    sys.exit("Belum diisi: " + ", ".join(missing) + " (aplikasi menganggap email nonaktif tanpa keduanya)")
if port == 465:
    print("PERINGATAN: aplikasi tidak mendukung port 465 (SSL langsung). Pakai 587.")
if user and not password:
    print("PERINGATAN: CCTV_SMTP_USER terisi tapi CCTV_SMTP_PASSWORD kosong.")
if not user and not os.environ.get("CCTV_MAIL_FROM"):
    print("PERINGATAN: pengirim jatuh ke 'cctv@localhost'; kebanyakan server menolaknya. Isi CCTV_MAIL_FROM atau CCTV_SMTP_USER.")

message = EmailMessage()
message["From"], message["To"] = sender, to
message["Subject"] = "[CCTV] Tes email"
message.set_content("Ini email uji dari skrip smtp_check.py. Kalau ini sampai, pengaturan SMTP benar.")

try:
    with smtplib.SMTP(host, port, timeout=20) as smtp:
        smtp.ehlo()
        if tls:
            smtp.starttls(context=ssl.create_default_context())
            smtp.ehlo()
        if user:
            smtp.login(user, password)
        smtp.send_message(message)
except smtplib.SMTPAuthenticationError as e:
    sys.exit(f"GAGAL login ({e.smtp_code}). Gmail: pakai App Password, bukan password akun.")
except smtplib.SMTPRecipientsRefused:
    sys.exit("GAGAL: penerima ditolak server. Cek CCTV_MAIL_TO.")
except smtplib.SMTPSenderRefused as e:
    sys.exit(f"GAGAL: pengirim ditolak ({e.smtp_code}). Cek CCTV_MAIL_FROM / CCTV_SMTP_USER.")
except (smtplib.SMTPException, OSError) as e:
    sys.exit(f"GAGAL: {type(e).__name__}: {e}")
print(f"OK: email uji terkirim ke {to} lewat {host}:{port}. Cek kotak masuk (dan spam).")