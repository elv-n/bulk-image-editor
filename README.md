# Bulk Image Editor

## Fitur Utama

- **AI Background Replacement (MODNet Fine-Tuned Pasfoto Indonesia):**
  - Menggunakan model ONNX deep learning MODNet yang telah di-fine-tune khusus untuk pasfoto Indonesia (`didik-maulana/pas-photo-matting`).
  - **Bebas artefak grey halo:** Mempertahankan helai rambut dan lekukan kepala secara natural tanpa bayangan abu-abu buram.
  - **Deteksi presisi:** Tidak melubangi objek tipis seperti **tali hijab/kerudung, lanyard/tali ID card, kalung, kancing, atau dasi**.
  - **Preset Warna Standar Pasfoto Indonesia:**
    - 🔴 **Merah Pasfoto (Tahun Ganjil):** `#D81B1B` (Standar KTP, SKCK, Ijazah, CPNS).
    - 🔵 **Biru Pasfoto (Tahun Genap):** `#0066CC` (Standar KTP, SKCK, Ijazah, CPNS).
    - ◽ **Putih Formal (Visa / Paspor):** `#FFFFFF` (Standar kedutaan & paspor resmi).
    - 🔳 **Transparan (Cutout PNG):** Menghapus background tanpa warna pengganti.
    - 🎨 **Kustom Hex:** Bebas memilih warna sesuai kebutuhan.
  - **Live AI Preview:** Pengguna dapat menguji dan melihat langsung hasil potongan latar belakang foto sebelum memulai proses batch.
- **Multi-Core Parallel Batch Processing:** Pemrosesan gambar secara simultan memanfaatkan seluruh core CPU menggunakan `Parallel.ForEachAsync` untuk kecepatan maksimal.
- **Smart Target Size Compression:** Mengoptimalkan resolusi dan kualitas kompresi (Quality 90 - 97) secara otomatis agar ukuran file mendekati batas target yang ditentukan (misalnya menghasilkan 480 KB - 498 KB untuk target 500 KB) tanpa mengurangi ketajaman visual.
- **Preset Pas Foto Instan:** Pilihan ukuran rasio standar pas foto:
  - `2 × 3 cm` (255 × 354 px @ 300 DPI)
  - `3 × 4 cm` (354 × 472 px @ 300 DPI)
  - `4 × 6 cm` (472 × 709 px @ 300 DPI)
  - `1 : 1` (Persegi 800 × 800 px)
  - `Kustom` (Tentukan dimensi lebar dan tinggi piksel sendiri)
- **Auto Center-Crop:** Mempertahankan rasio pas foto secara presisi dengan memotong bagian tengah foto tanpa mendistorsi wajah.
- **Dukungan Format Luas:**
  - Input: `JPEG`, `JPG`, `PNG`, `WEBP`, `BMP`, `TIFF`.
  - Output: `JPEG`, `PNG`, `WEBP`.
- **Antarmuka Modern (Fluent UI):**
  - Desain bersih dan elegan dengan palet warna modern.
  - Micro-thumbnail 38×38 px yang dimuat di background thread.
  - Indikator animasi vektor GPU Loading Spinner saat memuat foto dan saat proses kompresi.
  - Tampilan responsif dan bebas terpotong pada penskalaan layar Windows High-DPI (125%, 150%).
- **Standalone Portable Executable (.exe):** Dapat dipublikasikan sebagai file `.exe` tunggal mandiri tanpa mengharuskan pengguna akhir menginstal .NET 8 Runtime, Python, maupun CUDA.

---

## Spesifikasi Teknis

| Komponen                    | Spesifikasi                                                                                                    |
| --------------------------- | -------------------------------------------------------------------------------------------------------------- |
| **Bahasa Pemrograman**      | C# 12                                                                                                          |
| **Target Runtime**          | .NET 8.0 Windows Desktop (`net8.0-windows`)                                                                    |
| **Framework UI**            | Windows Presentation Foundation (WPF)                                                                          |
| **AI Inference Runtime**    | [Microsoft.ML.OnnxRuntime v1.20.1](https://github.com/microsoft/onnxruntime) (CPU Optimized)                  |
| **Model AI Matting**        | [MODNet Fine-Tuned Pasfoto Indonesia](https://github.com/didik-maulana/pas-photo-matting) (`modnet_v2.onnx`)    |
| **Library Pengolah Gambar** | [SixLabors.ImageSharp v3.1.12](https://github.com/SixLabors/ImageSharp) (Open-Source, bebas lisensi komersial) |
| **Target Platform**         | Windows 10 / 11 (64-bit / x64)                                                                                 |
| **Arsitektur Pemrosesan**   | Asynchronous Task-based Multi-threading                                                                        |

---

## Struktur Direktori

```text
ImageResizer/
├── ImageResizer.sln              # Visual Studio Solution File
├── build_exe.bat                 # Script batch otomatis untuk build .exe
├── .gitignore                    # Konfigurasi pengecualian git
├── README.md                     # Dokumentasi spesifikasi project
├── .github/
│   └── workflows/
│       └── release.yml           # CI/CD GitHub Actions untuk rilis otomatis .exe
└── ImageResizerCSharp/           # Source code utama C# WPF
    ├── ImageResizerCSharp.csproj # File project .NET 8
    ├── modnet_v2.onnx            # Bobot model AI MODNet fine-tuned pasfoto Indonesia (~24.8 MB)
    ├── App.xaml / App.xaml.cs    # Global styles, palette, dan entry point
    ├── MainWindow.xaml (.cs)     # Antarmuka utama dan logika interaksi
    ├── Core/
    │   ├── BackgroundMattingEngine.cs # Engine inferensi MODNet & compositing warna background
    │   └── ImageResizerEngine.cs      # Engine kompresi, crop, dan resize paralel
    ├── Controls/
    │   └── LoadingSpinner.xaml   # Kontrol animasi loading spinner berbasis vektor
    ├── Models/
    │   └── ImageItem.cs          # Model data item gambar dan metadata
    └── Tests/
        └── TestRunner.cs         # Automated verification tests (Resizing, AI Matting & Compositing)
```

---

## Cara Menjalankan & Mengembangkan

### 1. Prasyarat

- **Windows 10 / 11 (64-bit)**
- **.NET 8.0 SDK** (atau Visual Studio 2022 versi 17.8 ke atas dengan workload _.NET Desktop Development_)

### 2. Membuka di Visual Studio

1. Buka file `ImageResizer.sln`.
2. Tekan **F5** atau klik tombol **Start** untuk menjalankan aplikasi.

### 3. Menjalankan via Terminal / CLI

```powershell
dotnet run --project ImageResizerCSharp/ImageResizerCSharp.csproj
```

### 4. Menjalankan Automated Verification Tests

Aplikasi dilengkapi pengujian otomatis bawaan untuk memvalidasi fungsi baca dimensi, micro-thumbnail, crop pas foto 3x4, kompresi target ukuran, dan pemrosesan paralel:

```powershell
dotnet run --project ImageResizerCSharp/ImageResizerCSharp.csproj -- --test
```

---

## Cara Membuat File .EXE Mandiri (Release)

### Cara Cepat

Klik dua kali file `build_exe.bat`.

### Cara Terminal

```powershell
dotnet publish ImageResizerCSharp/ImageResizerCSharp.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o ./publish
```

File executable mandiri akan tersedia di:
`publish/ImageResizerCSharp.exe` (Dapat langsung dipindahkan dan dijalankan di komputer Windows manapun).

---
