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
| Manifest sha256 | a04ad4926919ff9c... |
| Mulai | 2026-11-03T04:40:13+00:00 |

## Hasil utama (held-out test)

Angka notebook dikutip dari output `evaluation.ipynb` (sel 16, 18, 20). Dua acuan dipakai karena angka 0,914 di slide berasal dari threshold 0,0049 pada skor notebook; baris 'flag yes/no' adalah acuan yang memakai flag yang dibangkitkan model.

Baris **Aplikasi** memakai flag yang diputuskan aplikasi saat run (threshold p(yes) = 0.5). Bagian 'Kurva threshold' di bawah menyapu semua threshold dari skor yang direkam.

### test / main (n=38, positif=30, Normal=8)

| Pipeline | Recall (semua positif) | Precision | F1 | False alarm (Normal) | Review rate | TP/FN/FP/TN |
|---|---|---|---|---|---|---|
| Notebook, flag yes/no (gated_8) | 0.629 | 1.000 | 0.772 | 0.000 | 0.483 | 44/26/0/21 |
| Notebook, flag yes/no (ungated_8) | 0.614 | 1.000 | 0.761 | 0.000 | 0.472 | 43/27/0/21 |
| Notebook, flag yes/no (gated_16) | 0.586 | 1.000 | 0.739 | 0.000 | 0.451 | 41/29/0/21 |
| Notebook, threshold 0.0049 (slide: 0.914) | 0.914 | 0.955 | 0.934 | 0.143 | 0.736 | 64/6/3/18 |
| **Aplikasi** (flag model) | 0.533 [0.367-0.700] | 0.889 [0.672-0.969] | 0.667 [0.512-0.800] | 0.250 [0.071-0.591] | 0.474 [0.316-0.632] | 16/14/2/6 |

Baris aplikasi menampilkan CI 95%: recall, F1 dan review rate dengan bootstrap (2000 resampel, positif dan Normal diambil terpisah); precision dan false alarm dengan interval Wilson, karena bootstrap memberi interval selebar nol saat jumlah kejadiannya nol.

Coverage aplikasi: 100.0% (38/38 valid; 0 positif dan 0 Normal tidak valid). Recall dihitung atas semua positif, jadi output tidak valid dihitung sebagai terlewat.

- Recall aplikasi 0.533: tidak dapat dibedakan dari notebook flag yes/no; CI 0.367-0.700 mencakup 0.629.
- Terhadap angka di slide: lebih rendah dari angka di slide; batas atas CI 0.700 di bawah 0.914.
- Target operasional notebook adalah recall 0,90: belum tercapai pada titik estimasi.

### test / person_optional (n=9, positif=9, Normal=0)

| Pipeline | Recall (semua positif) | Precision | F1 | False alarm (Normal) | Review rate | TP/FN/FP/TN |
|---|---|---|---|---|---|---|
| Notebook, flag yes/no (gated_8) | 0.524 | 1.000 | 0.688 | n/a | 0.524 | 11/10/0/0 |
| Notebook, threshold 0.0049 (slide: 0.914) | 0.714 | 1.000 | 0.833 | n/a | 0.714 | 15/6/0/0 |
| **Aplikasi** (flag model) | 0.444 [0.111-0.778] | 1.000 [0.510-1.000] | 0.615 [0.200-0.875] | n/a | 0.444 [0.111-0.778] | 4/5/0/0 |

Baris aplikasi menampilkan CI 95%: recall, F1 dan review rate dengan bootstrap (2000 resampel, positif dan Normal diambil terpisah); precision dan false alarm dengan interval Wilson, karena bootstrap memberi interval selebar nol saat jumlah kejadiannya nol.

Coverage aplikasi: 100.0% (9/9 valid; 0 positif dan 0 Normal tidak valid). Recall dihitung atas semua positif, jadi output tidak valid dihitung sebagai terlewat.

## Kurva threshold (skor p(yes))

