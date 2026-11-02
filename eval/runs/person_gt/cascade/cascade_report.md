# Simulasi pendekatan gabungan: YOLO sebagai penyaring, LLM untuk frame yang meragukan

- LLM: **llm:qwen3-vl:4b-instruct** (median 3.38 s/frame). Semua angka berasal dari skor yang sudah tercatat; tidak ada model yang dijalankan ulang.
- Frame berlabel: 280 (calibration 105, test 175 frame dari 35 video).
- Konfigurasi dipilih di split *calibration*: paling hemat panggilan LLM yang masih mencapai target recall dengan FPR ≤ 20%; lalu **dibekukan** dan dinilai di split *test* (bootstrap per video, interval 95%).
- Data ini sekitar 68% frame berisi orang, jauh lebih padat daripada CCTV nyata. Kolom bertanda π menghitung ulang biaya dan beban seolah 20% frame berisi orang (`--person-share`); ubah sesuai perkiraan lokasi kantor.
- Biaya deteksi per frame = waktu YOLO + (proporsi frame yang dikirim ke LLM) × waktu LLM. Biaya model deskripsi tidak dihitung, tetapi *beban ke model deskripsi* (proporsi frame yang lolos) ditampilkan.

> Catatan: llm:qwen3-vl:4b-cctv: tidak punya satu pun skor valid (semua panggilan gagal) dan dikeluarkan.

## Dengan yolo:yolov8m (median 0.348 s/frame)

| Strategi | Recall frame | FPR | Recall video | Panggilan LLM/frame (data ini) | Panggilan LLM/frame (π) | Waktu deteksi/frame (π) | Beban ke model deskripsi (π) |
|---|---|---|---|---|---|---|---|
| yolo:yolov8m saja | 0.975 [0.946–1.000] | 0.444 [0.233–0.643] | 1.000 [1.000–1.000] | 0.00 | 0.000 [0.000–0.000] | 0.35 s | 0.55 |
| llm:qwen3-vl:4b-instruct saja | 0.983 [0.957–1.000] | 0.148 [0.017–0.339] | 1.000 [1.000–1.000] | 1.00 | 1.000 [1.000–1.000] | 3.38 s | 0.32 |
| Gabungan (recall ≥ 90% / 95% / 98%) (target 95% / 98% tidak tercapai di calibration; dipakai recall terbaik dalam batas FPR) | 0.992 [0.973–1.000] | 0.241 [0.060–0.446] | 1.000 [1.000–1.000] | 0.29 | 0.649 [0.507–0.779] | 2.54 s | 0.39 |

Biaya total per frame (detik) = waktu deteksi + beban ke model deskripsi × D, dengan **D = waktu model deskripsi per frame yang lolos** (titik estimasi, π):

| Strategi | D = 2 s | D = 5 s | D = 10 s | D = 20 s | D = 40 s |
|---|---|---|---|---|---|
| yolo:yolov8m saja | 1.45 | 3.10 | 5.85 | 11.36 | 22.37 |
| llm:qwen3-vl:4b-instruct saja | 4.01 | 4.96 | 6.53 | 9.69 | 15.99 |
| Gabungan (recall ≥ 90% / 95% / 98%) | 3.32 | 4.50 | 6.45 | 10.36 | 18.18 |

Titik impas (D yang membuat dua strategi sama mahal). Ini hanya membandingkan **biaya**; recall tiap strategi berbeda (lihat tabel pertama), jadi strategi yang lebih murah belum tentu setara kualitasnya:

- llm:qwen3-vl:4b-instruct saja lebih murah dari yolo:yolov8m saja jika D > 12.9 s per frame yang lolos; di bawah itu yolo:yolov8m saja lebih murah.
- Gabungan (recall ≥ 90% / 95% / 98%) lebih murah dari yolo:yolov8m saja jika D > 13.7 s per frame yang lolos; di bawah itu yolo:yolov8m saja lebih murah.
- Gabungan (recall ≥ 90% / 95% / 98%) lebih murah dari llm:qwen3-vl:4b-instruct saja jika D < 11.1 s per frame yang lolos; di atas itu llm:qwen3-vl:4b-instruct saja lebih murah.

