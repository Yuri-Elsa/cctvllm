# Evaluasi pipeline aplikasi CCTV-LLM

## Konfigurasi yang diuji

| Item | Nilai |
|---|---|
| Mode / model | local / qwen3-vl:4b-instruct |
| Endpoint | http://127.0.0.1:11434/v1 |
| Person labels (YOLO) | True |
| Weapon / batch | True / False |
| Batas frame aplikasi | 16 |
| Rata-rata frame terkirim | 14.4 |
| Manifest sha256 | 729133fdd4b6dbff... |
| Mulai | 2026-11-03T01:07:36+00:00 |

## Hasil utama (held-out test)

Angka notebook bawaan dikutip dari output `evaluation.ipynb` (sel 16, 18, 20) dan dihitung pada set penuh test (main: 91 klip, person_optional: 21 klip). Angka itu hanya ditampilkan bila run ini berukuran sama; untuk subset, berikan `--notebook-predictions` agar dihitung pada klip yang sama. Dua acuan dipakai: baris 'flag yes/no' memakai flag yang dibangkitkan model, sedangkan baris threshold 0,0049 memakai skor notebook yang skalanya berbeda dari aplikasi. Recall baris threshold itu bukan angka sistem aplikasi; untuk aplikasi pakai titik terbekukan dari calibration di bagian kurva threshold.

Baris **Aplikasi** memakai flag yang diputuskan aplikasi saat run (threshold p(yes) = 0.5). Bagian 'Kurva threshold' di bawah menyapu semua threshold dari skor yang direkam.

### test / main (n=38, positif=30, Normal=8)

| Pipeline | Recall (semua positif) | Precision | F1 | False alarm (Normal) | Review rate | TP/FN/FP/TN |
|---|---|---|---|---|---|---|
| **Aplikasi** (flag model) | 0.533 [0.367-0.700] | 0.889 [0.672-0.969] | 0.681 [0.512-0.816] | 0.250 [0.071-0.591] | 0.500 [0.342-0.658] | 16/13/2/6 |

Baris aplikasi menampilkan CI 95%: recall, F1 dan review rate dengan bootstrap (2000 resampel, positif dan Normal diambil terpisah); precision dan false alarm dengan interval Wilson, karena bootstrap memberi interval selebar nol saat jumlah kejadiannya nol.

**Catatan pembanding:** Baris notebook dihilangkan: angka bawaan skrip dihitung pada 91 klip (70 positif), sedangkan run ini 38 klip (30 positif), sehingga tidak sebanding. Berikan `--notebook-predictions` (predictions.jsonl notebook) agar dihitung ulang pada klip yang sama.

Coverage aplikasi: 97.4% (37/38 valid; 1 positif dan 0 Normal tidak valid). Recall dihitung atas semua positif, jadi output tidak valid dihitung sebagai terlewat.

- Target operasional notebook adalah recall 0,90: belum tercapai pada titik estimasi.

### test / person_optional (n=9, positif=9, Normal=0)

| Pipeline | Recall (semua positif) | Precision | F1 | False alarm (Normal) | Review rate | TP/FN/FP/TN |
|---|---|---|---|---|---|---|
| **Aplikasi** (flag model) | 0.444 [0.111-0.778] | 1.000 [0.510-1.000] | 0.615 [0.200-0.875] | n/a | 0.444 [0.111-0.778] | 4/5/0/0 |

Baris aplikasi menampilkan CI 95%: recall, F1 dan review rate dengan bootstrap (2000 resampel, positif dan Normal diambil terpisah); precision dan false alarm dengan interval Wilson, karena bootstrap memberi interval selebar nol saat jumlah kejadiannya nol.

**Catatan pembanding:** Baris notebook dihilangkan: angka bawaan skrip dihitung pada 21 klip (21 positif), sedangkan run ini 9 klip (9 positif), sehingga tidak sebanding. Berikan `--notebook-predictions` (predictions.jsonl notebook) agar dihitung ulang pada klip yang sama.

Coverage aplikasi: 100.0% (9/9 valid; 0 positif dan 0 Normal tidak valid). Recall dihitung atas semua positif, jadi output tidak valid dihitung sebagai terlewat.

## Kurva threshold (skor p(yes))

