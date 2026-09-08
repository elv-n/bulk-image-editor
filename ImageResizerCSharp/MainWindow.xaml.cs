using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using ImageResizerCSharp.Core;
using ImageResizerCSharp.Models;

namespace ImageResizerCSharp
{
    public partial class MainWindow : Window
    {
        private readonly ObservableCollection<ImageItem> _imageItems = new();
        private string? _sourceFolder;
        private string? _outputFolder;
        private string? _lastSuccessfulOutputDir;
        private bool _isProcessing = false;
        private CancellationTokenSource? _cts;

        private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".tiff"
        };

        public MainWindow()
        {
            InitializeComponent();
            lstFiles.ItemsSource = _imageItems;
        }

        // ─── 1. Folder Selection & Image Loading ─────────────────────────────────

        private void BtnBrowseSource_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Pilih Folder Sumber Foto",
                Multiselect = false
            };

            if (dialog.ShowDialog() == true)
            {
                LoadImagesFromFolder(dialog.FolderName);
            }
        }

        public async void LoadImagesFromFolder(string folderPath)
        {
            if (!Directory.Exists(folderPath)) return;

            _sourceFolder = folderPath;
            txtSourceFolder.Text = folderPath;

            if (string.IsNullOrEmpty(_outputFolder))
            {
                var defOutput = Path.Combine(folderPath, "resized");
                txtOutputFolder.Text = defOutput;
                _outputFolder = defOutput;
            }

            _imageItems.Clear();
            imgPreview.Source = null;
            pnlPreviewEmpty.Visibility = Visibility.Visible;
            txtSpecDims.Text = "Resolusi: -";
            txtSpecSize.Text = "Ukuran: -";
            txtSpecFmt.Text = "Format: -";

            // Show loading state with spinner
            pnlEmptyState.Visibility = Visibility.Collapsed;
            lstFiles.Visibility = Visibility.Collapsed;
            pnlLoadingState.Visibility = Visibility.Visible;
            txtBottomStatus.Text = "Memindai folder sumber...";
            spinnerBottomStatus.Visibility = Visibility.Visible;

            int targetKb = (int)sliderMaxSize.Value;

            // Fast directory scan on background thread
            var files = await Task.Run(() =>
            {
                return Directory.EnumerateFiles(folderPath)
                    .Where(f => SupportedExtensions.Contains(Path.GetExtension(f)))
                    .ToList();
            });

            pnlLoadingState.Visibility = Visibility.Collapsed;
            spinnerBottomStatus.Visibility = Visibility.Collapsed;

            if (files.Count == 0)
            {
                pnlEmptyState.Visibility = Visibility.Visible;
                lstFiles.Visibility = Visibility.Collapsed;
                txtFileListHeader.Text = "Daftar Foto (0)";
                txtFileListSummary.Text = "Tidak ada foto didukung";
                btnStartResize.IsEnabled = false;
                txtBottomStatus.Text = "Tidak ada file gambar yang didukung di folder ini.";
                return;
            }

            pnlEmptyState.Visibility = Visibility.Collapsed;
            lstFiles.Visibility = Visibility.Visible;
            txtFileListHeader.Text = $"Daftar Foto ({files.Count})";

            long totalBytes = 0;
            int overLimit = 0;

            foreach (var file in files)
            {
                var item = new ImageItem(file, targetKb);
                _imageItems.Add(item);
                totalBytes += item.FileSize;
                if (item.IsOverLimit) overLimit++;
            }

            UpdateSummaryBadge(totalBytes, overLimit);
            btnStartResize.IsEnabled = true;
            txtBottomStatus.Text = $"Memuat {files.Count} foto • Total {ImageItem.FormatSize(totalBytes)} • {overLimit} di atas batas {targetKb} KB";

            // Select first item
            if (_imageItems.Count > 0)
            {
                lstFiles.SelectedIndex = 0;
            }

            // Asynchronously load dimensions & 38x38 micro-thumbnails in background thread with header spinner
            _ = LoadMetadataAsync(_imageItems.ToList());
        }

        private async Task LoadMetadataAsync(List<ImageItem> items)
        {
            pnlHeaderSpinner.Visibility = Visibility.Visible;
            txtHeaderSpinnerMsg.Text = "Membaca thumbnail...";

            await Task.Run(() =>
            {
                int total = items.Count;
                for (int i = 0; i < total; i++)
                {
                    var item = items[i];
                    int current = i + 1;
                    try
                    {
                        var dims = ImageResizerEngine.ReadImageDimensions(item.FilePath);
                        var thumb = ImageResizerEngine.GenerateMicroThumbnail(item.FilePath, 38);

                        Dispatcher.InvokeAsync(() =>
                        {
                            item.SetDimensions(dims.Width, dims.Height);
                            if (thumb != null) item.Thumbnail = thumb;
                            txtHeaderSpinnerMsg.Text = $"Membaca thumbnail ({current}/{total})...";
                        });
                    }
                    catch
                    {
                        Dispatcher.InvokeAsync(() => item.DimensionsText = "?");
                    }
                }
            });

            pnlHeaderSpinner.Visibility = Visibility.Collapsed;
        }

        private void UpdateSummaryBadge(long totalBytes, int overLimit)
        {
            txtFileListSummary.Text = $"Total {ImageItem.FormatSize(totalBytes)} • {overLimit} di atas batas";
        }

        private void BtnToggleSelectAll_Click(object sender, RoutedEventArgs e)
        {
            bool allChecked = _imageItems.All(i => i.IsChecked);
            bool newState = !allChecked;
            foreach (var item in _imageItems)
            {
                item.IsChecked = newState;
            }
            btnToggleSelectAll.Content = newState ? "Batal Semua" : "Pilih Semua";
        }

        // ─── 2. Preview & Selection ──────────────────────────────────────────────

        private void LstFiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (lstFiles.SelectedItem is ImageItem selected)
            {
                ShowPreview(selected);
            }
        }

        private void ShowPreview(ImageItem item)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(item.FilePath);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();

                imgPreview.Source = bitmap;
                pnlPreviewEmpty.Visibility = Visibility.Collapsed;

                int targetKb = (int)sliderMaxSize.Value;
                bool isOver = item.FileSize > (targetKb * 1024L);
                string tag = isOver ? " (Di atas batas)" : " (Sesuai batas)";

                txtSpecDims.Text = $"Resolusi: {bitmap.PixelWidth} × {bitmap.PixelHeight} px";
                txtSpecSize.Text = $"Ukuran: {item.FormattedSize}{tag}";
                txtSpecFmt.Text = $"Format: {Path.GetExtension(item.FilePath).TrimStart('.').ToUpperInvariant()}";
            }
            catch (Exception ex)
            {
                imgPreview.Source = null;
                pnlPreviewEmpty.Visibility = Visibility.Visible;
                txtSpecDims.Text = $"Gagal memuat: {ex.Message}";
            }
        }

        // ─── 3. Settings & Presets ───────────────────────────────────────────────

        private void Preset_Checked(object sender, RoutedEventArgs e)
        {
            if (pnlCustomDimensions == null || txtPresetHint == null) return;

            if (rbPresetOriginal.IsChecked == true)
            {
                txtPresetHint.Text = "Pertahankan rasio asli tanpa crop";
                pnlCustomDimensions.Visibility = Visibility.Collapsed;
            }
            else if (rbPreset2x3.IsChecked == true)
            {
                txtPresetHint.Text = "Pas Foto 2x3 cm (255 × 354 px @ 300 DPI) • Center Crop";
                pnlCustomDimensions.Visibility = Visibility.Collapsed;
            }
            else if (rbPreset3x4.IsChecked == true)
            {
                txtPresetHint.Text = "Pas Foto 3x4 cm (354 × 472 px @ 300 DPI) • Center Crop";
                pnlCustomDimensions.Visibility = Visibility.Collapsed;
            }
            else if (rbPreset4x6.IsChecked == true)
            {
                txtPresetHint.Text = "Pas Foto 4x6 cm (472 × 709 px @ 300 DPI) • Center Crop";
                pnlCustomDimensions.Visibility = Visibility.Collapsed;
            }
            else if (rbPreset1x1.IsChecked == true)
            {
                txtPresetHint.Text = "Persegi 1:1 (800 × 800 px) • Center Crop";
                pnlCustomDimensions.Visibility = Visibility.Collapsed;
            }
            else if (rbPresetCustom.IsChecked == true)
            {
                txtPresetHint.Text = "Tentukan resolusi kustom (px)";
                pnlCustomDimensions.Visibility = Visibility.Visible;
            }
        }

        private void SliderMaxSize_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (txtMaxSizeDisplay == null) return;

            int kb = (int)e.NewValue;
            string display = kb >= 1024
                ? $"{kb} KB ({kb / 1024.0:F1} MB)"
                : $"{kb} KB";
            txtMaxSizeDisplay.Text = display;

            int overLimit = 0;
            long totalBytes = 0;
            foreach (var item in _imageItems)
            {
                item.UpdateThreshold(kb);
                totalBytes += item.FileSize;
                if (item.IsOverLimit) overLimit++;
            }

            if (_imageItems.Count > 0)
            {
                UpdateSummaryBadge(totalBytes, overLimit);
            }

            if (lstFiles.SelectedItem is ImageItem selected)
            {
                bool isOver = selected.FileSize > (kb * 1024L);
                string tag = isOver ? " (Di atas batas)" : " (Sesuai batas)";
                txtSpecSize.Text = $"Ukuran: {selected.FormattedSize}{tag}";
            }
        }

        private void QuickSize_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && int.TryParse(btn.Tag?.ToString(), out int kb))
            {
                sliderMaxSize.Value = kb;
            }
        }

        private void BtnBrowseOutput_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Pilih Folder Output",
                Multiselect = false
            };

            if (dialog.ShowDialog() == true)
            {
                _outputFolder = dialog.FolderName;
                txtOutputFolder.Text = dialog.FolderName;
            }
        }

        private void ChkOverwrite_Click(object sender, RoutedEventArgs e)
        {
            bool isOverwrite = chkOverwrite.IsChecked == true;
            txtOutputFolder.IsEnabled = !isOverwrite;
            btnBrowseOutput.IsEnabled = !isOverwrite;
        }

        // ─── 4. Batch Resize Execution (Multi-Core Parallel) ─────────────────────

        private async void BtnStartResize_Click(object sender, RoutedEventArgs e)
        {
            if (_isProcessing) return;

            var selected = _imageItems.Where(i => i.IsChecked).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("Silakan centang setidaknya satu foto untuk di-resize.", "Pilih Foto", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrEmpty(_sourceFolder))
            {
                MessageBox.Show("Silakan pilih folder sumber terlebih dahulu.", "Pilih Folder", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            bool overwrite = chkOverwrite.IsChecked == true;
            string outputDir = overwrite ? _sourceFolder : txtOutputFolder.Text.Trim();
            if (string.IsNullOrEmpty(outputDir))
            {
                outputDir = Path.Combine(_sourceFolder, "resized");
            }

            long maxBytes = (long)sliderMaxSize.Value * 1024L;
            string format = rbFmtPng.IsChecked == true ? "PNG" : (rbFmtWebp.IsChecked == true ? "WEBP" : "JPEG");

            string preset = "Original";
            if (rbPreset2x3.IsChecked == true) preset = "2x3";
            else if (rbPreset3x4.IsChecked == true) preset = "3x4";
            else if (rbPreset4x6.IsChecked == true) preset = "4x6";
            else if (rbPreset1x1.IsChecked == true) preset = "1x1";
            else if (rbPresetCustom.IsChecked == true) preset = "Custom";

            int customW = int.TryParse(txtCustomW.Text, out int w) ? w : 600;
            int customH = int.TryParse(txtCustomH.Text, out int h) ? h : 800;
            bool maximizeQuality = chkMaximizeQuality.IsChecked == true;

            // Lock UI
            _isProcessing = true;
            SetControlsEnabled(false);
            pbProgress.Value = 0;
            txtProgressPct.Text = "0%";
            spinnerResizeBtn.Visibility = Visibility.Visible;
            txtStartResizeBtn.Text = "Memproses...";
            spinnerBottomStatus.Visibility = Visibility.Visible;
            _lastSuccessfulOutputDir = outputDir;

            foreach (var item in selected)
            {
                item.Status = ProcessingStatus.Processing;
                item.StatusMessage = "Memproses...";
            }

            _cts = new CancellationTokenSource();
            var sw = Stopwatch.StartNew();
            long totalSaved = 0;

            var progress = new Progress<ResizeProgressReport>(report =>
            {
                if (report.Error != null)
                {
                    report.Item.Status = ProcessingStatus.Error;
                    report.Item.StatusMessage = "Gagal";
                }
                else if (report.Result != null)
                {
                    if (report.Result.Status == "skipped")
                    {
                        report.Item.Status = ProcessingStatus.Skipped;
                        report.Item.StatusMessage = "Skip";
                    }
                    else
                    {
                        report.Item.Status = ProcessingStatus.Done;
                        report.Item.StatusMessage = $"{ImageItem.FormatSize(report.Result.NewSize)} • {report.Result.NewWidth}×{report.Result.NewHeight}";
                        long saved = report.Result.OriginalSize - report.Result.NewSize;
                        if (saved > 0) Interlocked.Add(ref totalSaved, saved);
                    }
                }

                double pct = (double)report.CurrentIndex / report.TotalCount * 100.0;
                pbProgress.Value = pct;
                txtProgressPct.Text = $"{(int)pct}%";
                txtBottomStatus.Text = $"Memproses {report.CurrentIndex}/{report.TotalCount}: {report.Item.FileName}";
            });

            try
            {
                await ImageResizerEngine.ProcessBatchAsync(
                    selected,
                    outputDir,
                    overwrite,
                    maxBytes,
                    format,
                    preset,
                    customW,
                    customH,
                    maximizeQuality,
                    progress,
                    _cts.Token
                );

                sw.Stop();
                string destDisplay = overwrite ? "folder sumber (ditimpa)" : outputDir;
                txtBottomStatus.Text = $"Selesai! {selected.Count} foto diproses dalam {sw.Elapsed.TotalSeconds:F1}s • Hemat {ImageItem.FormatSize(totalSaved)} • Output: {destDisplay}";
                btnOpenFolder.IsEnabled = true;
            }
            catch (Exception ex)
            {
                txtBottomStatus.Text = $"Terjadi kesalahan: {ex.Message}";
            }
            finally
            {
                _isProcessing = false;
                SetControlsEnabled(true);
                spinnerResizeBtn.Visibility = Visibility.Collapsed;
                txtStartResizeBtn.Text = "Mulai Resize";
                spinnerBottomStatus.Visibility = Visibility.Collapsed;
            }
        }

        private void SetControlsEnabled(bool enabled)
        {
            btnBrowseSource.IsEnabled = enabled;
            btnToggleSelectAll.IsEnabled = enabled;
            btnStartResize.IsEnabled = enabled;
            pnlPresetButtons.IsEnabled = enabled;
            sliderMaxSize.IsEnabled = enabled;
            chkMaximizeQuality.IsEnabled = enabled;
            rbFmtJpeg.IsEnabled = enabled;
            rbFmtPng.IsEnabled = enabled;
            rbFmtWebp.IsEnabled = enabled;
            btnBrowseOutput.IsEnabled = enabled;
            chkOverwrite.IsEnabled = enabled;
        }

        private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_lastSuccessfulOutputDir) && Directory.Exists(_lastSuccessfulOutputDir))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = _lastSuccessfulOutputDir,
                    UseShellExecute = true
                });
            }
        }
    }
}