# Evaluasi deteksi manusia: YOLO vs LLM

- Frame berlabel yang dipakai: 280 dari 56 video (calibration: 105 frame / 21 video, orang 68; test: 175 frame / 35 video, orang 121, kosong 54).
- Detektor: llm:qwen3-vl:4b-instruct, yolo:yolov8m, yolo:yolov8n, yolo:yolov8s. Referensi: **yolo:yolov8m**.
- Protokol: threshold tiap detektor dipilih di split *calibration* sebagai nilai tertinggi yang masih mencapai recall ≥ 95%, lalu **dibekukan** dan dinilai di split *test*. Interval 95% memakai bootstrap per video (2000x), karena frame dalam satu video saling berkorelasi.
- Recall diprioritaskan: orang yang tidak terdeteksi berarti video tidak pernah sampai ke model deskripsi. False positive hanya menambah beban kerja model.

> Catatan: llm:qwen3-vl:4b-cctv: tidak punya satu pun skor valid (semua panggilan gagal) dan dikeluarkan dari analisis.
> Catatan: llm:qwen3-vl:4b-instruct: threshold terpilih 0.999719 berada di ekor jenuh (probabilitas mendekati 1). Nilai sedekat itu rapuh terhadap perubahan model, kuantisasi atau perangkat; jangan salin ke Settings tanpa memeriksa kurva (curve_*.csv) dan titik yang lebih longgar.
> Catatan: yolo:yolov8m: pada target recall 95% threshold yang terpilih meloloskan hampir semua frame (FPR 1.000). Ada frame berisi orang yang tidak mendapat skor di atas lantai pencatatan, jadi target itu hanya tercapai tanpa gate yang berguna. Bandingkan pada FPR yang disamakan, bukan pada threshold beku ini.
> Catatan: yolo:yolov8n: pada target recall 95% threshold yang terpilih meloloskan hampir semua frame (FPR 1.000). Ada frame berisi orang yang tidak mendapat skor di atas lantai pencatatan, jadi target itu hanya tercapai tanpa gate yang berguna. Bandingkan pada FPR yang disamakan, bukan pada threshold beku ini.
> Catatan: yolo:yolov8s: pada target recall 95% threshold yang terpilih meloloskan hampir semua frame (FPR 1.000). Ada frame berisi orang yang tidak mendapat skor di atas lantai pencatatan, jadi target itu hanya tercapai tanpa gate yang berguna. Bandingkan pada FPR yang disamakan, bukan pada threshold beku ini.

## Hasil di split test pada threshold beku

| Detektor | Threshold | Recall frame | Presisi | FPR | Recall video (gate) | AUC | AP | Invalid |
|---|---|---|---|---|---|---|---|---|
| llm:qwen3-vl:4b-instruct | 0.999719 | 0.992 [0.973–1.000] | 0.923 [0.831–0.992] | 0.185 [0.019–0.382] | 1.000 [1.000–1.000] | 0.967 | 0.980 | 0 |
| yolo:yolov8m | 0.000000 | 1.000 [1.000–1.000] | 0.691 [0.566–0.806] | 1.000 [1.000–1.000] | 1.000 [1.000–1.000] | 0.952 | 0.978 | 0 |
| yolo:yolov8n | 0.000000 | 1.000 [1.000–1.000] | 0.691 [0.566–0.806] | 1.000 [1.000–1.000] | 1.000 [1.000–1.000] | 0.921 | 0.966 | 0 |
| yolo:yolov8s | 0.000000 | 1.000 [1.000–1.000] | 0.691 [0.566–0.806] | 1.000 [1.000–1.000] | 1.000 [1.000–1.000] | 0.919 | 0.966 | 0 |

*Recall video (gate)* = dari video yang berisi orang, berapa persen yang lolos gate (minimal satu frame sampel melewati threshold). *Invalid* = jawaban model gagal/tidak valid; dihitung sebagai tidak terdeteksi.

## Selisih terhadap yolo:yolov8m (berpasangan, bootstrap sama)

| Detektor | Δ recall frame | Δ recall video | Δ FPR |
|---|---|---|---|
| llm:qwen3-vl:4b-instruct | -0.008 [-0.027–0.000] | 0.000 [0.000–0.000] | -0.815 [-0.981–-0.618] |
| yolo:yolov8n | 0.000 [0.000–0.000] | 0.000 [0.000–0.000] | 0.000 [0.000–0.000] |
| yolo:yolov8s | 0.000 [0.000–0.000] | 0.000 [0.000–0.000] | 0.000 [0.000–0.000] |

## Recall pada false-positive rate yang disamakan (test)

Tiap detektor mendapat threshold sendiri dari split calibration agar FPR-nya ≈ target, jadi perbandingan ini adil walau skala skornya berbeda.