- Skor p(yes) tersedia untuk 37 dari 37 video valid (test/main); 0 video tanpa orang dihitung 0 karena model tidak dipanggil.
- **Peringatan:** skor berasal dari lebih dari satu model; skor model berbeda tidak sebanding.
- AUC (video valid): 0.802 [0.612-0.970]. Nilai 0,5 berarti skor tidak lebih baik dari menebak.

| Titik operasi | Threshold | Recall (semua positif) | False alarm (Normal) | Precision | Review rate | TP/FN/FP/TN |
|---|---|---|---|---|---|---|
| Aplikasi (flag saat run) | 0.5 | 0.533 [0.333-0.700] | 0.250 [0.071-0.591] | 0.889 | 0.500 | 16/13/2/6 |
| OPTIMISTIS: dipilih di test sendiri (recall ≥ 90%) | 4.29109e-10 | 0.900 [0.800-1.000] | 0.750 [0.409-0.929] | 0.818 | 0.895 | 27/2/6/2 |

Recall dihitung atas semua positif; CI 95% bootstrap untuk recall, Wilson untuk false alarm. Skor p(yes) dari model 4B cenderung jenuh (mendekati 0 atau 1), jadi banyak video bisa berbagi skor yang sama dan recall naik dalam lompatan besar.

- Baris notebook dihilangkan: angka bawaan skrip dihitung pada 91 klip (70 positif), sedangkan run ini 38 klip (30 positif), sehingga tidak sebanding. Berikan `--notebook-predictions` (predictions.jsonl notebook) agar dihitung ulang pada klip yang sama.
- Run ini tidak memuat split calibration, jadi threshold tidak bisa dipilih secara jujur. Baris 'OPTIMISTIS' dipilih dan dinilai pada data yang sama dan melebihkan kinerja. Jalankan `run_app_eval.py --split calibration,test` agar threshold dipilih di calibration lalu dibekukan.

Grafik: `threshold_curve.png`. File `threshold_curve.csv` berisi seluruh tabel per threshold.

## Pemeriksaan flag selalu yes / selalu no

- Di test, 47.8% dari output valid ber-flag `yes`.
- Tidak ada stratum dengan 95% atau lebih flag yang sama.
- Prompt aplikasi berbunyi 'jika ragu, jawab yes', jadi false alarm pada kelas Normal adalah angka yang paling perlu dibaca di samping recall.

## Pengaruh gating orang

Aplikasi tidak memanggil model bila YOLO tidak menemukan orang; hasilnya `no` secara otomatis. Notebook memakai uniform fallback pada kasus yang sama, jadi ini perbedaan pipeline nyata, terutama untuk kelas person_optional (Arson, Explosion, RoadAccidents).

| Video positif (test) | n | Terdeteksi | Recall |
|---|---|---|---|
| tanpa orang terdeteksi (LLM tidak dipanggil) | 1 | 0 | 0.000 |
| ada orang terdeteksi | 38 | 20 | 0.526 |

## Output tidak valid dan penolakan

| Alasan | Jumlah |
|---|---|
| failed | 1 |

Detail per video ada di `invalid_outputs.csv`. Video >5 menit atau >500 MB ditolak aplikasi saat upload (`upload_rejected`) dan dihitung sebagai tidak valid, bukan dibuang. Aplikasi menandai JSON yang terpotong sebagai gagal dan tidak menyelamatkan isinya.

## Latensi

Waktu proses aplikasi per video: median 277.5 s, p90 358.2 s, maks 400.2 s (tidak termasuk waktu upload).

## Batasan yang perlu ditulis di handoff

- Benchmark kecil: 30 positif dan 8 Normal pada test/main. Interval kepercayaan lebar; baca CI, bukan hanya titik estimasi.
- Dataset UCF-Crime/UCA, bukan rekaman CCTV kantor. Hasil ini tidak menjawab kinerja di lapangan.
- Perbedaan pipeline selain model: jumlah frame (sampai 150 vs 8), gating orang tanpa fallback, prompt, parsing JSON, dekode FFmpeg vs OpenCV.
- Nilai threshold notebook (0,0049) tidak bisa dipindahkan ke aplikasi: skala skor bergantung pada model, prompt dan jumlah frame. Bandingkan titik operasi (recall pada false alarm tertentu), bukan angka threshold.
- Label benchmark mengikuti kelas anomali UCF (bukan penilaian kecurigaan oleh petugas).
- Satu kali jalan dengan decoding greedy; variasi antar-jalan tidak diukur.