Konfigurasi (dibekukan dari calibration):

- **yolo:yolov8m saja**: lolos jika skor ≥ 0.050099
- **llm:qwen3-vl:4b-instruct saja**: lolos jika skor ≥ 0.999978
- **Gabungan (recall ≥ 90% / 95% / 98%)**: YOLO ≥ 0.25 lolos tanpa LLM; 0 ≤ YOLO < 0.25 → tanya LLM (lolos jika p ≥ 0.999993); tidak ada frame yang ditolak tanpa LLM

Selisih berpasangan (test):

| Gabungan vs | Δ recall | Δ FPR | Δ panggilan LLM/frame (π) | Δ waktu deteksi/frame (π, detik) |
|---|---|---|---|---|
| Gabungan (recall ≥ 90% / 95% / 98%) vs llm:qwen3-vl:4b-instruct saja | 0.008 [0.000–0.027] | 0.093 [-0.032–0.246] | -0.351 [-0.493–-0.221] | -0.840 [-1.318–-0.401] |
| Gabungan (recall ≥ 90% / 95% / 98%) vs yolo:yolov8m saja | 0.017 [0.000–0.043] | -0.204 [-0.346–-0.078] | 0.649 [0.507–0.779] | 2.193 [1.715–2.633] |

Pembacaan otomatis:

- **Gabungan (recall ≥ 90% / 95% / 98%)**: memakai LLM pada 0.65 frame (π) dibanding 1,00 untuk LLM saja, yaitu hemat sekitar 35% panggilan; recall tidak berbeda nyata dibanding llm:qwen3-vl:4b-instruct saja (Δ 0.008 [0.000–0.027]) dan tidak berbeda nyata dibanding yolo:yolov8m saja (Δ 0.017 [0.000–0.043]).

## Dengan yolo:yolov8n (median 0.066 s/frame)

| Strategi | Recall frame | FPR | Recall video | Panggilan LLM/frame (data ini) | Panggilan LLM/frame (π) | Waktu deteksi/frame (π) | Beban ke model deskripsi (π) |
|---|---|---|---|---|---|---|---|
| yolo:yolov8n saja | 0.868 [0.791–0.933] | 0.204 [0.050–0.389] | 1.000 [1.000–1.000] | 0.00 | 0.000 [0.000–0.000] | 0.07 s | 0.34 |
| llm:qwen3-vl:4b-instruct saja | 0.983 [0.957–1.000] | 0.148 [0.017–0.339] | 1.000 [1.000–1.000] | 1.00 | 1.000 [1.000–1.000] | 3.38 s | 0.32 |
| Gabungan (recall ≥ 90% / 95% / 98%) (target 95% / 98% tidak tercapai di calibration; dipakai recall terbaik dalam batas FPR) | 0.983 [0.957–1.000] | 0.222 [0.051–0.414] | 1.000 [1.000–1.000] | 0.41 | 0.736 [0.638–0.817] | 2.55 s | 0.37 |

Biaya total per frame (detik) = waktu deteksi + beban ke model deskripsi × D, dengan **D = waktu model deskripsi per frame yang lolos** (titik estimasi, π):

| Strategi | D = 2 s | D = 5 s | D = 10 s | D = 20 s | D = 40 s |
|---|---|---|---|---|---|
| yolo:yolov8n saja | 0.74 | 1.75 | 3.43 | 6.80 | 13.53 |
| llm:qwen3-vl:4b-instruct saja | 4.01 | 4.96 | 6.53 | 9.69 | 15.99 |
| Gabungan (recall ≥ 90% / 95% / 98%) | 3.30 | 4.43 | 6.30 | 10.04 | 17.53 |

