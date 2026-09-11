using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImageResizerCSharp.Models
{
    public enum ProcessingStatus
    {
        Pending,
        Processing,
        Done,
        Skipped,
        Error
    }

    public record NormalizedCropRect(double X, double Y, double Width, double Height)
    {
        public SixLabors.ImageSharp.Rectangle ToImageSharpRect(int imageWidth, int imageHeight)
        {
            int rx = (int)Math.Round(X * imageWidth);
            int ry = (int)Math.Round(Y * imageHeight);
            int rw = (int)Math.Round(Width * imageWidth);
            int rh = (int)Math.Round(Height * imageHeight);

            rx = Math.Clamp(rx, 0, Math.Max(0, imageWidth - 10));
            ry = Math.Clamp(ry, 0, Math.Max(0, imageHeight - 10));
            rw = Math.Clamp(rw, 10, imageWidth - rx);
            rh = Math.Clamp(rh, 10, imageHeight - ry);

            return new SixLabors.ImageSharp.Rectangle(rx, ry, rw, rh);
        }
    }

    public class ImageItem : INotifyPropertyChanged
    {
        private bool _isChecked = true;
        private bool _isSelected = false;
        private bool _isOverLimit = false;
        private ProcessingStatus _status = ProcessingStatus.Pending;
        private string _statusMessage = string.Empty;
        private ImageSource? _thumbnail;
        private BitmapSource? _baseThumbnail;
        private int _rotationAngle = 0;
        private int _originalWidth;
        private int _originalHeight;
        private string _dimensionsText = "memuat...";
        private NormalizedCropRect? _customCrop;

        public string FilePath { get; }
        public string FileName { get; }
        public long FileSize { get; }

        public NormalizedCropRect? CustomCrop
        {
            get => _customCrop;
            set
            {
                if (_customCrop != value)
                {
                    _customCrop = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasCustomCrop));
                    OnPropertyChanged(nameof(InfoText));
                }
            }
        }

        public bool HasCustomCrop => _customCrop != null;

        public bool IsChecked
        {
            get => _isChecked;
            set => SetField(ref _isChecked, value);
        }

        public bool IsSelected
        {
            get => _isSelected;
            set => SetField(ref _isSelected, value);
        }

        public bool IsOverLimit
        {
            get => _isOverLimit;
            set => SetField(ref _isOverLimit, value);
        }

        public ProcessingStatus Status
        {
            get => _status;
            set => SetField(ref _status, value);
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetField(ref _statusMessage, value);
        }

        public int RotationAngle
        {
            get => _rotationAngle;
            set
            {
                int normalized = ((value % 360) + 360) % 360;
                if (_rotationAngle != normalized)
                {
                    _rotationAngle = normalized;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasRotation));
                    OnPropertyChanged(nameof(EffectiveWidth));
                    OnPropertyChanged(nameof(EffectiveHeight));
                    OnPropertyChanged(nameof(DimensionsText));
                    OnPropertyChanged(nameof(InfoText));
                    UpdateRotatedThumbnail();
                }
            }
        }

        public bool HasRotation => _rotationAngle != 0;

        public ImageSource? Thumbnail
        {
            get => _thumbnail;
            set
            {
                _baseThumbnail = value as BitmapSource;
                UpdateRotatedThumbnail();
            }
        }

        private void UpdateRotatedThumbnail()
        {
            if (_baseThumbnail == null)
            {
                _thumbnail = null;
            }
            else if (_rotationAngle == 0)
            {
                _thumbnail = _baseThumbnail;
            }
            else
            {
                try
                {
                    var transformed = new TransformedBitmap();
                    transformed.BeginInit();
                    transformed.Source = _baseThumbnail;
                    transformed.Transform = new RotateTransform(_rotationAngle);
                    transformed.EndInit();
                    transformed.Freeze();
                    _thumbnail = transformed;
                }
                catch
                {
                    _thumbnail = _baseThumbnail;
                }
            }
            OnPropertyChanged(nameof(Thumbnail));
        }

        public int OriginalWidth
        {
            get => _originalWidth;
            set => SetField(ref _originalWidth, value);
        }

        public int OriginalHeight
        {
            get => _originalHeight;
            set => SetField(ref _originalHeight, value);
        }

        public int EffectiveWidth => (_rotationAngle == 90 || _rotationAngle == 270) ? _originalHeight : _originalWidth;
        public int EffectiveHeight => (_rotationAngle == 90 || _rotationAngle == 270) ? _originalWidth : _originalHeight;

        public string DimensionsText
        {
            get
            {
                if (_originalWidth <= 0 || _originalHeight <= 0) return _dimensionsText;
                return $"{EffectiveWidth}×{EffectiveHeight} px";
            }
            set => SetField(ref _dimensionsText, value);
        }

        public string FormattedSize => FormatSize(FileSize);

        public string InfoText
        {
            get
            {
                var tag = IsOverLimit ? " • Di atas batas" : "";
                var rotTag = HasRotation ? $" • ↻ {_rotationAngle}°" : "";
                var cropTag = HasCustomCrop ? " • ✂ Crop" : "";
                return $"{FormattedSize} • {DimensionsText}{rotTag}{cropTag}{tag}";
            }
        }

        public ImageItem(string filePath, int targetMaxSizeKb)
        {
            FilePath = filePath;
            FileName = Path.GetFileName(filePath);
            var fi = new FileInfo(filePath);
            FileSize = fi.Exists ? fi.Length : 0;
            UpdateThreshold(targetMaxSizeKb);
        }

        public void UpdateThreshold(int targetMaxSizeKb)
        {
            IsOverLimit = FileSize > (targetMaxSizeKb * 1024L);
            OnPropertyChanged(nameof(InfoText));
        }

        public void SetDimensions(int width, int height)
        {
            OriginalWidth = width;
            OriginalHeight = height;
            DimensionsText = $"{width}×{height} px";
            OnPropertyChanged(nameof(InfoText));
        }

        public static string FormatSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / (1024.0 * 1024.0):F2} MB";
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (System.Collections.Generic.EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}
