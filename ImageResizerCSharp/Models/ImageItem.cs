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

    public class ImageItem : INotifyPropertyChanged
    {
        private bool _isChecked = true;
        private bool _isSelected = false;
        private bool _isOverLimit = false;
        private ProcessingStatus _status = ProcessingStatus.Pending;
        private string _statusMessage = string.Empty;
        private ImageSource? _thumbnail;
        private int _originalWidth;
        private int _originalHeight;
        private string _dimensionsText = "memuat...";

        public string FilePath { get; }
        public string FileName { get; }
        public long FileSize { get; }

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

        public ImageSource? Thumbnail
        {
            get => _thumbnail;
            set => SetField(ref _thumbnail, value);
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

        public string DimensionsText
        {
            get => _dimensionsText;
            set => SetField(ref _dimensionsText, value);
        }

        public string FormattedSize => FormatSize(FileSize);

        public string InfoText
        {
            get
            {
                var tag = IsOverLimit ? " • Di atas batas" : "";
                return $"{FormattedSize} • {DimensionsText}{tag}";
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
