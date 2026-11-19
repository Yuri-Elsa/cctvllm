# Rollout di kantor: integrasi CCTV, evaluasi, dan kalibrasi threshold

Mencakup dua future work handoff: integrasi dengan CCTV kantor + evaluasi dengan data aktual, dan kalibrasi threshold dari sampel rekaman asli. Fitur aplikasinya sudah ada (RTSP, folder watcher, halaman Calibration); yang dibutuhkan adalah rekaman dan label dari kantor.

## 1. Hubungkan kamera

1. Buat akun kamera **read-only**. URL RTSP terlihat di command line ffmpeg bagi pengguna lain di mesin yang sama.
2. Siapkan `CCTV_UI_PASSWORD` (atau `users.json`) bila aplikasi bisa dijangkau dari jaringan; lihat README, bagian _Security and access_ dan _Users and roles_.
3. Tambahkan kamera sesuai README, bagian _Live cameras (RTSP)_. Jalankan **Settings → System check** sampai tidak ada kegagalan.
4. Biarkan **Face blur** aktif. Rekaman kantor memuat karyawan; baca `PRIVACY.md` dan sesuaikan retention.
5. Biarkan berjalan beberapa hari. Mode live hanya menyimpan klip saat ada orang, jadi aplikasi mengumpulkan sampelnya sendiri.

## 2. Kumpulkan dan beri label sampel

Target minimum: **10 kejadian dan 30 klip normal**, makin banyak makin baik. Di bawah itu interval kepercayaan terlalu lebar (dengan 10 kejadian, menemukan 8 cocok dengan recall sebenarnya antara kira-kira 44% dan 97%).

Label berdasarkan apa yang dilihat orang, bukan jawaban aplikasi. Klip normal harus diambil acak dari yang tidak ditandai, bukan hanya yang ditandai.

Dua jalur, pilih salah satu:

**A. Lewat aplikasi (paling mudah).** Tekan _Confirmed / False alarm_ pada video yang ditandai, dan _Missed event / Normal_ pada yang tidak. Halaman **Calibration** langsung memakainya (langkah 3).

**B. Lewat folder klip, untuk evaluasi offline yang bisa diulang.**

```bash
python eval/office_manifest.py --video-root D:/kantor --write-template eval/office/labels.csv
# isi kolom expected_suspicious (1 atau 0); tanda person_optional=1 bila kejadiannya tidak melibatkan orang
python eval/office_manifest.py --video-root D:/kantor --labels eval/office/labels.csv --out eval/office/manifest.csv
```

Jalan pintas bila reviewer sudah menekan verdict di aplikasi: pakai tombol export CSV, lalu `python eval/office_manifest.py --from-export cctv-jobs-XXXX.csv --write-template eval/office/labels.csv`. Confirmed dan Missed event menjadi 1, False alarm dan Normal menjadi 0; klip dicocokkan lewat nama file, jadi nama harus unik. Catatan: reviewer melihat flag model saat menilai, sehingga label cenderung condong ke flag itu. Untuk evaluasi yang bersih, minta orang lain menilai ulang tanpa melihat flag, minimal pada sampel acak.

Skrip membagi klip menjadi calibration dan test secara berstrata (kejadian dan normal dibagi terpisah) dengan seed tetap. **Jangan membagi ulang setelah melihat hasil.**

## 3. Kalibrasi threshold suspicious

- Dari aplikasi: buka **Calibration**, pilih _Best balance_ / _Catch almost everything_ / _Few false alarms_, tekan **Use**, lalu **Save** di Model settings.
- Dari klip offline:

```bash
python eval/run_app_eval.py --manifest eval/office/manifest.csv --video-root D:/kantor \
    --settings eval/settings.json --out eval/runs/office_v1
python eval/analyze_app_eval.py --results eval/runs/office_v1/results.jsonl \
    --manifest eval/office/manifest.csv --split calibration,test --out eval/runs/office_v1/report
```

Threshold dipilih di split calibration (recall target 0,90), dibekukan, lalu dilaporkan di test. Laporkan dengan hitungannya ("8 dari 10").

Kalibrasi hanya berlaku untuk satu model, prompt, dan sudut kamera. Ulangi setelah mengganti salah satunya.

## 4. Evaluasi detektor manusia di kantor

Lihat aturan keputusan di `PERSON-DETECTOR-DECISION.md`.

```bash
python eval/person_detection_eval.py sample --manifest eval/office/manifest.csv --video-root D:/kantor --out eval/runs/office_person
python eval/person_detection_eval.py label --labels eval/runs/office_person/labels.csv
python eval/person_detection_eval.py score --labels eval/runs/office_person/labels.csv --out eval/runs/office_person/scores.jsonl --detectors yolo,llm --model qwen3-vl:4b-instruct
python eval/person_detection_eval.py analyze --labels eval/runs/office_person/labels.csv --scores eval/runs/office_person/scores.jsonl --out eval/runs/office_person/report
python eval/person_detection_eval.py cascade --labels eval/runs/office_person/labels.csv --scores eval/runs/office_person/scores.jsonl --out eval/runs/office_person/cascade --person-share 0.2
```

Sesuaikan `--person-share` dengan perkiraan proporsi frame berisi orang di kantor, dan jalankan `score` di mesin produksi agar latensinya nyata.

## Checklist selesai

- [ ] Kamera kantor terhubung, System check bersih
- [ ] ≥ 10 kejadian dan ≥ 30 klip normal berlabel
- [ ] Threshold suspicious dikalibrasi dari rekaman kantor dan disimpan
- [ ] Evaluasi detektor manusia di kantor selesai; keputusan dicatat di `PERSON-DETECTOR-DECISION.md`