| Target FPR | Detektor | Threshold | Recall frame | FPR aktual | Recall video (gate) | Δ recall vs yolo:yolov8m |
|---|---|---|---|---|---|---|
| 10% | llm:qwen3-vl:4b-instruct | 0.999998 | 0.967 [0.932–0.992] | 0.111 [0.000–0.300] | 0.967 [0.893–1.000] | 0.000 [-0.041–0.039] |
| 10% | yolo:yolov8m | 0.082155 | 0.967 [0.934–0.992] | 0.389 [0.190–0.585] | 1.000 [1.000–1.000] | (referensi) |
| 10% | yolo:yolov8n | 0.312745 | 0.777 [0.673–0.867] | 0.056 [0.000–0.129] | 0.967 [0.897–1.000] | -0.190 [-0.294–-0.103] |
| 10% | yolo:yolov8s | 0.204564 | 0.868 [0.809–0.922] | 0.204 [0.082–0.333] | 1.000 [1.000–1.000] | -0.099 [-0.154–-0.052] |
| 20% | llm:qwen3-vl:4b-instruct | 0.999978 | 0.983 [0.957–1.000] | 0.148 [0.017–0.339] | 1.000 [1.000–1.000] | 0.008 [-0.018–0.035] |
| 20% | yolo:yolov8m | 0.050099 | 0.975 [0.946–1.000] | 0.444 [0.233–0.643] | 1.000 [1.000–1.000] | (referensi) |
| 20% | yolo:yolov8n | 0.164922 | 0.868 [0.791–0.933] | 0.204 [0.050–0.389] | 1.000 [1.000–1.000] | -0.107 [-0.188–-0.039] |
| 20% | yolo:yolov8s | 0.079919 | 0.926 [0.884–0.964] | 0.463 [0.257–0.655] | 1.000 [1.000–1.000] | -0.050 [-0.087–-0.017] |

## Kecepatan

| Detektor | Median/frame | P95/frame | Frame/detik |
|---|---|---|---|
| llm:qwen3-vl:4b-instruct | 3.381 s | 30.144 s | 0.14 |
| yolo:yolov8m | 0.348 s | 0.369 s | 2.87 |
| yolo:yolov8n | 0.066 s | 0.079 s | 14.90 |
| yolo:yolov8s | 0.146 s | 0.170 s | 6.70 |

Latensi diukur di mesin dan server yang menjalankan `score`; ukur ulang di perangkat produksi.

## Kesimpulan otomatis

Kriteria: batas bawah interval 95% untuk Δ recall frame ≥ −0.02 (non-inferior terhadap yolo:yolov8m).

- **llm:qwen3-vl:4b-instruct**: pada threshold beku, kriteria recall **tidak terbukti terpenuhi** (tidak informatif: threshold beku referensi meloloskan hampir semua frame); 9.7x lebih lambat per frame.
  - Pada FPR ≈ 10%: recall **tidak berbeda nyata** dibanding yolo:yolov8m (Δ 0.000 [-0.041–0.039]).
  - Pada FPR ≈ 20%: recall **tidak berbeda nyata** dibanding yolo:yolov8m (Δ 0.008 [-0.018–0.035]).
  - Ini hanya satu sisi keputusan: pertimbangkan juga latensi pada perangkat nyata dan hilangnya estimasi kecepatan orang pada detektor LLM.
- **yolo:yolov8n**: pada threshold beku, kriteria recall **terpenuhi** (tidak informatif: threshold beku referensi meloloskan hampir semua frame); 5.3x lebih cepat per frame.
  - Pada FPR ≈ 10%: recall **lebih buruk** dibanding yolo:yolov8m (Δ -0.190 [-0.294–-0.103]).
  - Pada FPR ≈ 20%: recall **lebih buruk** dibanding yolo:yolov8m (Δ -0.107 [-0.188–-0.039]).
  - Ini hanya satu sisi keputusan: pertimbangkan juga latensi pada perangkat nyata dan hilangnya estimasi kecepatan orang pada detektor LLM.
- **yolo:yolov8s**: pada threshold beku, kriteria recall **terpenuhi** (tidak informatif: threshold beku referensi meloloskan hampir semua frame); 2.4x lebih cepat per frame.
  - Pada FPR ≈ 10%: recall **lebih buruk** dibanding yolo:yolov8m (Δ -0.099 [-0.154–-0.052]).
  - Pada FPR ≈ 20%: recall **lebih buruk** dibanding yolo:yolov8m (Δ -0.050 [-0.087–-0.017]).
  - Ini hanya satu sisi keputusan: pertimbangkan juga latensi pada perangkat nyata dan hilangnya estimasi kecepatan orang pada detektor LLM.

## Threshold untuk Model settings → Person threshold

