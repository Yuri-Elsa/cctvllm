# Uji kegagalan (fault injection) aplikasi CCTV

- Dibuat: 2026-11-12T05:19:58+00:00
- Aplikasi: `D:\dev\cctvllm\eval\runs\fault_dbg\app`
- Skenario dijalankan: 1
- Pemeriksaan: 5/5 lolos

Aplikasi asli dijalankan; hanya model, webhook, dan SMTP yang dipalsukan. Satu video bertanda = satu alert yang diharapkan (alert dibuat critical agar tidak kena throttle), kecuali di skenario `crash_waiting`.

## Ringkasan

| Skenario | Lolos | Gagal | Durasi |
|---|---|---|---|
| baseline | 5 | 0 | 3 dtk |

## Rincian

### baseline

- ✅ video selesai diproses (status completed)
- ✅ video ditandai suspicious
- ✅ alert terkirim ke webhook tepat sekali
- ✅ jurnal alert kosong setelah terkirim
- ✅ job mencatat notifiedAt dan tanpa notifyError

## Cara membaca

- ❌ berarti perilaku aplikasi berbeda dari yang diharapkan. Cek `app.log` di folder skenario sebelum menyimpulkan.
- Waktu pulih di skenario webhook dipengaruhi jadwal retry aplikasi (30 dtk, 1 mnt, 2 mnt, ...) dan siklus 10 dtk.
- Yang diuji hanya jalur notifikasi dan pemulihan. Ketepatan deteksi tidak diukur di sini (lihat `agent_eval.py` dan `run_app_eval.py`).
- Pengiriman WhatsApp dan SMTP sungguhan tidak diuji; kanal email hanya dimatikan untuk menguji retry per kanal.
