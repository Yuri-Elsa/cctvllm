# Evaluasi pipeline aplikasi CCTV-LLM

## Konfigurasi yang diuji

| Item | Nilai |
|---|---|
| Mode / model | local / gemma4:e2b-it-qat |
| Endpoint | http://127.0.0.1:11434/v1 |
| Person labels (YOLO) | True |
| Weapon / batch | True / False |
| Batas frame aplikasi | 16 |
| Rata-rata frame terkirim | 14.0 |
| Manifest sha256 | 78c7060334b0ff18... |
| Mulai | 2026-10-15T08:48:17+00:00 |

## Hasil utama (held-out test)

Angka notebook bawaan dikutip dari output `evaluation.ipynb` (sel 16, 18, 20) dan dihitung pada set penuh test (main: 91 klip, person_optional: 21 klip). Angka itu hanya ditampilkan bila run ini berukuran sama; untuk subset, berikan `--notebook-predictions` agar dihitung pada klip yang sama. Dua acuan dipakai: baris 'flag yes/no' memakai flag yang dibangkitkan model, sedangkan baris threshold 0,0049 memakai skor notebook yang skalanya berbeda dari aplikasi. Recall baris threshold itu bukan angka sistem aplikasi; untuk aplikasi pakai titik terbekukan dari calibration di bagian kurva threshold.

Run ini tidak menyimpan skor p(yes) (`suspicious_score`), jadi hanya threshold yang dipakai aplikasi saat run yang bisa dinilai. Jalankan ulang dengan `run_app_eval.py` versi terbaru untuk kurva threshold.

### test / main (n=91, positif=70, Normal=21)

| Pipeline | Recall (semua positif) | Precision | F1 | False alarm (Normal) | Review rate | TP/FN/FP/TN |
|---|---|---|---|---|---|---|
| Notebook, flag yes/no (gated_8) | 0.629 | 1.000 | 0.772 | 0.000 | 0.483 | 44/26/0/21 |
| Notebook, flag yes/no (ungated_8) | 0.614 | 1.000 | 0.761 | 0.000 | 0.472 | 43/27/0/21 |
| Notebook, flag yes/no (gated_16) | 0.586 | 1.000 | 0.739 | 0.000 | 0.451 | 41/29/0/21 |
| Notebook, threshold 0.0049 (set penuh) | 0.914 | 0.955 | 0.934 | 0.143 | 0.736 | 64/6/3/18 |
| **Aplikasi** (flag model) | 0.114 [0.043-0.200] | 0.727 [0.434-0.903] | 0.211 [0.090-0.333] | 0.200 [0.070-0.452] | 0.242 [0.165-0.319] | 8/57/3/12 |

Baris aplikasi menampilkan CI 95%: recall, F1 dan review rate dengan bootstrap (2000 resampel, positif dan Normal diambil terpisah); precision dan false alarm dengan interval Wilson, karena bootstrap memberi interval selebar nol saat jumlah kejadiannya nol.

Coverage aplikasi: 87.9% (80/91 valid; 5 positif dan 6 Normal tidak valid). Recall dihitung atas semua positif, jadi output tidak valid dihitung sebagai terlewat.

- Recall aplikasi 0.114: lebih rendah dari notebook flag yes/no; batas atas CI 0.200 di bawah 0.629.
- Terhadap notebook threshold 0.0049 (set penuh): lebih rendah dari notebook threshold 0.0049 (set penuh); batas atas CI 0.200 di bawah 0.914.
- Target operasional notebook adalah recall 0,90: belum tercapai pada titik estimasi.

### test / person_optional (n=21, positif=21, Normal=0)

| Pipeline | Recall (semua positif) | Precision | F1 | False alarm (Normal) | Review rate | TP/FN/FP/TN |
|---|---|---|---|---|---|---|
| Notebook, flag yes/no (gated_8) | 0.524 | 1.000 | 0.688 | n/a | 0.524 | 11/10/0/0 |
| Notebook, threshold 0.0049 (set penuh) | 0.714 | 1.000 | 0.833 | n/a | 0.714 | 15/6/0/0 |
| **Aplikasi** (flag model) | 0.238 [0.095-0.429] | 1.000 [0.566-1.000] | 0.385 [0.174-0.600] | n/a | 0.238 [0.095-0.429] | 5/16/0/0 |

Baris aplikasi menampilkan CI 95%: recall, F1 dan review rate dengan bootstrap (2000 resampel, positif dan Normal diambil terpisah); precision dan false alarm dengan interval Wilson, karena bootstrap memberi interval selebar nol saat jumlah kejadiannya nol.

Coverage aplikasi: 100.0% (21/21 valid; 0 positif dan 0 Normal tidak valid). Recall dihitung atas semua positif, jadi output tidak valid dihitung sebagai terlewat.

## Pemeriksaan flag selalu yes / selalu no

- Di test, 15.8% dari output valid ber-flag `yes`.
- Tidak ada stratum dengan 95% atau lebih flag yang sama.
- Prompt aplikasi berbunyi 'jika ragu, jawab yes', jadi false alarm pada kelas Normal adalah angka yang paling perlu dibaca di samping recall.

## Pengaruh gating orang

Aplikasi tidak memanggil model bila YOLO tidak menemukan orang; hasilnya `no` secara otomatis. Notebook memakai uniform fallback pada kasus yang sama, jadi ini perbedaan pipeline nyata, terutama untuk kelas person_optional (Arson, Explosion, RoadAccidents).

| Video positif (test) | n | Terdeteksi | Recall |
|---|---|---|---|
| tanpa orang terdeteksi (LLM tidak dipanggil) | 3 | 0 | 0.000 |
| ada orang terdeteksi | 88 | 13 | 0.148 |

## Output tidak valid dan penolakan

| Alasan | Jumlah |
|---|---|
| upload_rejected | 11 |

Detail per video ada di `invalid_outputs.csv`. Video >5 menit atau >500 MB ditolak aplikasi saat upload (`upload_rejected`) dan dihitung sebagai tidak valid, bukan dibuang. Aplikasi menandai JSON yang terpotong sebagai gagal dan tidak menyelamatkan isinya.

## Latensi

Waktu proses aplikasi per video: median 49.3 s, p90 78.4 s, maks 108.6 s (tidak termasuk waktu upload).

## Batasan yang perlu ditulis di handoff

- Benchmark kecil: 70 positif dan 21 Normal pada test/main. Interval kepercayaan lebar; baca CI, bukan hanya titik estimasi.
- Dataset UCF-Crime/UCA, bukan rekaman CCTV kantor. Hasil ini tidak menjawab kinerja di lapangan.
- Perbedaan pipeline selain model: jumlah frame (sampai 150 vs 8), gating orang tanpa fallback, prompt, parsing JSON, dekode FFmpeg vs OpenCV.
- Run ini tidak merekam skor p(yes), jadi threshold tidak bisa dikalibrasi dari hasilnya.
- Label benchmark mengikuti kelas anomali UCF (bukan penilaian kecurigaan oleh petugas).
- Satu kali jalan dengan decoding greedy; variasi antar-jalan tidak diukur.