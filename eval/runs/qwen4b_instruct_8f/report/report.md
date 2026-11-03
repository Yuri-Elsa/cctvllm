# Evaluasi pipeline aplikasi CCTV-LLM

## Konfigurasi yang diuji

| Item | Nilai |
|---|---|
| Mode / model | local / qwen3-vl:4b-instruct |
| Endpoint | http://127.0.0.1:11434/v1 |
| Person labels (YOLO) | True |
| Weapon / batch | True / False |
| Batas frame aplikasi | 8 |
| Rata-rata frame terkirim | 7.4 |
| Manifest sha256 | 729133fdd4b6dbff... |
| Mulai | 2026-10-16T13:50:48+00:00 |

## Hasil utama (held-out test)

Angka notebook dikutip dari output `evaluation.ipynb` (sel 16, 18, 20). Dua acuan dipakai karena aplikasi memakai flag `suspicious` yang dibangkitkan model, **bukan** threshold 0,0049 pada skor logit. Angka 0,914 di slide berasal dari threshold itu; acuan yang sebanding dengan aplikasi adalah baris 'flag yes/no'.

### test / main (n=38, positif=30, Normal=8)

| Pipeline | Recall (semua positif) | Precision | F1 | False alarm (Normal) | Review rate | TP/FN/FP/TN |
|---|---|---|---|---|---|---|
| Notebook, flag yes/no (gated_8) | 0.629 | 1.000 | 0.772 | 0.000 | 0.483 | 44/26/0/21 |
| Notebook, flag yes/no (ungated_8) | 0.614 | 1.000 | 0.761 | 0.000 | 0.472 | 43/27/0/21 |
| Notebook, flag yes/no (gated_16) | 0.586 | 1.000 | 0.739 | 0.000 | 0.451 | 41/29/0/21 |
| Notebook, threshold 0.0049 (slide: 0.914) | 0.914 | 0.955 | 0.934 | 0.143 | 0.736 | 64/6/3/18 |
| **Aplikasi** (flag model) | 0.467 [0.300-0.633] | 0.933 [0.702-0.988] | 0.622 [0.439-0.776] | 0.125 [0.022-0.471] | 0.395 [0.263-0.553] | 14/16/1/7 |

Baris aplikasi menampilkan CI 95%: recall, F1 dan review rate dengan bootstrap (2000 resampel, positif dan Normal diambil terpisah); precision dan false alarm dengan interval Wilson, karena bootstrap memberi interval selebar nol saat jumlah kejadiannya nol.

Coverage aplikasi: 100.0% (38/38 valid; 0 positif dan 0 Normal tidak valid). Recall dihitung atas semua positif, jadi output tidak valid dihitung sebagai terlewat.

- Recall aplikasi 0.467: tidak dapat dibedakan dari notebook flag yes/no; CI 0.300-0.633 mencakup 0.629.
- Terhadap angka di slide: lebih rendah dari angka di slide; batas atas CI 0.633 di bawah 0.914.
- Target operasional notebook adalah recall 0,90: belum tercapai pada titik estimasi.

### test / person_optional (n=9, positif=9, Normal=0)

| Pipeline | Recall (semua positif) | Precision | F1 | False alarm (Normal) | Review rate | TP/FN/FP/TN |
|---|---|---|---|---|---|---|
| Notebook, flag yes/no (gated_8) | 0.524 | 1.000 | 0.688 | n/a | 0.524 | 11/10/0/0 |
| Notebook, threshold 0.0049 (slide: 0.914) | 0.714 | 1.000 | 0.833 | n/a | 0.714 | 15/6/0/0 |
| **Aplikasi** (flag model) | 0.444 [0.111-0.778] | 1.000 [0.510-1.000] | 0.615 [0.200-0.875] | n/a | 0.444 [0.111-0.778] | 4/5/0/0 |

Baris aplikasi menampilkan CI 95%: recall, F1 dan review rate dengan bootstrap (2000 resampel, positif dan Normal diambil terpisah); precision dan false alarm dengan interval Wilson, karena bootstrap memberi interval selebar nol saat jumlah kejadiannya nol.

Coverage aplikasi: 100.0% (9/9 valid; 0 positif dan 0 Normal tidak valid). Recall dihitung atas semua positif, jadi output tidak valid dihitung sebagai terlewat.

## Pemeriksaan flag selalu yes / selalu no

- Di test, 40.4% dari output valid ber-flag `yes`.
- Tidak ada stratum dengan 95% atau lebih flag yang sama.
- Prompt aplikasi berbunyi 'jika ragu, jawab yes', jadi false alarm pada kelas Normal adalah angka yang paling perlu dibaca di samping recall.

## Pengaruh gating orang

Aplikasi tidak memanggil model bila YOLO tidak menemukan orang; hasilnya `no` secara otomatis. Notebook memakai uniform fallback pada kasus yang sama, jadi ini perbedaan pipeline nyata, terutama untuk kelas person_optional (Arson, Explosion, RoadAccidents).

| Video positif (test) | n | Terdeteksi | Recall |
|---|---|---|---|
| tanpa orang terdeteksi (LLM tidak dipanggil) | 1 | 0 | 0.000 |
| ada orang terdeteksi | 38 | 18 | 0.474 |

## Output tidak valid dan penolakan

Semua output valid.

Detail per video ada di `invalid_outputs.csv`. Video >5 menit atau >500 MB ditolak aplikasi saat upload (`upload_rejected`) dan dihitung sebagai tidak valid, bukan dibuang. Aplikasi menandai JSON yang terpotong sebagai gagal dan tidak menyelamatkan isinya.

## Latensi

Waktu proses aplikasi per video: median 339.7 s, p90 408.9 s, maks 462.4 s (tidak termasuk waktu upload).

## Batasan yang perlu ditulis di handoff

- Benchmark kecil: 30 positif dan 8 Normal pada test/main. Interval kepercayaan lebar; baca CI, bukan hanya titik estimasi.
- Dataset UCF-Crime/UCA, bukan rekaman CCTV kantor. Hasil ini tidak menjawab kinerja di lapangan.
- Perbedaan pipeline selain model: jumlah frame (sampai 150 vs 8), gating orang tanpa fallback, prompt, parsing JSON, dekode FFmpeg vs OpenCV.
- Aplikasi tidak punya threshold yang dapat dikalibrasi. Memakai skor logit seperti di notebook membutuhkan perubahan kode.
- Label benchmark mengikuti kelas anomali UCF (bukan penilaian kecurigaan oleh petugas).
- Satu kali jalan dengan decoding greedy; variasi antar-jalan tidak diukur.
