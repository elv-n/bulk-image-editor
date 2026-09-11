using System;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ImageResizerCSharp.Models;

namespace ImageResizerCSharp.Core
{
    public enum PhotoBackgroundType
    {
        None,         // Tidak diubah (asli)
        Red,          // Merah Pasfoto Indonesia (#D81B1B / Ganjil)
        Blue,         // Biru Pasfoto Indonesia (#0066CC / Genap)
        White,        // Putih Formal (#FFFFFF / Visa & Paspor)
        Transparent,  // Transparan (PNG Cutout)
        Custom        // Kustom (Hex / Color Picker)
    }

    public static class BackgroundMattingEngine
    {
        private static readonly object LockObj = new();
        private static InferenceSession? _session;
        private static string? _inputName;
        private static string? _outputName;
        private static bool _isInitialized = false;

        public const int ModelInputSize = 512;

        // Standar Kode Warna Pasfoto Indonesia
        public static readonly Rgba32 ColorPasfotoRed = new(216, 27, 27, 255);    // #D81B1B (Tahun Ganjil)
        public static readonly Rgba32 ColorPasfotoBlue = new(0, 102, 204, 255);   // #0066CC (Tahun Genap)
        public static readonly Rgba32 ColorFormalWhite = new(255, 255, 255, 255); // #FFFFFF (Visa / Dokumen)

        public static bool IsModelAvailable()
        {
            return File.Exists(GetModelPath());
        }

        public static string GetModelPath()
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var pathInBase = Path.Combine(baseDir, "modnet_v2.onnx");
            if (File.Exists(pathInBase)) return pathInBase;

            var pathInParent = Path.Combine(baseDir, "..", "..", "..", "modnet_v2.onnx");
            if (File.Exists(pathInParent)) return Path.GetFullPath(pathInParent);

            var pathRelative = "modnet_v2.onnx";
            if (File.Exists(pathRelative)) return Path.GetFullPath(pathRelative);

            return pathInBase;
        }