| Detektor | Dipilih (recall ≥ target di calibration) | Alternatif: F1 tertinggi di calibration |
|---|---|---|
| llm:qwen3-vl:4b-instruct | 0.999719 | 0.999993 |
| yolo:yolov8m | 0.000000 | 0.082544 |
| yolo:yolov8n | 0.000000 | 0.080972 |
| yolo:yolov8s | 0.000000 | 0.138485 |

App memakai satu pengaturan *Person threshold* untuk kedua detektor; isi dengan nilai detektor yang dipilih.

## Pada threshold tetap (titik estimasi, test)

| Detektor | Threshold | Recall frame | Presisi | FPR | Recall video (gate) |
|---|---|---|---|---|---|
| llm:qwen3-vl:4b-instruct | 0.25 | 1.000 | 0.864 | 0.352 | 1.000 |
| yolo:yolov8m | 0.25 | 0.942 | 0.912 | 0.204 | 1.000 |
| yolo:yolov8n | 0.25 | 0.802 | 0.933 | 0.130 | 1.000 |
| yolo:yolov8s | 0.25 | 0.860 | 0.937 | 0.130 | 1.000 |
| llm:qwen3-vl:4b-instruct | 0.5 | 1.000 | 0.871 | 0.333 | 1.000 |
| yolo:yolov8m | 0.5 | 0.810 | 0.970 | 0.056 | 0.967 |
| yolo:yolov8n | 0.5 | 0.678 | 1.000 | 0.000 | 0.900 |
| yolo:yolov8s | 0.5 | 0.736 | 0.989 | 0.019 | 0.933 |
| llm:qwen3-vl:4b-instruct | 0.9 | 1.000 | 0.890 | 0.278 | 1.000 |
| yolo:yolov8m | 0.9 | 0.182 | 1.000 | 0.000 | 0.367 |
| yolo:yolov8n | 0.9 | 0.066 | 1.000 | 0.000 | 0.167 |
| yolo:yolov8s | 0.9 | 0.132 | 1.000 | 0.000 | 0.333 |
| llm:qwen3-vl:4b-instruct | 0.99 | 1.000 | 0.903 | 0.241 | 1.000 |
| yolo:yolov8m | 0.99 | 0.000 | n/a | 0.000 | 0.000 |
| yolo:yolov8n | 0.99 | 0.000 | n/a | 0.000 | 0.000 |
| yolo:yolov8s | 0.99 | 0.000 | n/a | 0.000 | 0.000 |
| llm:qwen3-vl:4b-instruct | 0.999 | 0.992 | 0.916 | 0.204 | 1.000 |
| yolo:yolov8m | 0.999 | 0.000 | n/a | 0.000 | 0.000 |
| yolo:yolov8n | 0.999 | 0.000 | n/a | 0.000 | 0.000 |
| yolo:yolov8s | 0.999 | 0.000 | n/a | 0.000 | 0.000 |
| llm:qwen3-vl:4b-instruct | 0.9999 | 0.983 | 0.930 | 0.167 | 1.000 |
| yolo:yolov8m | 0.9999 | 0.000 | n/a | 0.000 | 0.000 |
| yolo:yolov8n | 0.9999 | 0.000 | n/a | 0.000 | 0.000 |
| yolo:yolov8s | 0.9999 | 0.000 | n/a | 0.000 | 0.000 |

## Kesepakatan detektor (semua frame berlabel; yolo:yolov8m ≥ 0.25, detektor lain ≥ 0.5)

| Detektor | Frame | keduanya lolos | hanya detektor ini lolos | hanya yolo:yolov8m lolos | keduanya menolak |
|---|---|---|---|---|---|
| llm:qwen3-vl:4b-instruct | ada orang | 166 | 21 | 0 | 2 |
| llm:qwen3-vl:4b-instruct | kosong | 8 | 25 | 4 | 54 |
| yolo:yolov8n | ada orang | 120 | 1 | 46 | 22 |
| yolo:yolov8n | kosong | 1 | 0 | 11 | 79 |
| yolo:yolov8s | ada orang | 135 | 2 | 31 | 21 |
| yolo:yolov8s | kosong | 1 | 0 | 11 | 79 |

Daftar frame yang tidak sepakat ada di `disagreements_*.csv`. Periksa frame *ada orang* yang hanya lolos di satu detektor: apakah orangnya benar-benar ada atau label perlu dikoreksi.

## Batasan

- Label dibuat manual pada frame 640 px; kualitas kesimpulan mengikuti kualitas dan jumlah label.
- Data publik (mis. UCF-Crime) tidak mewakili CCTV kantor. Ulangi `analyze` dengan sampel video kantor sebelum threshold dipakai.
- Skor LLM bergantung pada model, prompt, kuantisasi dan server; ganti salah satunya berarti kalibrasi ulang.
- Model `qwen*` di port 11434 dipanggil app lewat API native Ollama; skrip ini memakai endpoint `/v1`, jadi skor bisa sedikit berbeda dari di app.
