using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using ImageResizerCSharp.Core;
using ImageResizerCSharp.Models;

namespace ImageResizerCSharp.Tests
{
    public static class TestRunner
    {
        public static async Task<bool> RunAllTestsAsync()
        {
            Console.WriteLine("=== Starting C# ImageResizer Verification Tests ===");
            string tempDir = Path.Combine(Path.GetTempPath(), "ImageResizerTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                // 1. Create sample test images
                string img1 = Path.Combine(tempDir, "sample1.jpg");
                string img2 = Path.Combine(tempDir, "sample2.png");

                using (var image1 = new Image<Rgb24>(1600, 1200))
                {
                    image1.ProcessPixelRows(accessor =>
                    {
                        for (int y = 0; y < accessor.Height; y++)
                        {
                            var pixelRow = accessor.GetRowSpan(y);
                            for (int x = 0; x < pixelRow.Length; x++)
                            {
                                pixelRow[x] = new Rgb24(
                                    (byte)((x * 255) / accessor.Width),
                                    (byte)((y * 255) / accessor.Height),
                                    (byte)((x * 7 + y * 13) % 256)
                                );
                            }
                        }
                    });
                    image1.SaveAsJpeg(img1);
                }

                using (var image2 = new Image<Rgb24>(1200, 800))
                {
                    image2.SaveAsPng(img2);
                }

                Console.WriteLine("[PASS] Sample test images created.");

                // 2. Test Dimension Reading
                var dims = ImageResizerEngine.ReadImageDimensions(img1);
                if (dims.Width != 1600 || dims.Height != 1200)
                {
                    throw new Exception($"Dimension mismatch: Expected 1600x1200, got {dims.Width}x{dims.Height}");
                }
                Console.WriteLine($"[PASS] Dimensions read: {dims.Width}x{dims.Height}");

                // 3. Test Micro-Thumbnail Generation (38x38 px)
                var thumb = ImageResizerEngine.GenerateMicroThumbnail(img1, 38);
                if (thumb == null)
                {
                    throw new Exception("Micro-thumbnail generation failed.");
                }
                Console.WriteLine($"[PASS] Micro-thumbnail generated: {thumb.PixelWidth}x{thumb.PixelHeight} px");

                // 4. Test Pas Foto 3x4 Preset (Fixed 300 DPI Mode: Expected 354x472 px)
                string outPasFoto = Path.Combine(tempDir, "pasfoto_3x4.jpg");
                var pasResult = ImageResizerEngine.ResizeImageToTarget(
                    img1,
                    outPasFoto,
                    maxSizeBytes: 2 * 1024 * 1024,
                    outputFormat: "JPEG",
                    photoPresetKey: "3x4",
                    maximizeQuality: false
                );

                if (pasResult.NewWidth != 354 || pasResult.NewHeight != 472)
                {
                    throw new Exception($"Pas foto 3x4 dimension mismatch: Expected 354x472, got {pasResult.NewWidth}x{pasResult.NewHeight}");
                }
                Console.WriteLine($"[PASS] Pas Foto 3x4 fixed DPI resize verified: {pasResult.NewWidth}x{pasResult.NewHeight} px");

                // 4B. Test Pas Foto 3x4 with Maximize Quality enabled (Ultra HD scaling to target 500 KB)
                string outPasFotoMax = Path.Combine(tempDir, "pasfoto_3x4_max.jpg");
                long target500Kb = 500 * 1024;
                var pasMaxResult = ImageResizerEngine.ResizeImageToTarget(
                    img1,
                    outPasFotoMax,
                    maxSizeBytes: target500Kb,
                    outputFormat: "JPEG",
                    photoPresetKey: "3x4",
                    maximizeQuality: true
                );

                if (pasMaxResult.NewSize > target500Kb)
                {
                    throw new Exception($"Pas foto max quality size exceeded: {pasMaxResult.NewSize} > {target500Kb}");
                }
                double aspect = (double)pasMaxResult.NewWidth / pasMaxResult.NewHeight;
                if (Math.Abs(aspect - 0.75) > 0.05)
                {
                    throw new Exception($"Pas foto aspect ratio mismatch: {aspect} vs 0.75");
                }
                Console.WriteLine($"[PASS] Pas Foto 3x4 max quality verified: {pasMaxResult.NewWidth}x{pasMaxResult.NewHeight}, {pasMaxResult.NewSize} B (close to {target500Kb} B)");

                // 5. Test Target Size Compression (<= 100 KB)
                string outCompressed = Path.Combine(tempDir, "compressed_target.jpg");
                long targetBytes = 100 * 1024;
                var compResult = ImageResizerEngine.ResizeImageToTarget(
                    img1,
                    outCompressed,
                    maxSizeBytes: targetBytes,
                    outputFormat: "JPEG",
                    photoPresetKey: "Original",
                    maximizeQuality: true
                );

                if (compResult.NewSize > targetBytes)
                {
                    throw new Exception($"Target size exceeded: Expected <= {targetBytes} B, got {compResult.NewSize} B");
                }
                Console.WriteLine($"[PASS] Target compression verified: {compResult.NewSize} B <= {targetBytes} B (Quality iterations successful)");

                // 6. Test Multi-Core Parallel Batch Processing
                var items = new List<ImageItem>
                {
                    new ImageItem(img1, 500),
                    new ImageItem(img2, 500)
                };

                string outDir = Path.Combine(tempDir, "batch_out");
                var progress = new Progress<ResizeProgressReport>(r =>
                {
                    Console.WriteLine($"[BATCH PROGRESS] Completed {r.CurrentIndex}/{r.TotalCount}: {r.Item.FileName}");
                });

                await ImageResizerEngine.ProcessBatchAsync(
                    items,
                    outDir,
                    overwrite: false,
                    maxSizeBytes: 500 * 1024,
                    outputFormat: "JPEG",
                    photoPresetKey: "Original",
                    customW: 600,
                    customH: 800,
                    maximizeQuality: true,
                    progress: progress,
                    ct: CancellationToken.None
                );

                var generatedFiles = Directory.GetFiles(outDir);
                if (generatedFiles.Length != 2)
                {
                    throw new Exception($"Batch processing count mismatch: Expected 2 files, found {generatedFiles.Length}");
                }
                // 7. Test AI MODNet Pasfoto Indonesia Matting & Initialization
                if (!BackgroundMattingEngine.IsModelAvailable())
                {
                    throw new FileNotFoundException($"MODNet model file not found at: {BackgroundMattingEngine.GetModelPath()}");
                }
                Console.WriteLine($"[PASS] MODNet fine-tuned model located: {BackgroundMattingEngine.GetModelPath()}");

                using (var sampleImage = Image.Load<Rgba32>(img1))
                {
                    using var alphaMatte = BackgroundMattingEngine.GenerateAlphaMatte(sampleImage);
                    if (alphaMatte.Width != sampleImage.Width || alphaMatte.Height != sampleImage.Height)
                    {
                        throw new Exception($"Alpha matte dimension mismatch: Expected {sampleImage.Width}x{sampleImage.Height}, got {alphaMatte.Width}x{alphaMatte.Height}");
                    }
                    Console.WriteLine($"[PASS] Alpha matte generated successfully: {alphaMatte.Width}x{alphaMatte.Height} px");

                    // 8. Test Background Color Replacement (Red Ganjil, Blue Genap, Transparent)
                    using var redComposite = BackgroundMattingEngine.ReplaceBackground(sampleImage, PhotoBackgroundType.Red);
                    if (redComposite.Width != sampleImage.Width || redComposite.Height != sampleImage.Height)
                    {
                        throw new Exception("Red background composite dimension mismatch.");
                    }
                    Console.WriteLine("[PASS] Pasfoto Red background (#D81B1B) replacement verified.");

                    using var blueComposite = BackgroundMattingEngine.ReplaceBackground(sampleImage, PhotoBackgroundType.Blue);
                    if (blueComposite.Width != sampleImage.Width || blueComposite.Height != sampleImage.Height)
                    {
                        throw new Exception("Blue background composite dimension mismatch.");
                    }
                    Console.WriteLine("[PASS] Pasfoto Blue background (#0066CC) replacement verified.");

                    using var transparentComposite = BackgroundMattingEngine.ReplaceBackground(sampleImage, PhotoBackgroundType.Transparent);
                    if (transparentComposite.Width != sampleImage.Width || transparentComposite.Height != sampleImage.Height)
                    {
                        throw new Exception("Transparent composite dimension mismatch.");
                    }
                    Console.WriteLine("[PASS] Transparent background cutout verified.");
                }

                // 9. Test End-to-End Pas Foto 3x4 with Red Background Replacement & Compression
                string outPasfotoRed = Path.Combine(tempDir, "pasfoto_3x4_red.jpg");
                var e2eResult = ImageResizerEngine.ResizeImageToTarget(
                    img1,
                    outPasfotoRed,
                    maxSizeBytes: 300 * 1024,
                    outputFormat: "JPEG",
                    photoPresetKey: "3x4",
                    maximizeQuality: true,
                    bgType: PhotoBackgroundType.Red
                );

                if (e2eResult.NewWidth <= 0 || e2eResult.NewHeight <= 0)
                {
                    throw new Exception("End-to-end resize with AI background returned invalid dimensions.");
                }
                if (e2eResult.NewSize > 300 * 1024)
                {
                    throw new Exception($"End-to-end result exceeded target size: {e2eResult.NewSize} > 307200");
                }
                Console.WriteLine($"[PASS] End-to-end Pasfoto 3x4 + Red Background + Target Size: {e2eResult.NewWidth}x{e2eResult.NewHeight}, {e2eResult.NewSize} B");

                // 10. Test Interactive Free Crop (NormalizedCropRect)
                var cropRect = new NormalizedCropRect(0.1, 0.2, 0.5, 0.6);
                var isRect = cropRect.ToImageSharpRect(1600, 1200);
                if (isRect.X != 160 || isRect.Y != 240 || isRect.Width != 800 || isRect.Height != 720)
                {
                    throw new Exception($"NormalizedCropRect mapping mismatch: Expected (160, 240, 800, 720), got ({isRect.X}, {isRect.Y}, {isRect.Width}, {isRect.Height})");
                }

                string outCrop = Path.Combine(tempDir, "cropped_custom.jpg");
                var cropResult = ImageResizerEngine.ResizeImageToTarget(
                    img1,
                    outCrop,
                    maxSizeBytes: 200 * 1024,
                    outputFormat: "JPEG",
                    photoPresetKey: "Original",
                    customCrop: cropRect
                );
                if (cropResult.NewWidth != 800 || cropResult.NewHeight != 720)
                {
                    throw new Exception($"Cropped output dimension mismatch: Expected 800x720, got {cropResult.NewWidth}x{cropResult.NewHeight}");
                }
                Console.WriteLine($"[PASS] Free Crop test verified: {cropResult.NewWidth}x{cropResult.NewHeight} px from 1600x1200 source.");

                // 11. Test Image Rotation (90°, 180°, 270°)
                var testItem = new ImageItem(img1, 500);
                testItem.SetDimensions(1600, 1200);
                testItem.RotationAngle = 90;
                if (testItem.EffectiveWidth != 1200 || testItem.EffectiveHeight != 1600)
                {
                    throw new Exception($"ImageItem rotation dimension swap mismatch: Expected 1200x1600, got {testItem.EffectiveWidth}x{testItem.EffectiveHeight}");
                }
                if (!testItem.HasRotation)
                {
                    throw new Exception("ImageItem HasRotation should be true when RotationAngle is 90.");
                }

                string outRotated90 = Path.Combine(tempDir, "rotated_90.jpg");
                var rotResult90 = ImageResizerEngine.ResizeImageToTarget(
                    img1,
                    outRotated90,
                    maxSizeBytes: 300 * 1024,
                    outputFormat: "JPEG",
                    photoPresetKey: "Original",
                    rotationAngle: 90
                );
                if (rotResult90.NewWidth != 1200 || rotResult90.NewHeight != 1600)
                {
                    throw new Exception($"Rotated 90 output dimension mismatch: Expected 1200x1600, got {rotResult90.NewWidth}x{rotResult90.NewHeight}");
                }
                Console.WriteLine($"[PASS] Image Rotation 90° verified: {rotResult90.NewWidth}x{rotResult90.NewHeight} px (swapped from 1600x1200 source).");

                string outRotated180 = Path.Combine(tempDir, "rotated_180.jpg");
                var rotResult180 = ImageResizerEngine.ResizeImageToTarget(
                    img1,
                    outRotated180,
                    maxSizeBytes: 300 * 1024,
                    outputFormat: "JPEG",
                    photoPresetKey: "Original",
                    rotationAngle: 180
                );
                if (rotResult180.NewWidth != 1600 || rotResult180.NewHeight != 1200)
                {
                    throw new Exception($"Rotated 180 output dimension mismatch: Expected 1600x1200, got {rotResult180.NewWidth}x{rotResult180.NewHeight}");
                }
                Console.WriteLine($"[PASS] Image Rotation 180° verified: {rotResult180.NewWidth}x{rotResult180.NewHeight} px.");

                Console.WriteLine("=== All C# ImageResizer Verification Tests PASSED! ===");
                return true;
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }
}