Titik impas (D yang membuat dua strategi sama mahal). Ini hanya membandingkan **biaya**; recall tiap strategi berbeda (lihat tabel pertama), jadi strategi yang lebih murah belum tentu setara kualitasnya:

- llm:qwen3-vl:4b-instruct saja lebih murah dari yolo:yolov8n saja jika D > 155.6 s per frame yang lolos; di bawah itu yolo:yolov8n saja lebih murah.
- Gabungan (recall ≥ 90% / 95% / 98%) tidak pernah lebih murah dari yolo:yolov8n saja (tidak lebih cepat dan beban tidak lebih kecil).
- Gabungan (recall ≥ 90% / 95% / 98%) lebih murah dari llm:qwen3-vl:4b-instruct saja jika D < 14.0 s per frame yang lolos; di atas itu llm:qwen3-vl:4b-instruct saja lebih murah.

Konfigurasi (dibekukan dari calibration):

- **yolo:yolov8n saja**: lolos jika skor ≥ 0.164922
- **llm:qwen3-vl:4b-instruct saja**: lolos jika skor ≥ 0.999978
- **Gabungan (recall ≥ 90% / 95% / 98%)**: YOLO ≥ 0.25 lolos tanpa LLM; 0 ≤ YOLO < 0.25 → tanya LLM (lolos jika p ≥ 0.999993); tidak ada frame yang ditolak tanpa LLM

Selisih berpasangan (test):

| Gabungan vs | Δ recall | Δ FPR | Δ panggilan LLM/frame (π) | Δ waktu deteksi/frame (π, detik) |
|---|---|---|---|---|
| Gabungan (recall ≥ 90% / 95% / 98%) vs llm:qwen3-vl:4b-instruct saja | 0.000 [0.000–0.000] | 0.074 [-0.038–0.196] | -0.264 [-0.362–-0.183] | -0.827 [-1.158–-0.553] |
| Gabungan (recall ≥ 90% / 95% / 98%) vs yolo:yolov8n saja | 0.116 [0.052–0.189] | 0.019 [-0.098–0.151] | 0.736 [0.638–0.817] | 2.488 [2.157–2.762] |

Pembacaan otomatis:

- **Gabungan (recall ≥ 90% / 95% / 98%)**: memakai LLM pada 0.74 frame (π) dibanding 1,00 untuk LLM saja, yaitu hemat sekitar 26% panggilan; recall tidak berbeda nyata dibanding llm:qwen3-vl:4b-instruct saja (Δ 0.000 [0.000–0.000]) dan lebih tinggi dibanding yolo:yolov8n saja (Δ 0.116 [0.052–0.189]).

## Dengan yolo:yolov8s (median 0.146 s/frame)

| Strategi | Recall frame | FPR | Recall video | Panggilan LLM/frame (data ini) | Panggilan LLM/frame (π) | Waktu deteksi/frame (π) | Beban ke model deskripsi (π) |
|---|---|---|---|---|---|---|---|
| yolo:yolov8s saja | 0.926 [0.884–0.964] | 0.463 [0.257–0.655] | 1.000 [1.000–1.000] | 0.00 | 0.000 [0.000–0.000] | 0.15 s | 0.56 |
| llm:qwen3-vl:4b-instruct saja | 0.983 [0.957–1.000] | 0.148 [0.017–0.339] | 1.000 [1.000–1.000] | 1.00 | 1.000 [1.000–1.000] | 3.38 s | 0.32 |
| Gabungan (recall ≥ 90% / 95% / 98%) (target 95% / 98% tidak tercapai di calibration; dipakai recall terbaik dalam batas FPR) | 0.983 [0.957–1.000] | 0.204 [0.058–0.381] | 1.000 [1.000–1.000] | 0.37 | 0.724 [0.649–0.799] | 2.60 s | 0.36 |

Biaya total per frame (detik) = waktu deteksi + beban ke model deskripsi × D, dengan **D = waktu model deskripsi per frame yang lolos** (titik estimasi, π):

