# Uji kegagalan (fault injection) aplikasi CCTV

- Dibuat: 2026-11-05T01:44:37+00:00
- Aplikasi: `D:\buat kp phr\cctvllm\eval\runs\fault_injection\app`
- Skenario dijalankan: 2
- Pemeriksaan: 12/12 lolos

Aplikasi asli dijalankan; hanya model, webhook, dan SMTP yang dipalsukan. Satu video bertanda = satu alert yang diharapkan (alert dibuat critical agar tidak kena throttle), kecuali di skenario `crash_waiting`.

## Ringkasan

| Skenario | Lolos | Gagal | Durasi |
|---|---|---|---|
| baseline | 5 | 0 | 3 dtk |
| crash_retrying | 7 | 0 | 59 dtk |

## Rincian

### baseline

- ✅ video selesai diproses (status completed)
- ✅ video ditandai suspicious
- ✅ alert terkirim ke webhook tepat sekali
- ✅ jurnal alert kosong setelah terkirim
- ✅ job mencatat notifiedAt dan tanpa notifyError

### crash_retrying

- ✅ video selesai diproses (status completed)
- ✅ alert masuk jurnal retry
- ✅ aplikasi hidup lagi setelah restart
- ✅ alert tetap ada di jurnal setelah restart
- ✅ alert terkirim setelah restart dan webhook pulih
- ✅ terkirim tepat sekali
- ✅ jurnal kosong

## Cara membaca

- ❌ berarti perilaku aplikasi berbeda dari yang diharapkan. Cek `app.log` di folder skenario sebelum menyimpulkan.
- Waktu pulih di skenario webhook dipengaruhi jadwal retry aplikasi (30 dtk, 1 mnt, 2 mnt, ...) dan siklus 10 dtk.
- Yang diuji hanya jalur notifikasi dan pemulihan. Ketepatan deteksi tidak diukur di sini (lihat `agent_eval.py` dan `run_app_eval.py`).
- Pengiriman WhatsApp dan SMTP sungguhan tidak diuji; kanal email hanya dimatikan untuk menguji retry per kanal.
