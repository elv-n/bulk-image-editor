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
using System.Windows.Controls.Primitives;
using System.Windows.Media;
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

        private PhotoBackgroundType _selectedBgType = PhotoBackgroundType.None;
        private string _customBgHex = "#6366F1";
        private ImageItem? _currentPreviewItem = null;
        private BitmapSource? _originalPreviewBitmap = null;
        private bool _isPreviewingAi = false;
        private bool _isCropMode = false;
        private double? _lockedCropRatio = null;

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
                var defOutput = Path.Combine(folderPath, "edited");
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

        private BitmapSource? _lastAiPreviewBitmap = null;
        private int _aiPreviewGeneration = 0;

        private void ShowPreview(ImageItem item)
        {
            _currentPreviewItem = item;
            _isPreviewingAi = false;
            _lastAiPreviewBitmap = null;
            if (badgeAiPreviewActive != null) badgeAiPreviewActive.Visibility = Visibility.Collapsed;
            if (btnResetPreview != null) btnResetPreview.Visibility = Visibility.Collapsed;

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(item.FilePath);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();

                _originalPreviewBitmap = bitmap;
                imgPreview.Source = bitmap;
                pnlPreviewEmpty.Visibility = Visibility.Collapsed;

                int targetKb = (int)sliderMaxSize.Value;
                bool isOver = item.FileSize > (targetKb * 1024L);
                string tag = isOver ? " (Di atas batas)" : " (Sesuai batas)";

                txtSpecDims.Text = $"Resolusi: {bitmap.PixelWidth} × {bitmap.PixelHeight} px";
                txtSpecSize.Text = $"Ukuran: {item.FormattedSize}{tag}";
                txtSpecSize.Foreground = isOver
                    ? (System.Windows.Media.Brush)FindResource("DangerBrush")
                    : (System.Windows.Media.Brush)FindResource("SuccessBrush");
                txtSpecFmt.Text = $"Format: {Path.GetExtension(item.FilePath).TrimStart('.').ToUpperInvariant()}";

                // Jika latar belakang warna, preset crop, atau crop manual aktif, tampilkan preview live
                if (_isCropMode)
                {
                    ShowUncroppedPreviewForCropMode();
                    InitCropBoxPosition();
                }
                else if (_selectedBgType != PhotoBackgroundType.None || GetSelectedPresetKey() != "Original" || item.CustomCrop != null)
                {
                    TriggerAiPreviewAsync();
                }
            }
            catch (Exception ex)
            {
                imgPreview.Source = null;
                pnlPreviewEmpty.Visibility = Visibility.Visible;
                txtSpecDims.Text = $"Gagal memuat: {ex.Message}";
            }
        }

        private string GetSelectedPresetKey()
        {
            if (rbPreset2x3?.IsChecked == true) return "2x3";
            if (rbPreset3x4?.IsChecked == true) return "3x4";
            if (rbPreset4x6?.IsChecked == true) return "4x6";
            if (rbPreset1x1?.IsChecked == true) return "1x1";
            if (rbPresetCustom?.IsChecked == true) return "Custom";
            return "Original";
        }

        private (int W, int H) GetCustomDimensions()
        {
            int w = (txtCustomW != null && int.TryParse(txtCustomW.Text, out int cw)) ? cw : 600;
            int h = (txtCustomH != null && int.TryParse(txtCustomH.Text, out int ch)) ? ch : 800;
            return (w, h);
        }

        // ─── Interactive Free Crop Mode ──────────────────────────────────────────

        private void BtnToggleCropMode_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPreviewItem == null)
            {
                btnToggleCropMode.IsChecked = false;
                return;
            }

            _isCropMode = btnToggleCropMode.IsChecked == true;

            if (_isCropMode)
            {
                btnToggleCropMode.Content = "✓ Selesai Crop";
                pnlCropRatioOptions.Visibility = Visibility.Visible;
                cropCanvas.Visibility = Visibility.Visible;

                ShowUncroppedPreviewForCropMode();

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    InitCropBoxPosition();
                }), System.Windows.Threading.DispatcherPriority.Loaded);
            }
            else
            {
                btnToggleCropMode.Content = "✂ Crop Bebas";
                pnlCropRatioOptions.Visibility = Visibility.Collapsed;
                cropCanvas.Visibility = Visibility.Collapsed;

                TriggerAiPreviewAsync();
            }
        }

        private async void ShowUncroppedPreviewForCropMode()
        {
            if (_currentPreviewItem == null) return;
            string filePath = _currentPreviewItem.FilePath;
            var bgToPreview = _selectedBgType;
            string hex = _customBgHex;

            if (bgToPreview == PhotoBackgroundType.None)
            {
                if (_originalPreviewBitmap != null)
                {
                    imgPreview.Source = _originalPreviewBitmap;
                }
            }
            else
            {
                try
                {
                    var previewBitmap = await Task.Run(() =>
                    {
                        return BackgroundMattingEngine.GeneratePreviewBitmap(
                            filePath,
                            bgToPreview,
                            hex,
                            720,
                            "Original",
                            600,
                            800,
                            null
                        );
                    });

                    if (_isCropMode && _currentPreviewItem?.FilePath == filePath)
                    {
                        imgPreview.Source = previewBitmap;
                    }
                }
                catch { }
            }
        }

        private void CropRatio_Checked(object sender, RoutedEventArgs e)
        {
            if (rbCropRatio2x3?.IsChecked == true) _lockedCropRatio = 2.0 / 3.0;
            else if (rbCropRatio3x4?.IsChecked == true) _lockedCropRatio = 3.0 / 4.0;
            else if (rbCropRatio4x6?.IsChecked == true) _lockedCropRatio = 4.0 / 6.0;
            else if (rbCropRatio1x1?.IsChecked == true) _lockedCropRatio = 1.0;
            else _lockedCropRatio = null; // Free

            if (_isCropMode && _currentPreviewItem != null)
            {
                AdjustCropBoxToLockedRatio();
            }
        }

        private void BtnResetCrop_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPreviewItem == null) return;
            _currentPreviewItem.CustomCrop = null;

            if (_isCropMode)
            {
                InitCropBoxPosition();
            }
            else
            {
                TriggerAiPreviewAsync();
            }
        }

        private void GridPreviewContainer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_isCropMode && _currentPreviewItem != null)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    UpdateCropBoxFromNormalizedRect();
                }), System.Windows.Threading.DispatcherPriority.Render);
            }
        }

        private Rect GetRenderedImageRect()
        {
            if (imgPreview.Source == null || imgPreview.ActualWidth <= 0 || imgPreview.ActualHeight <= 0)
                return Rect.Empty;

            double srcW = imgPreview.Source.Width;
            double srcH = imgPreview.Source.Height;
            if (srcW <= 0 || srcH <= 0) return Rect.Empty;

            double availW = imgPreview.ActualWidth;
            double availH = imgPreview.ActualHeight;

            double srcRatio = srcW / srcH;
            double availRatio = availW / availH;

            double renderW, renderH;
            if (srcRatio > availRatio)
            {
                renderW = availW;
                renderH = availW / srcRatio;
            }
            else
            {
                renderH = availH;
                renderW = availH * srcRatio;
            }

            Point imgPos = imgPreview.TranslatePoint(new Point(0, 0), cropCanvas);
            double renderX = imgPos.X + (availW - renderW) / 2.0;
            double renderY = imgPos.Y + (availH - renderH) / 2.0;

            return new Rect(renderX, renderY, renderW, renderH);
        }

        private void InitCropBoxPosition()
        {
            var renderRect = GetRenderedImageRect();
            if (renderRect.IsEmpty || renderRect.Width < 20 || renderRect.Height < 20) return;

            if (_currentPreviewItem?.CustomCrop != null)
            {
                UpdateCropBoxFromNormalizedRect();
            }
            else
            {
                double w = renderRect.Width * 0.8;
                double h = renderRect.Height * 0.8;

                if (_lockedCropRatio.HasValue)
                {
                    double ratio = _lockedCropRatio.Value;
                    if (w / h > ratio)
                    {
                        w = h * ratio;
                    }
                    else
                    {
                        h = w / ratio;
                    }
                }

                double x = renderRect.X + (renderRect.Width - w) / 2.0;
                double y = renderRect.Y + (renderRect.Height - h) / 2.0;

                SetCropBoxRect(new Rect(x, y, w, h));
            }
        }

        private void AdjustCropBoxToLockedRatio()
        {
            if (!_lockedCropRatio.HasValue) return;

            var renderRect = GetRenderedImageRect();
            if (renderRect.IsEmpty) return;

            double curX = Canvas.GetLeft(cropBoxThumb);
            double curY = Canvas.GetTop(cropBoxThumb);
            double curW = cropBoxThumb.Width;
            double curH = cropBoxThumb.Height;

            if (double.IsNaN(curX) || curW <= 0) return;

            double ratio = _lockedCropRatio.Value;
            double newW = curW;
            double newH = newW / ratio;

            if (curY + newH > renderRect.Bottom)
            {
                newH = renderRect.Bottom - curY;
                newW = newH * ratio;
            }

            if (curX + newW > renderRect.Right)
            {
                newW = renderRect.Right - curX;
                newH = newW / ratio;
            }

            SetCropBoxRect(new Rect(curX, curY, newW, newH));
        }

        private void UpdateCropBoxFromNormalizedRect()
        {
            var renderRect = GetRenderedImageRect();
            if (renderRect.IsEmpty) return;

            var norm = _currentPreviewItem?.CustomCrop;
            if (norm != null)
            {
                double x = renderRect.X + norm.X * renderRect.Width;
                double y = renderRect.Y + norm.Y * renderRect.Height;
                double w = norm.Width * renderRect.Width;
                double h = norm.Height * renderRect.Height;
                SetCropBoxRect(new Rect(x, y, w, h), saveToModel: false);
            }
        }

        private void SetCropBoxRect(Rect r, bool saveToModel = true)
        {
            var renderRect = GetRenderedImageRect();
            if (renderRect.IsEmpty) return;

            double x = Math.Clamp(r.X, renderRect.X, Math.Max(renderRect.X, renderRect.Right - 20));
            double y = Math.Clamp(r.Y, renderRect.Y, Math.Max(renderRect.Y, renderRect.Bottom - 20));
            double w = Math.Clamp(r.Width, 20, Math.Max(20, renderRect.Right - x));
            double h = Math.Clamp(r.Height, 20, Math.Max(20, renderRect.Bottom - y));

            Canvas.SetLeft(cropBoxThumb, x);
            Canvas.SetTop(cropBoxThumb, y);
            cropBoxThumb.Width = w;
            cropBoxThumb.Height = h;

            double hw = 5;
            double hh = 5;

            Canvas.SetLeft(thumbNW, x - hw);
            Canvas.SetTop(thumbNW, y - hh);

            Canvas.SetLeft(thumbNE, x + w - hw);
            Canvas.SetTop(thumbNE, y - hh);

            Canvas.SetLeft(thumbSW, x - hw);
            Canvas.SetTop(thumbSW, y + h - hh);

            Canvas.SetLeft(thumbSE, x + w - hw);
            Canvas.SetTop(thumbSE, y + h - hh);

            Canvas.SetLeft(thumbN, x + w / 2.0 - hw);
            Canvas.SetTop(thumbN, y - hh);

            Canvas.SetLeft(thumbS, x + w / 2.0 - hw);
            Canvas.SetTop(thumbS, y + h - hh);

            Canvas.SetLeft(thumbW, x - hw);
            Canvas.SetTop(thumbW, y + h / 2.0 - hh);

            Canvas.SetLeft(thumbE, x + w - hw);
            Canvas.SetTop(thumbE, y + h / 2.0 - hh);

            if (cropCanvas.ActualWidth > 0 && cropCanvas.ActualHeight > 0)
            {
                var outer = new RectangleGeometry(new Rect(0, 0, cropCanvas.ActualWidth, cropCanvas.ActualHeight));
                var inner = new RectangleGeometry(new Rect(x, y, w, h));
                cropMaskPath.Data = new CombinedGeometry(GeometryCombineMode.Exclude, outer, inner);
            }

            if (saveToModel && _currentPreviewItem != null && renderRect.Width > 0 && renderRect.Height > 0)
            {
                double nx = (x - renderRect.X) / renderRect.Width;
                double ny = (y - renderRect.Y) / renderRect.Height;
                double nw = w / renderRect.Width;
                double nh = h / renderRect.Height;

                _currentPreviewItem.CustomCrop = new NormalizedCropRect(nx, ny, nw, nh);

                if (_originalPreviewBitmap != null)
                {
                    int pxW = (int)Math.Round(nw * _originalPreviewBitmap.PixelWidth);
                    int pxH = (int)Math.Round(nh * _originalPreviewBitmap.PixelHeight);
                    txtSpecDims.Text = $"Crop Aktif: {pxW} × {pxH} px";
                }
            }
        }

        private void CropBoxThumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            var renderRect = GetRenderedImageRect();
            if (renderRect.IsEmpty) return;

            double curX = Canvas.GetLeft(cropBoxThumb);
            double curY = Canvas.GetTop(cropBoxThumb);
            double w = cropBoxThumb.Width;
            double h = cropBoxThumb.Height;

            double newX = curX + e.HorizontalChange;
            double newY = curY + e.VerticalChange;

            newX = Math.Clamp(newX, renderRect.X, Math.Max(renderRect.X, renderRect.Right - w));
            newY = Math.Clamp(newY, renderRect.Y, Math.Max(renderRect.Y, renderRect.Bottom - h));

            SetCropBoxRect(new Rect(newX, newY, w, h));
        }

        private void Handle_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (sender is not Thumb thumb || thumb.Tag is not string tag) return;
            var renderRect = GetRenderedImageRect();
            if (renderRect.IsEmpty) return;

            double x = Canvas.GetLeft(cropBoxThumb);
            double y = Canvas.GetTop(cropBoxThumb);
            double w = cropBoxThumb.Width;
            double h = cropBoxThumb.Height;

            double dx = e.HorizontalChange;
            double dy = e.VerticalChange;

            switch (tag)
            {
                case "SE":
                    w += dx;
                    h = _lockedCropRatio.HasValue ? w / _lockedCropRatio.Value : h + dy;
                    break;
                case "SW":
                    x += dx;
                    w -= dx;
                    h = _lockedCropRatio.HasValue ? w / _lockedCropRatio.Value : h + dy;
                    break;
                case "NE":
                    w += dx;
                    if (_lockedCropRatio.HasValue)
                    {
                        double oldH = h;
                        h = w / _lockedCropRatio.Value;
                        y -= (h - oldH);
                    }
                    else
                    {
                        y += dy;
                        h -= dy;
                    }
                    break;
                case "NW":
                    x += dx;
                    w -= dx;
                    if (_lockedCropRatio.HasValue)
                    {
                        double oldH = h;
                        h = w / _lockedCropRatio.Value;
                        y -= (h - oldH);
                    }
                    else
                    {
                        y += dy;
                        h -= dy;
                    }
                    break;
                case "E":
                    w += dx;
                    if (_lockedCropRatio.HasValue) h = w / _lockedCropRatio.Value;
                    break;
                case "W":
                    x += dx;
                    w -= dx;
                    if (_lockedCropRatio.HasValue) h = w / _lockedCropRatio.Value;
                    break;
                case "S":
                    h += dy;
                    if (_lockedCropRatio.HasValue) w = h * _lockedCropRatio.Value;
                    break;
                case "N":
                    y += dy;
                    h -= dy;
                    if (_lockedCropRatio.HasValue) w = h * _lockedCropRatio.Value;
                    break;
            }

            SetCropBoxRect(new Rect(x, y, w, h));
        }

        private async void TriggerAiPreviewAsync()
        {
            if (_currentPreviewItem == null) return;

            string preset = GetSelectedPresetKey();
            var (customW, customH) = GetCustomDimensions();
            var customCrop = _currentPreviewItem.CustomCrop;

            // Jika preset Original, tidak ada crop manual, dan Background None: kembalikan ke foto asli
            if (_selectedBgType == PhotoBackgroundType.None && preset == "Original" && customCrop == null)
            {
                if (_originalPreviewBitmap != null)
                {
                    imgPreview.Source = _originalPreviewBitmap;
                    _isPreviewingAi = false;
                    if (badgeAiPreviewActive != null) badgeAiPreviewActive.Visibility = Visibility.Collapsed;
                    if (btnResetPreview != null) btnResetPreview.Visibility = Visibility.Collapsed;

                    txtSpecDims.Text = $"Resolusi: {_originalPreviewBitmap.PixelWidth} × {_originalPreviewBitmap.PixelHeight} px";
                }
                return;
            }

            string filePath = _currentPreviewItem.FilePath;
            var bgToPreview = _selectedBgType;
            string hex = _customBgHex;

            int currentGen = Interlocked.Increment(ref _aiPreviewGeneration);
            if (pnlAiLoading != null && bgToPreview != PhotoBackgroundType.None)
            {
                pnlAiLoading.Visibility = Visibility.Visible;
            }

            try
            {
                var previewBitmap = await Task.Run(() =>
                {
                    return BackgroundMattingEngine.GeneratePreviewBitmap(
                        filePath,
                        bgToPreview,
                        hex,
                        720,
                        preset,
                        customW,
                        customH,
                        customCrop
                    );
                });

                if (currentGen == _aiPreviewGeneration && _currentPreviewItem?.FilePath == filePath)
                {
                    _lastAiPreviewBitmap = previewBitmap;
                    imgPreview.Source = previewBitmap;
                    _isPreviewingAi = (bgToPreview != PhotoBackgroundType.None);

                    if (badgeAiPreviewActive != null)
                    {
                        badgeAiPreviewActive.Visibility = _isPreviewingAi ? Visibility.Visible : Visibility.Collapsed;
                    }

                    if (btnResetPreview != null)
                    {
                        if (bgToPreview != PhotoBackgroundType.None)
                        {
                            btnResetPreview.Visibility = Visibility.Visible;
                            btnResetPreview.Content = "Bandingkan Asli";
                        }
                        else
                        {
                            btnResetPreview.Visibility = Visibility.Collapsed;
                        }
                    }

                    if (customCrop != null && _originalPreviewBitmap != null)
                    {
                        int cw = (int)Math.Round(customCrop.Width * _originalPreviewBitmap.PixelWidth);
                        int ch = (int)Math.Round(customCrop.Height * _originalPreviewBitmap.PixelHeight);
                        txtSpecDims.Text = $"Crop Bebas: {cw} × {ch} px";
                    }
                    else
                    {
                        var presetDims = ImageResizerEngine.GetPresetDimensions(preset, customW, customH);
                        if (presetDims.HasValue)
                        {
                            txtSpecDims.Text = $"Preview Crop: {presetDims.Value.Width} × {presetDims.Value.Height} px ({preset})";
                        }
                        else if (_originalPreviewBitmap != null)
                        {
                            txtSpecDims.Text = $"Resolusi: {_originalPreviewBitmap.PixelWidth} × {_originalPreviewBitmap.PixelHeight} px";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                txtBottomStatus.Text = $"Catatan preview: {ex.Message}";
            }
            finally
            {
                if (currentGen == _aiPreviewGeneration && pnlAiLoading != null)
                {
                    pnlAiLoading.Visibility = Visibility.Collapsed;
                }
            }
        }

        private async void BtnResetPreview_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPreviewItem == null) return;

            string preset = GetSelectedPresetKey();
            var (customW, customH) = GetCustomDimensions();
            string filePath = _currentPreviewItem.FilePath;
            var customCrop = _currentPreviewItem.CustomCrop;

            if (_isPreviewingAi)
            {
                var origCropped = await Task.Run(() =>
                {
                    return BackgroundMattingEngine.GeneratePreviewBitmap(
                        filePath,
                        PhotoBackgroundType.None,
                        "",
                        720,
                        preset,
                        customW,
                        customH,
                        customCrop
                    );
                });

                imgPreview.Source = origCropped;
                _isPreviewingAi = false;
                if (badgeAiPreviewActive != null) badgeAiPreviewActive.Visibility = Visibility.Collapsed;
                btnResetPreview.Content = "Lihat AI";
            }
            else if (_lastAiPreviewBitmap != null)
            {
                imgPreview.Source = _lastAiPreviewBitmap;
                _isPreviewingAi = true;
                if (badgeAiPreviewActive != null) badgeAiPreviewActive.Visibility = Visibility.Visible;
                btnResetPreview.Content = "Bandingkan Asli";
            }
        }

        // ─── 3. Settings & Presets ───────────────────────────────────────────────

        private void BgPreset_Checked(object sender, RoutedEventArgs e)
        {
            if (txtBgHint == null) return;

            if (rbBgNone.IsChecked == true)
            {
                _selectedBgType = PhotoBackgroundType.None;
                txtBgHint.Text = "Pertahankan background asli tanpa perubahan";
            }
            else if (rbBgRed.IsChecked == true)
            {
                _selectedBgType = PhotoBackgroundType.Red;
                txtBgHint.Text = "Merah Pasfoto (#D81B1B) • Standar tahun kelahiran GANJIL (KTP/Ijazah)";
            }
            else if (rbBgBlue.IsChecked == true)
            {
                _selectedBgType = PhotoBackgroundType.Blue;
                txtBgHint.Text = "Biru Pasfoto (#0066CC) • Standar tahun kelahiran GENAP (KTP/Ijazah)";
            }
            else if (rbBgWhite.IsChecked == true)
            {
                _selectedBgType = PhotoBackgroundType.White;
                txtBgHint.Text = "Putih Formal (#FFFFFF) • Standar Visa, Paspor & Dokumen Resmi";
            }
            else if (rbBgTransparent.IsChecked == true)
            {
                _selectedBgType = PhotoBackgroundType.Transparent;
                txtBgHint.Text = "Transparan • Menghapus latar belakang (otomatis simpan format PNG)";
                if (rbFmtPng != null) rbFmtPng.IsChecked = true;
            }
            else if (rbBgCustom.IsChecked == true)
            {
                _selectedBgType = PhotoBackgroundType.Custom;
                UpdateCustomColorUi();
            }

            // Jika bukan Kustom, langsung jalankan atau perbarui preview AI.
            // Untuk Kustom, RbBgCustom_Click akan membuka dialog ColorPickerWindow lalu men-trigger preview.
            if (rbBgCustom?.IsChecked != true)
            {
                TriggerAiPreviewAsync();
            }
        }

        private void RbBgCustom_Click(object sender, RoutedEventArgs e)
        {
            OpenColorPickerDialog();
        }

        private void OpenColorPickerDialog()
        {
            _selectedBgType = PhotoBackgroundType.Custom;
            if (rbBgCustom != null && rbBgCustom.IsChecked != true)
            {
                rbBgCustom.IsChecked = true;
            }

            var picker = new ColorPickerWindow(_customBgHex)
            {
                Owner = this
            };

            if (picker.ShowDialog() == true)
            {
                _customBgHex = picker.SelectedHexColor;
            }

            UpdateCustomColorUi();
            TriggerAiPreviewAsync();
        }

        private void UpdateCustomColorUi()
        {
            try
            {
                var brush = (System.Windows.Media.Brush?)new System.Windows.Media.BrushConverter().ConvertFromString(_customBgHex);
                if (brush != null && rbBgCustom != null)
                {
                    rbBgCustom.Background = brush;
                }

                // Jamin kontras keterbacaan teks tombol sesuai standar WCAG AA
                if (txtRbBgCustom != null && !string.IsNullOrEmpty(_customBgHex))
                {
                    string cleanHex = _customBgHex.TrimStart('#');
                    if (cleanHex.Length == 6)
                    {
                        byte r = Convert.ToByte(cleanHex.Substring(0, 2), 16);
                        byte g = Convert.ToByte(cleanHex.Substring(2, 2), 16);
                        byte b = Convert.ToByte(cleanHex.Substring(4, 2), 16);
                        double luminance = 0.299 * r + 0.587 * g + 0.114 * b;
                        txtRbBgCustom.Foreground = luminance > 140
                            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 23, 42))
                            : System.Windows.Media.Brushes.White;
                    }
                }
            }
            catch { }

            if (txtBgHint != null)
            {
                txtBgHint.Text = $"Warna Kustom ({_customBgHex}) • Klik untuk ganti warna";
            }
        }

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

            // Live update preview kanvas seketika saat ukuran/crop pasfoto diubah
            TriggerAiPreviewAsync();
        }

        private void TxtCustomDims_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (rbPresetCustom?.IsChecked == true && _currentPreviewItem != null)
            {
                TriggerAiPreviewAsync();
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
                txtSpecSize.Foreground = isOver
                    ? (System.Windows.Media.Brush)FindResource("DangerBrush")
                    : (System.Windows.Media.Brush)FindResource("SuccessBrush");
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
                outputDir = Path.Combine(_sourceFolder, "edited");
            }

            long maxBytes = (long)sliderMaxSize.Value * 1024L;
            string format = rbFmtPng.IsChecked == true ? "PNG" : (rbFmtWebp.IsChecked == true ? "WEBP" : "JPEG");

            string preset = GetSelectedPresetKey();
            var (customW, customH) = GetCustomDimensions();
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
                    _selectedBgType,
                    _customBgHex,
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
            pnlBgButtons.IsEnabled = enabled;
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