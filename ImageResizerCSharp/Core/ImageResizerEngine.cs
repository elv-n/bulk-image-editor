using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;
using ImageResizerCSharp.Models;

namespace ImageResizerCSharp.Core
{
    public record ResizeResult(
        string Status,
        long OriginalSize,
        long NewSize,
        int NewWidth,
        int NewHeight,
        string OutputPath
    );

    public record ResizeProgressReport(
        ImageItem Item,
        int CurrentIndex,
        int TotalCount,
        ResizeResult? Result,
        Exception? Error
    );

    public static class ImageResizerEngine
    {
        public static (int Width, int Height)? GetPresetDimensions(string presetKey, int customW = 600, int customH = 800)
        {
            return presetKey switch
            {
                "2x3" => (255, 354),
                "3x4" => (354, 472),
                "4x6" => (472, 709),
                "1x1" => (800, 800),
                "Custom" => (Math.Max(50, customW), Math.Max(50, customH)),
                _ => null
            };
        }

        public static (int Width, int Height) ReadImageDimensions(string filePath)
        {
            using var image = Image.Load(filePath);
            return (image.Width, image.Height);
        }

        public static BitmapSource? GenerateMicroThumbnail(string filePath, int targetSize = 38)
        {
            try
            {
                using var image = Image.Load(filePath);
                image.Mutate(x => x.AutoOrient().Resize(new ResizeOptions
                {
                    Size = new SixLabors.ImageSharp.Size(targetSize, targetSize),
                    Mode = ResizeMode.Crop,
                    Position = AnchorPositionMode.Center
                }));

                using var ms = new MemoryStream();
                image.SaveAsPng(ms);
                ms.Position = 0;

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = ms;
                bitmap.EndInit();
                bitmap.Freeze(); // Crucial for cross-thread access in WPF
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        public static ResizeResult ResizeImageToTarget(
            string inputPath,
            string outputPath,
            long maxSizeBytes,
            string outputFormat = "JPEG",
            string photoPresetKey = "Original",
            int customWidth = 600,
            int customHeight = 800,
            bool maximizeQuality = true)
        {
            var originalFileInfo = new FileInfo(inputPath);
            long originalSize = originalFileInfo.Length;

            using var image = Image.Load(inputPath);
            image.Mutate(x => x.AutoOrient());

            var presetDims = GetPresetDimensions(photoPresetKey, customWidth, customHeight);

            // If format unchanged, preset is original, and already within target size: skip with copy
            bool isSameFormat = string.Equals(Path.GetExtension(inputPath), GetFormatExtension(outputFormat), StringComparison.OrdinalIgnoreCase);
            if (photoPresetKey == "Original" && isSameFormat && originalSize <= maxSizeBytes)
            {
                if (!string.Equals(inputPath, outputPath, StringComparison.OrdinalIgnoreCase))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                    File.Copy(inputPath, outputPath, true);
                }

                return new ResizeResult(
                    Status: "skipped",
                    OriginalSize: originalSize,
                    NewSize: originalSize,
                    NewWidth: image.Width,
                    NewHeight: image.Height,
                    OutputPath: outputPath
                );
            }

            byte[] outputBytes;

            if (presetDims.HasValue)
            {
                var (targetW, targetH) = presetDims.Value;
                double targetRatio = (double)targetW / targetH;
                double currentRatio = (double)image.Width / image.Height;

                int cropW = image.Width;
                int cropH = image.Height;

                if (currentRatio > targetRatio)
                {
                    cropW = Math.Max(10, (int)Math.Round(image.Height * targetRatio));
                }
                else if (currentRatio < targetRatio)
                {
                    cropH = Math.Max(10, (int)Math.Round(image.Width / targetRatio));
                }

                // Center crop to target aspect ratio at maximum source resolution
                image.Mutate(x => x.Crop(new Rectangle(
                    Math.Max(0, (image.Width - cropW) / 2),
                    Math.Max(0, (image.Height - cropH) / 2),
                    Math.Min(cropW, image.Width),
                    Math.Min(cropH, image.Height)
                )));

                if (maximizeQuality)
                {
                    outputBytes = CompressAndScaleToTarget(image, maxSizeBytes, outputFormat, targetW);
                }
                else
                {
                    image.Mutate(x => x.Resize(targetW, targetH));
                    outputBytes = CompressToTarget(image, maxSizeBytes, outputFormat);
                }
            }
            else
            {
                // Original preset
                if (maximizeQuality && originalSize > maxSizeBytes)
                {
                    outputBytes = CompressAndScaleToTarget(image, maxSizeBytes, outputFormat, Math.Min(300, image.Width));
                }
                else
                {
                    outputBytes = CompressToTarget(image, maxSizeBytes, outputFormat);
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllBytes(outputPath, outputBytes);

            return new ResizeResult(
                Status: "done",
                OriginalSize: originalSize,
                NewSize: outputBytes.Length,
                NewWidth: image.Width,
                NewHeight: image.Height,
                OutputPath: outputPath
            );
        }

        private static byte[] CompressAndScaleToTarget(
            Image image,
            long maxSizeBytes,
            string outputFormat,
            int minAcceptableWidth = 300)
        {
            double ratio = (double)image.Height / image.Width;
            bool isPng = outputFormat.Equals("PNG", StringComparison.OrdinalIgnoreCase);

            // 1. If at current resolution with high quality (92) it already fits, try higher quality up to 98
            if (!isPng)
            {
                byte[] fullAttempt = EncodeImage(image, outputFormat, 92);
                if (fullAttempt.Length <= maxSizeBytes)
                {
                    int lowQ = 93;
                    int highQ = 98;
                    byte[] bestCandidate = fullAttempt;
                    while (lowQ <= highQ)
                    {
                        int midQ = (lowQ + highQ) / 2;
                        byte[] candidate = EncodeImage(image, outputFormat, midQ);
                        if (candidate.Length <= maxSizeBytes)
                        {
                            bestCandidate = candidate;
                            lowQ = midQ + 1;
                        }
                        else
                        {
                            highQ = midQ - 1;
                        }
                    }
                    return bestCandidate;
                }
            }

            // 2. Binary search resolution to find the largest resolution that fits at base quality
            int lowW = Math.Max(50, Math.Min(minAcceptableWidth, image.Width));
            int highW = image.Width;
            int bestW = lowW;
            byte[]? bestBytes = null;
            int baseQuality = 90;

            while (lowW <= highW)
            {
                int midW = (lowW + highW) / 2;
                int midH = Math.Max(50, (int)Math.Round(midW * ratio));

                using var resized = image.Clone(ctx => ctx.Resize(midW, midH));
                byte[] candidate = EncodeImage(resized, outputFormat, baseQuality);

                if (candidate.Length <= maxSizeBytes)
                {
                    bestW = midW;
                    bestBytes = candidate;
                    lowW = midW + 6;
                }
                else
                {
                    highW = midW - 6;
                }
            }

            // Resize image to the best found resolution
            int finalH = Math.Max(50, (int)Math.Round(bestW * ratio));
            image.Mutate(ctx => ctx.Resize(bestW, finalH));

            if (bestBytes == null)
            {
                return CompressToTarget(image, maxSizeBytes, outputFormat);
            }

            // 3. For JPEG/WEBP, fine-tune quality between 88 and 97 to approach maxSizeBytes closely
            if (!isPng)
            {
                int fineLowQ = 88;
                int fineHighQ = 97;

                while (fineLowQ <= fineHighQ)
                {
                    int midQ = (fineLowQ + fineHighQ) / 2;
                    byte[] candidate = EncodeImage(image, outputFormat, midQ);

                    if (candidate.Length <= maxSizeBytes)
                    {
                        bestBytes = candidate;
                        fineLowQ = midQ + 1;
                    }
                    else
                    {
                        fineHighQ = midQ - 1;
                    }
                }
            }

            return bestBytes;
        }

        private static byte[] CompressToTarget(Image image, long maxSizeBytes, string outputFormat)
        {
            int currentW = image.Width;
            int currentH = image.Height;

            int maxAttempts = 6;
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                if (outputFormat.Equals("JPEG", StringComparison.OrdinalIgnoreCase) ||
                    outputFormat.Equals("WEBP", StringComparison.OrdinalIgnoreCase))
                {
                    int lowQ = 15;
                    int highQ = 95;
                    byte[]? bestBytes = null;

                    // Binary search quality
                    while (lowQ <= highQ)
                    {
                        int midQ = (lowQ + highQ) / 2;
                        byte[] candidate = EncodeImage(image, outputFormat, midQ);

                        if (candidate.Length <= maxSizeBytes)
                        {
                            bestBytes = candidate;
                            lowQ = midQ + 1; // Try higher quality
                        }
                        else
                        {
                            highQ = midQ - 1; // Need lower quality
                        }
                    }

                    if (bestBytes != null)
                    {
                        return bestBytes;
                    }
                }
                else // PNG (lossless)
                {
                    byte[] candidate = EncodeImage(image, outputFormat, 90);
                    if (candidate.Length <= maxSizeBytes)
                    {
                        return candidate;
                    }
                }

                // If still exceeds target size, downscale dimensions by 15% and retry
                currentW = Math.Max(50, (int)(currentW * 0.85));
                currentH = Math.Max(50, (int)(currentH * 0.85));

                image.Mutate(x => x.Resize(currentW, currentH));
            }

            // Return best effort at low quality if still over
            return EncodeImage(image, outputFormat, 20);
        }

        private static byte[] EncodeImage(Image image, string format, int quality)
        {
            using var ms = new MemoryStream();
            switch (format.ToUpperInvariant())
            {
                case "PNG":
                    image.Save(ms, new PngEncoder());
                    break;
                case "WEBP":
                    image.Save(ms, new WebpEncoder { Quality = quality });
                    break;
                case "JPEG":
                default:
                    image.Save(ms, new JpegEncoder { Quality = quality });
                    break;
            }
            return ms.ToArray();
        }

        public static string GetFormatExtension(string format)
        {
            return format.ToUpperInvariant() switch
            {
                "PNG" => ".png",
                "WEBP" => ".webp",
                _ => ".jpg"
            };
        }

        public static async Task ProcessBatchAsync(
            System.Collections.Generic.IReadOnlyList<ImageItem> items,
            string outputDirectory,
            bool overwrite,
            long maxSizeBytes,
            string outputFormat,
            string photoPresetKey,
            int customW,
            int customH,
            bool maximizeQuality,
            IProgress<ResizeProgressReport> progress,
            CancellationToken ct = default)
        {
            int total = items.Count;
            int completed = 0;

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount),
                CancellationToken = ct
            };

            await Parallel.ForEachAsync(items, parallelOptions, async (item, token) =>
            {
                token.ThrowIfCancellationRequested();

                string ext = GetFormatExtension(outputFormat);
                string outPath = overwrite
                    ? item.FilePath
                    : Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(item.FilePath) + ext);

                try
                {
                    var result = ResizeImageToTarget(
                        item.FilePath,
                        outPath,
                        maxSizeBytes,
                        outputFormat,
                        photoPresetKey,
                        customW,
                        customH,
                        maximizeQuality
                    );

                    int cur = Interlocked.Increment(ref completed);
                    progress.Report(new ResizeProgressReport(item, cur, total, result, null));
                }
                catch (Exception ex)
                {
                    int cur = Interlocked.Increment(ref completed);
                    progress.Report(new ResizeProgressReport(item, cur, total, null, ex));
                }

                await Task.CompletedTask;
            });
        }
    }
}