| Strategi | D = 2 s | D = 5 s | D = 10 s | D = 20 s | D = 40 s |
|---|---|---|---|---|---|
| yolo:yolov8s saja | 1.26 | 2.92 | 5.70 | 11.26 | 22.37 |
| llm:qwen3-vl:4b-instruct saja | 4.01 | 4.96 | 6.53 | 9.69 | 15.99 |
| Gabungan (recall ≥ 90% / 95% / 98%) | 3.31 | 4.39 | 6.19 | 9.79 | 16.98 |

Titik impas (D yang membuat dua strategi sama mahal). Ini hanya membandingkan **biaya**; recall tiap strategi berbeda (lihat tabel pertama), jadi strategi yang lebih murah belum tentu setara kualitasnya:

- llm:qwen3-vl:4b-instruct saja lebih murah dari yolo:yolov8s saja jika D > 13.5 s per frame yang lolos; di bawah itu yolo:yolov8s saja lebih murah.
- Gabungan (recall ≥ 90% / 95% / 98%) lebih murah dari yolo:yolov8s saja jika D > 12.5 s per frame yang lolos; di bawah itu yolo:yolov8s saja lebih murah.
- Gabungan (recall ≥ 90% / 95% / 98%) lebih murah dari llm:qwen3-vl:4b-instruct saja jika D < 17.7 s per frame yang lolos; di atas itu llm:qwen3-vl:4b-instruct saja lebih murah.

Konfigurasi (dibekukan dari calibration):

- **yolo:yolov8s saja**: lolos jika skor ≥ 0.079919
- **llm:qwen3-vl:4b-instruct saja**: lolos jika skor ≥ 0.999978
- **Gabungan (recall ≥ 90% / 95% / 98%)**: YOLO ≥ 0.25 lolos tanpa LLM; 0 ≤ YOLO < 0.25 → tanya LLM (lolos jika p ≥ 0.999993); tidak ada frame yang ditolak tanpa LLM

Selisih berpasangan (test):

| Gabungan vs | Δ recall | Δ FPR | Δ panggilan LLM/frame (π) | Δ waktu deteksi/frame (π, detik) |
|---|---|---|---|---|
| Gabungan (recall ≥ 90% / 95% / 98%) vs llm:qwen3-vl:4b-instruct saja | 0.000 [0.000–0.000] | 0.056 [-0.034–0.148] | -0.276 [-0.351–-0.201] | -0.786 [-1.041–-0.532] |
| Gabungan (recall ≥ 90% / 95% / 98%) vs yolo:yolov8s saja | 0.058 [0.018–0.099] | -0.259 [-0.415–-0.102] | 0.724 [0.649–0.799] | 2.449 [2.194–2.703] |

Pembacaan otomatis:

- **Gabungan (recall ≥ 90% / 95% / 98%)**: memakai LLM pada 0.72 frame (π) dibanding 1,00 untuk LLM saja, yaitu hemat sekitar 28% panggilan; recall tidak berbeda nyata dibanding llm:qwen3-vl:4b-instruct saja (Δ 0.000 [0.000–0.000]) dan lebih tinggi dibanding yolo:yolov8s saja (Δ 0.058 [0.018–0.099]).

## Batasan

- Pilihan konfigurasi memakai grid kasar di calibration yang kecil; ada risiko menyesuaikan diri dengan sampel itu. Karena itu hasilnya dinilai di test yang terpisah, dan tetap perlu dikonfirmasi pada data kantor.
- Frame dengan skor YOLO di bawah ambang bawah tidak pernah sampai ke LLM, jadi gabungan tidak bisa menyelamatkan orang yang sama sekali tidak terdeteksi YOLO kecuali ambang bawahnya 0.
- Waktu per frame diukur di mesin pengujian (sebagian LLM berjalan di CPU); ukur ulang di perangkat produksi. Angka π adalah asumsi, bukan pengukuran.
- LLM tidak memberi kotak atau estimasi kecepatan orang; gabungan tetap memerlukan YOLO untuk itu.
