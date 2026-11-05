# Uji kegagalan (fault injection) aplikasi CCTV

- Dibuat: 2026-11-05T01:26:56+00:00
- Aplikasi: `D:\buat kp phr\cctvllm\eval\runs\fault_v1\app`
- Skenario dijalankan: 11
- Pemeriksaan: 89/89 lolos

Aplikasi asli dijalankan; hanya model, webhook, dan SMTP yang dipalsukan. Satu video bertanda = satu alert yang diharapkan (alert dibuat critical agar tidak kena throttle), kecuali di skenario `crash_waiting`.

## Ringkasan

| Skenario | Lolos | Gagal | Durasi |
|---|---|---|---|
| baseline | 5 | 0 | 3 dtk |
| model_down | 8 | 0 | 14 dtk |
| model_bad | 15 | 0 | 11 dtk |
| agent_faults | 13 | 0 | 95 dtk |
| webhook_outage | 8 | 0 | 63 dtk |
| webhook_outage_500 | 8 | 0 | 54 dtk |
| crash_retrying | 7 | 0 | 58 dtk |
| crash_retrying_term | 7 | 0 | 59 dtk |
| crash_waiting | 6 | 0 | 10 dtk |
| crash_midjob | 6 | 0 | 5 dtk |
| corrupt_files | 6 | 0 | 4 dtk |

## Rincian

### baseline

- ✅ video selesai diproses (status completed)
- ✅ video ditandai suspicious
- ✅ alert terkirim ke webhook tepat sekali
- ✅ jurnal alert kosong setelah terkirim
- ✅ job mencatat notifiedAt dan tanpa notifyError

### model_down

- ✅ ada video yang mulai diproses
- ✅ tidak ada video yang macet di 'processing' / antrean berhenti
- ✅ semua video yang terdampak berstatus failed dengan pesan error
- ✅ aplikasi tetap hidup
- ✅ minimal satu alert 'analisis gagal' terkirim (video dari folder terhubung)
- ✅ tidak ada alert 'suspicious' palsu saat model mati
- ✅ semua video selesai setelah model pulih
- ✅ setiap video bertanda menghasilkan tepat satu alert
- ℹ️ alert 'analisis gagal' hanya dibuat untuk video dari ingest otomatis (folder terhubung/RTSP); upload manual tidak memicunya (sengaja, lihat DescriptionWorker.cs)

### model_bad

- ✅ [garbage] job berakhir failed (tidak menggantung), error terisi
- ✅ [garbage] tidak ada alert 'suspicious' palsu
- ✅ [garbage] aplikasi tetap hidup
- ✅ [http500] job berakhir failed (tidak menggantung), error terisi
- ✅ [http500] tidak ada alert 'suspicious' palsu
- ✅ [http500] aplikasi tetap hidup
- ✅ [truncated] job berakhir failed (tidak menggantung), error terisi
- ✅ [truncated] tidak ada alert 'suspicious' palsu
- ✅ [truncated] aplikasi tetap hidup
- ✅ [drop] job berakhir failed (tidak menggantung), error terisi
- ✅ [drop] tidak ada alert 'suspicious' palsu
- ✅ [drop] aplikasi tetap hidup
- ✅ alert 'analisis gagal' terkirim
- ✅ video selesai setelah model pulih
- ✅ alert terkirim tepat sekali setelah pulih

### agent_faults

- ✅ [agen normal] video selesai
- ✅ [agen normal] webhook menerima alert tepat sekali
- ✅ [agen normal] agen memutuskan send_email
- ✅ [agen gagal 500 (harus fallback ke semua kanal)] video selesai
- ✅ [agen gagal 500 (harus fallback ke semua kanal)] webhook menerima alert tepat sekali
- ✅ [agen gagal 500 (harus fallback ke semua kanal)] log agen mencatat fallbackUsed
- ✅ [agen memilih ignore padahal weapon=yes (harus dikoreksi guard)] video selesai
- ✅ [agen memilih ignore padahal weapon=yes (harus dikoreksi guard)] webhook menerima alert tepat sekali
- ✅ [agen memilih ignore padahal weapon=yes (harus dikoreksi guard)] guard mengoreksi: guardOverride=true
- ✅ [agen memilih ignore padahal weapon=yes (harus dikoreksi guard)] urgensi alert yang terkirim = critical
- ✅ kanal email yang gagal disimpan di jurnal untuk retry (hanya email)
- ✅ retry email tidak mengirim ulang ke webhook yang sudah menerima (tanpa duplikat)
- ✅ aplikasi tetap hidup

