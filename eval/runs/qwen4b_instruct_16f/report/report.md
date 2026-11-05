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
| Mulai | 2026-10-16T10:07:47+00:00 |

## Hasil utama (held-out test)

Angka notebook bawaan dikutip dari output `evaluation.ipynb` (sel 16, 18, 20) dan dihitung pada set penuh test (main: 91 klip, person_optional: 21 klip). Angka itu hanya ditampilkan bila run ini berukuran sama; untuk subset, berikan `--notebook-predictions` agar dihitung pada klip yang sama. Dua acuan dipakai: baris 'flag yes/no' memakai flag yang dibangkitkan model, sedangkan baris threshold 0,0049 memakai skor notebook yang skalanya berbeda dari aplikasi. Recall baris threshold itu bukan angka sistem aplikasi; untuk aplikasi pakai titik terbekukan dari calibration di bagian kurva threshold.

Run ini tidak menyimpan skor p(yes) (`suspicious_score`), jadi hanya threshold yang dipakai aplikasi saat run yang bisa dinilai. Jalankan ulang dengan `run_app_eval.py` versi terbaru untuk kurva threshold.

### test / main (n=38, positif=30, Normal=8)

| Pipeline | Recall (semua positif) | Precision | F1 | False alarm (Normal) | Review rate | TP/FN/FP/TN |
|---|---|---|---|---|---|---|
| **Aplikasi** (flag model) | 0.667 [0.500-0.833] | 0.952 [0.773-0.992] | 0.784 [0.638-0.893] | 0.125 [0.022-0.471] | 0.553 [0.421-0.684] | 20/10/1/7 |

Baris aplikasi menampilkan CI 95%: recall, F1 dan review rate dengan bootstrap (2000 resampel, positif dan Normal diambil terpisah); precision dan false alarm dengan interval Wilson, karena bootstrap memberi interval selebar nol saat jumlah kejadiannya nol.

**Catatan pembanding:** Baris notebook dihilangkan: angka bawaan skrip dihitung pada 91 klip (70 positif), sedangkan run ini 38 klip (30 positif), sehingga tidak sebanding. Berikan `--notebook-predictions` (predictions.jsonl notebook) agar dihitung ulang pada klip yang sama.

Coverage aplikasi: 100.0% (38/38 valid; 0 positif dan 0 Normal tidak valid). Recall dihitung atas semua positif, jadi output tidak valid dihitung sebagai terlewat.

- Target operasional notebook adalah recall 0,90: belum tercapai pada titik estimasi.

### test / person_optional (n=9, positif=9, Normal=0)

| Pipeline | Recall (semua positif) | Precision | F1 | False alarm (Normal) | Review rate | TP/FN/FP/TN |
|---|---|---|---|---|---|---|
| **Aplikasi** (flag model) | 0.556 [0.222-0.889] | 1.000 [0.566-1.000] | 0.714 [0.364-0.941] | n/a | 0.556 [0.222-0.889] | 5/4/0/0 |

Baris aplikasi menampilkan CI 95%: recall, F1 dan review rate dengan bootstrap (2000 resampel, positif dan Normal diambil terpisah); precision dan false alarm dengan interval Wilson, karena bootstrap memberi interval selebar nol saat jumlah kejadiannya nol.

**Catatan pembanding:** Baris notebook dihilangkan: angka bawaan skrip dihitung pada 21 klip (21 positif), sedangkan run ini 9 klip (9 positif), sehingga tidak sebanding. Berikan `--notebook-predictions` (predictions.jsonl notebook) agar dihitung ulang pada klip yang sama.

Coverage aplikasi: 100.0% (9/9 valid; 0 positif dan 0 Normal tidak valid). Recall dihitung atas semua positif, jadi output tidak valid dihitung sebagai terlewat.

## Pemeriksaan flag selalu yes / selalu no

- Di test, 55.3% dari output valid ber-flag `yes`.
- Tidak ada stratum dengan 95% atau lebih flag yang sama.
- Prompt aplikasi berbunyi 'jika ragu, jawab yes', jadi false alarm pada kelas Normal adalah angka yang paling perlu dibaca di samping recall.

## Pengaruh gating orang

Aplikasi tidak memanggil model bila YOLO tidak menemukan orang; hasilnya `no` secara otomatis. Notebook memakai uniform fallback pada kasus yang sama, jadi ini perbedaan pipeline nyata, terutama untuk kelas person_optional (Arson, Explosion, RoadAccidents).

| Video positif (test) | n | Terdeteksi | Recall |
|---|---|---|---|
| tanpa orang terdeteksi (LLM tidak dipanggil) | 1 | 0 | 0.000 |
| ada orang terdeteksi | 38 | 25 | 0.658 |

## Output tidak valid dan penolakan

Semua output valid.

Detail per video ada di `invalid_outputs.csv`. Video >5 menit atau >500 MB ditolak aplikasi saat upload (`upload_rejected`) dan dihitung sebagai tidak valid, bukan dibuang. Aplikasi menandai JSON yang terpotong sebagai gagal dan tidak menyelamatkan isinya.

## Latensi

Waktu proses aplikasi per video: median 272.8 s, p90 339.6 s, maks 368.0 s (tidak termasuk waktu upload).

## Batasan yang perlu ditulis di handoff

- Benchmark kecil: 30 positif dan 8 Normal pada test/main. Interval kepercayaan lebar; baca CI, bukan hanya titik estimasi.
- Dataset UCF-Crime/UCA, bukan rekaman CCTV kantor. Hasil ini tidak menjawab kinerja di lapangan.
- Perbedaan pipeline selain model: jumlah frame (sampai 150 vs 8), gating orang tanpa fallback, prompt, parsing JSON, dekode FFmpeg vs OpenCV.
- Run ini tidak merekam skor p(yes), jadi threshold tidak bisa dikalibrasi dari hasilnya.
- Label benchmark mengikuti kelas anomali UCF (bukan penilaian kecurigaan oleh petugas).
- Satu kali jalan dengan decoding greedy; variasi antar-jalan tidak diukur.