        public static void Initialize()
        {
            if (_isInitialized && _session != null) return;

            lock (LockObj)
            {
                if (_isInitialized && _session != null) return;

                string modelPath = GetModelPath();
                if (!File.Exists(modelPath))
                {
                    throw new FileNotFoundException($"Model file MODNet '{modelPath}' tidak ditemukan.");
                }

                var sessionOptions = new SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                    IntraOpNumThreads = Math.Max(1, Math.Min(Environment.ProcessorCount, 4))
                };

                _session = new InferenceSession(modelPath, sessionOptions);
                _inputName = _session.InputNames.FirstOrDefault() ?? "input";
                _outputName = _session.OutputNames.FirstOrDefault() ?? "output";
                _isInitialized = true;
            }
        }

        public static Rgba32 ParseHexColor(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return ColorPasfotoRed;

            hex = hex.Trim().TrimStart('#');
            if (hex.Length == 6)
            {
                byte r = Convert.ToByte(hex.Substring(0, 2), 16);
                byte g = Convert.ToByte(hex.Substring(2, 2), 16);
                byte b = Convert.ToByte(hex.Substring(4, 2), 16);
                return new Rgba32(r, g, b, 255);
            }
            if (hex.Length == 8)
            {
                byte a = Convert.ToByte(hex.Substring(0, 2), 16);
                byte r = Convert.ToByte(hex.Substring(2, 2), 16);
                byte g = Convert.ToByte(hex.Substring(4, 2), 16);
                byte b = Convert.ToByte(hex.Substring(6, 2), 16);
                return new Rgba32(r, g, b, a);
            }

            return ColorPasfotoRed;
        }

        public static Rgba32 GetTargetColor(PhotoBackgroundType bgType, string customHex = "")
        {
            return bgType switch
            {
                PhotoBackgroundType.Red => ColorPasfotoRed,
                PhotoBackgroundType.Blue => ColorPasfotoBlue,
                PhotoBackgroundType.White => ColorFormalWhite,
                PhotoBackgroundType.Custom => ParseHexColor(customHex),
                _ => ColorPasfotoRed
            };
        }

        /// <summary>
        /// Mengekstrak alpha matte (masker subjek) dari gambar menggunakan MODNet ONNX.
        /// Nilai alpha bernilai 0.0 (background) hingga 1.0 (subjek).
        /// </summary>
        public static Image<L8> GenerateAlphaMatte(Image<Rgba32> sourceImage)
        {
            Initialize();

            // 1. Resize gambar sumber ke 512x512 untuk inferensi MODNet
            using var resized512 = sourceImage.Clone(ctx => ctx.Resize(new ResizeOptions
            {
                Size = new SixLabors.ImageSharp.Size(ModelInputSize, ModelInputSize),
                Mode = ResizeMode.Stretch,
                Sampler = KnownResamplers.Bicubic
            }));

            // 2. Buat tensor [1, 3, 512, 512] dengan normalisasi (pixel / 255.0 - 0.5) / 0.5 = (pixel - 127.5) / 127.5
            var tensor = new DenseTensor<float>(new[] { 1, 3, ModelInputSize, ModelInputSize });

            resized512.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 0; x < row.Length; x++)
                    {
                        var pixel = row[x];
                        tensor[0, 0, y, x] = (pixel.R - 127.5f) / 127.5f;
                        tensor[0, 1, y, x] = (pixel.G - 127.5f) / 127.5f;
                        tensor[0, 2, y, x] = (pixel.B - 127.5f) / 127.5f;
                    }
                }
            });

            // 3. Eksekusi model ONNX
            var inputs = new[] { NamedOnnxValue.CreateFromTensor(_inputName!, tensor) };

            IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results;
            lock (LockObj)
            {
                results = _session!.Run(inputs);
            }

            using (results)
            {
                var outputTensor = results.First().AsTensor<float>();

                // 4. Bangun Image<L8> 512x512 dari output tensor
                using var matte512 = new Image<L8>(ModelInputSize, ModelInputSize);
                matte512.ProcessPixelRows(accessor =>
                {
                    for (int y = 0; y < accessor.Height; y++)
                    {
                        var row = accessor.GetRowSpan(y);
                        for (int x = 0; x < row.Length; x++)
                        {
                            float alpha = Math.Clamp(outputTensor[0, 0, y, x], 0.0f, 1.0f);
                            row[x] = new L8((byte)Math.Round(alpha * 255.0f));
                        }
                    }
                });

                // 5. Skalakan alpha matte kembali ke resolusi asli gambar sumber menggunakan Bilinear interpolation
                var fullMatte = matte512.Clone(ctx => ctx.Resize(new ResizeOptions
                {
                    Size = new SixLabors.ImageSharp.Size(sourceImage.Width, sourceImage.Height),
                    Mode = ResizeMode.Stretch,
                    Sampler = KnownResamplers.Bicubic
                }));

                return fullMatte;
            }
        }

        private static readonly object _previewCacheLock = new();
        private static string? _cachedPreviewPath;
        private static int _cachedPreviewRotation = 0;
        private static Image<Rgba32>? _cachedPreviewSource;
        private static Image<L8>? _cachedPreviewMatte;

        public static void ClearPreviewCache()
        {
            lock (_previewCacheLock)
            {
                _cachedPreviewPath = null;
                _cachedPreviewRotation = 0;
                _cachedPreviewSource?.Dispose();
                _cachedPreviewSource = null;
                _cachedPreviewMatte?.Dispose();
                _cachedPreviewMatte = null;
            }
        }

        /// <summary>
        /// Mengganti background gambar dengan warna pasfoto target atau transparan.
        /// Mengembalikan gambar baru bertipe Image&lt;Rgba32&gt;.
        /// </summary>
        public static Image<Rgba32> ReplaceBackground(
            Image<Rgba32> sourceImage,
            PhotoBackgroundType bgType,
            string customHex = "")
        {
            if (bgType == PhotoBackgroundType.None)
            {
                return sourceImage.Clone();
            }

            using var alphaMatte = GenerateAlphaMatte(sourceImage);
            return CompositeImageWithMatte(sourceImage, alphaMatte, bgType, customHex);
        }

        /// <summary>
        /// Melakukan alpha blending antara subjek foto dan latar belakang target menggunakan alpha matte.
        /// </summary>
        public static Image<Rgba32> CompositeImageWithMatte(
            Image<Rgba32> sourceImage,
            Image<L8> alphaMatte,
            PhotoBackgroundType bgType,
            string customHex = "")
        {
            var resultImage = new Image<Rgba32>(sourceImage.Width, sourceImage.Height);

            bool isTransparent = bgType == PhotoBackgroundType.Transparent;
            Rgba32 targetBg = GetTargetColor(bgType, customHex);

            sourceImage.ProcessPixelRows(alphaMatte, resultImage, (srcAccessor, matteAccessor, dstAccessor) =>
            {
                for (int y = 0; y < srcAccessor.Height; y++)
                {
                    var srcRow = srcAccessor.GetRowSpan(y);
                    var matteRow = matteAccessor.GetRowSpan(y);
                    var dstRow = dstAccessor.GetRowSpan(y);

                    for (int x = 0; x < srcRow.Length; x++)
                    {
                        var fg = srcRow[x];
                        byte alphaByte = matteRow[x].PackedValue;
                        float alpha = alphaByte / 255.0f;

                        if (isTransparent)
                        {
                            // Pertahankan warna foreground dengan transparansi berdasarkan alpha
                            dstRow[x] = new Rgba32(fg.R, fg.G, fg.B, alphaByte);
                        }
                        else
                        {
                            // Alpha compositing dengan warna background pasfoto
                            float invAlpha = 1.0f - alpha;
                            byte r = (byte)Math.Clamp(Math.Round(fg.R * alpha + targetBg.R * invAlpha), 0, 255);
                            byte g = (byte)Math.Clamp(Math.Round(fg.G * alpha + targetBg.G * invAlpha), 0, 255);
                            byte b = (byte)Math.Clamp(Math.Round(fg.B * alpha + targetBg.B * invAlpha), 0, 255);

                            dstRow[x] = new Rgba32(r, g, b, 255);
                        }
                    }
                }
            });

            return resultImage;
        }

        /// <summary>
        /// Menghasilkan BitmapSource WPF untuk live preview langsung pada UI dengan caching matte.
        /// Pergantian warna berikutnya pada foto yang sama menjadi instan (~2ms).
        /// </summary>
        public static BitmapSource GeneratePreviewBitmap(
            string imagePath,
            PhotoBackgroundType bgType,
            string customHex = "",
            int maxPreviewDimension = 720,
            string photoPresetKey = "Original",
            int customWidth = 600,
            int customHeight = 800,
            NormalizedCropRect? customCrop = null,
            int rotationAngle = 0)
        {
            lock (_previewCacheLock)
            {
                int normAngle = ((rotationAngle % 360) + 360) % 360;
                if (_cachedPreviewPath != imagePath || _cachedPreviewRotation != normAngle || _cachedPreviewSource == null)
                {
                    _cachedPreviewSource?.Dispose();
                    _cachedPreviewMatte?.Dispose();
                    _cachedPreviewSource = null;
                    _cachedPreviewMatte = null;

                    var source = SixLabors.ImageSharp.Image.Load<Rgba32>(imagePath);
                    source.Mutate(x => x.AutoOrient());

                    if (normAngle == 90) source.Mutate(x => x.Rotate(RotateMode.Rotate90));
                    else if (normAngle == 180) source.Mutate(x => x.Rotate(RotateMode.Rotate180));
                    else if (normAngle == 270) source.Mutate(x => x.Rotate(RotateMode.Rotate270));

                    if (source.Width > maxPreviewDimension || source.Height > maxPreviewDimension)
                    {
                        source.Mutate(x => x.Resize(new ResizeOptions
                        {
                            Size = new SixLabors.ImageSharp.Size(maxPreviewDimension, maxPreviewDimension),
                            Mode = ResizeMode.Max
                        }));
                    }

                    _cachedPreviewSource = source;
                    _cachedPreviewPath = imagePath;
                    _cachedPreviewRotation = normAngle;
                }

                Image<Rgba32> workingImage;
                if (bgType == PhotoBackgroundType.None)
                {
                    workingImage = _cachedPreviewSource.Clone();
                }
                else
                {
                    if (_cachedPreviewMatte == null)
                    {
                        _cachedPreviewMatte = GenerateAlphaMatte(_cachedPreviewSource);
                    }
                    workingImage = CompositeImageWithMatte(_cachedPreviewSource, _cachedPreviewMatte, bgType, customHex);
                }

                // Live crop: prioritaskan crop manual jika ada, jika tidak gunakan preset center crop
                if (customCrop != null)
                {
                    var cropRect = customCrop.ToImageSharpRect(workingImage.Width, workingImage.Height);
                    workingImage.Mutate(x => x.Crop(cropRect));
                }
                else
                {
                    var presetDims = ImageResizerEngine.GetPresetDimensions(photoPresetKey, customWidth, customHeight);
                    if (presetDims.HasValue)
                    {
                        ImageResizerEngine.ApplyCenterCrop(workingImage, presetDims.Value);
                    }
                }

                using var ms = new MemoryStream();
                workingImage.SaveAsPng(ms);
                workingImage.Dispose();
                ms.Position = 0;

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = ms;
                bitmap.EndInit();
                bitmap.Freeze();

                return bitmap;
            }
        }
    }
}