### webhook_outage

- ✅ video selesai diproses (status completed)
- ✅ alert masuk jurnal retry saat webhook tidak bisa dijangkau
- ✅ job mencatat notifyError
- ✅ tidak ada pengiriman palsu yang tercatat berhasil
- ✅ alert akhirnya terkirim setelah webhook pulih
- ✅ terkirim tepat sekali (tanpa duplikat)
- ✅ jurnal kosong setelah terkirim
- ✅ notifiedAt terisi dan notifyError dibersihkan
- ℹ️ waktu pulih sampai terkirim: 36 detik

### webhook_outage_500

- ✅ video selesai diproses (status completed)
- ✅ alert masuk jurnal retry saat webhook tidak bisa dijangkau
- ✅ job mencatat notifyError
- ✅ tidak ada pengiriman palsu yang tercatat berhasil
- ✅ alert akhirnya terkirim setelah webhook pulih
- ✅ terkirim tepat sekali (tanpa duplikat)
- ✅ jurnal kosong setelah terkirim
- ✅ notifiedAt terisi dan notifyError dibersihkan
- ℹ️ waktu pulih sampai terkirim: 32 detik

### crash_retrying

- ✅ video selesai diproses (status completed)
- ✅ alert masuk jurnal retry
- ✅ aplikasi hidup lagi setelah restart
- ✅ alert tetap ada di jurnal setelah restart
- ✅ alert terkirim setelah restart dan webhook pulih
- ✅ terkirim tepat sekali
- ✅ jurnal kosong

### crash_retrying_term

- ✅ video selesai diproses (status completed)
- ✅ alert masuk jurnal retry
- ✅ aplikasi hidup lagi setelah restart
- ✅ alert tetap ada di jurnal setelah restart
- ✅ alert terkirim setelah restart dan webhook pulih
- ✅ terkirim tepat sekali
- ✅ jurnal kosong

### crash_waiting

- ✅ kedua video selesai
- ✅ alert pertama terkirim langsung
- ✅ alert kedua ditahan untuk digest dan tercatat di jurnal (status waiting)
- ✅ aplikasi hidup lagi setelah restart
- ✅ alert yang ditahan akhirnya terkirim setelah restart
- ✅ masing-masing video tepat satu alert (tanpa duplikat, tanpa hilang)

### crash_midjob

- ✅ video mulai diproses
- ✅ video kembali ke 'queued' (tidak macet di processing)
- ✅ antrean dalam keadaan berhenti setelah restart (sesuai README)
- ✅ tidak ada alert terkirim sebelum video selesai
- ✅ video selesai setelah antrean dijalankan lagi
- ✅ alert terkirim tepat sekali

### corrupt_files

- ✅ aplikasi tetap bisa start dengan state.json dan jurnal rusak
- ✅ salinan state rusak disimpan untuk diperiksa
- ✅ salinan jurnal rusak disimpan untuk diperiksa
- ✅ antrean dimulai kosong
- ✅ pipeline berfungsi normal setelah pemulihan
- ✅ alert terkirim tepat sekali

## Cara membaca

- ❌ berarti perilaku aplikasi berbeda dari yang diharapkan. Cek `app.log` di folder skenario sebelum menyimpulkan.
- Waktu pulih di skenario webhook dipengaruhi jadwal retry aplikasi (30 dtk, 1 mnt, 2 mnt, ...) dan siklus 10 dtk.
- Yang diuji hanya jalur notifikasi dan pemulihan. Ketepatan deteksi tidak diukur di sini (lihat `agent_eval.py` dan `run_app_eval.py`).
- Pengiriman WhatsApp dan SMTP sungguhan tidak diuji; kanal email hanya dimatikan untuk menguji retry per kanal.