- Skor p(yes) tersedia untuk 38 dari 38 video valid (test/main); 0 video tanpa orang dihitung 0 karena model tidak dipanggil.
- **Peringatan:** skor berasal dari lebih dari satu model; skor model berbeda tidak sebanding.
- AUC (video valid): 0.792 [0.600-0.950]. Nilai 0,5 berarti skor tidak lebih baik dari menebak.

| Titik operasi | Threshold | Recall (semua positif) | False alarm (Normal) | Precision | Review rate | TP/FN/FP/TN |
|---|---|---|---|---|---|---|
| Aplikasi (flag saat run) | 0.5 | 0.533 [0.367-0.700] | 0.250 [0.071-0.591] | 0.889 | 0.474 | 16/14/2/6 |
| Notebook, threshold 0.0049 (skala skor berbeda) | 0.0049 | 0.914 | 0.143 | 0.955 | 0.736 | 64/6/3/18 |
| Dipilih di calibration (recall ≥ 90%), dibekukan | 2.45617e-09 | 0.900 [0.800-1.000] | 0.750 [0.409-0.929] | 0.818 | 0.868 | 27/3/6/2 |

Recall dihitung atas semua positif; CI 95% bootstrap untuk recall, Wilson untuk false alarm. Skor p(yes) dari model 4B cenderung jenuh (mendekati 0 atau 1), jadi banyak video bisa berbagi skor yang sama dan recall naik dalam lompatan besar.

- Split calibration kecil (30 positif, 9 Normal; batas yang disarankan 10 dan 30). Threshold yang dipilih darinya tidak stabil.
- Recall di titik terbekukan: tidak dapat dibedakan dari angka di slide; CI 0.800-1.000 mencakup 0.914; target 90% tercapai di test.
- Threshold 2.45617e-09 ada di ekor jenuh (dekat 0 atau 1). Nilai sedekat itu rapuh terhadap perubahan model, kuantisasi atau perangkat; periksa kurva dan titik yang lebih longgar sebelum dipakai.

Grafik: `threshold_curve.png`. File `threshold_curve.csv` berisi seluruh tabel per threshold.

### Split lain yang ikut dijalankan

| split | stratum | n | recall (semua positif) | precision | false alarm | coverage |
|---|---|---|---|---|---|---|
| calibration | main | 39 | 0.333 | 0.833 | 0.250 | 0.949 |

## Pemeriksaan flag selalu yes / selalu no

- Di test, 46.8% dari output valid ber-flag `yes`.
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
| upload_rejected | 2 |

Detail per video ada di `invalid_outputs.csv`. Video >5 menit atau >500 MB ditolak aplikasi saat upload (`upload_rejected`) dan dihitung sebagai tidak valid, bukan dibuang. Aplikasi menandai JSON yang terpotong sebagai gagal dan tidak menyelamatkan isinya.

## Latensi

Waktu proses aplikasi per video: median 276.8 s, p90 382.0 s, maks 474.8 s (tidak termasuk waktu upload).

## Batasan yang perlu ditulis di handoff

- Benchmark kecil: 30 positif dan 8 Normal pada test/main. Interval kepercayaan lebar; baca CI, bukan hanya titik estimasi.
- Dataset UCF-Crime/UCA, bukan rekaman CCTV kantor. Hasil ini tidak menjawab kinerja di lapangan.
- Perbedaan pipeline selain model: jumlah frame (sampai 150 vs 8), gating orang tanpa fallback, prompt, parsing JSON, dekode FFmpeg vs OpenCV.
- Nilai threshold notebook (0,0049) tidak bisa dipindahkan ke aplikasi: skala skor bergantung pada model, prompt dan jumlah frame. Bandingkan titik operasi (recall pada false alarm tertentu), bukan angka threshold.
- Label benchmark mengikuti kelas anomali UCF (bukan penilaian kecurigaan oleh petugas).
- Satu kali jalan dengan decoding greedy; variasi antar-jalan tidak diukur.
