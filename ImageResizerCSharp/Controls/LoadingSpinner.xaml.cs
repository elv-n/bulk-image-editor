using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ImageResizerCSharp.Controls
{
    public partial class LoadingSpinner : UserControl
    {
        public static readonly DependencyProperty SpinnerColorProperty =
            DependencyProperty.Register(
                nameof(SpinnerColor),
                typeof(Brush),
                typeof(LoadingSpinner),
                new PropertyMetadata(new SolidColorBrush(Color.FromRgb(0x4F, 0x6E, 0xF7))));

        public static readonly DependencyProperty TrackColorProperty =
            DependencyProperty.Register(
                nameof(TrackColor),
                typeof(Brush),
                typeof(LoadingSpinner),
                new PropertyMetadata(new SolidColorBrush(Color.FromArgb(0x33, 0x4F, 0x6E, 0xF7))));

        public static readonly DependencyProperty StrokeThicknessProperty =
            DependencyProperty.Register(
                nameof(StrokeThickness),
                typeof(double),
                typeof(LoadingSpinner),
                new PropertyMetadata(2.5));

        public Brush SpinnerColor
        {
            get => (Brush)GetValue(SpinnerColorProperty);
            set => SetValue(SpinnerColorProperty, value);
        }

        public Brush TrackColor
        {
            get => (Brush)GetValue(TrackColorProperty);
            set => SetValue(TrackColorProperty, value);
        }

        public double StrokeThickness
        {
            get => (double)GetValue(StrokeThicknessProperty);
            set => SetValue(StrokeThicknessProperty, value);
        }

        private Storyboard? _storyboard;

        public LoadingSpinner()
        {
            InitializeComponent();
            Loaded += LoadingSpinner_Loaded;
            IsVisibleChanged += LoadingSpinner_IsVisibleChanged;
        }

        private void LoadingSpinner_Loaded(object sender, RoutedEventArgs e)
        {
            SetupAndStartAnimation();
        }

        private void LoadingSpinner_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (IsVisible)
            {
                if (_storyboard == null)
                {
                    SetupAndStartAnimation();
                }
                else
                {
                    _storyboard.Resume();
                }
            }
            else
            {
                _storyboard?.Pause();
            }
        }

        private void SetupAndStartAnimation()
        {
            if (!IsVisible) return;
            if (_storyboard != null)
            {
                _storyboard.Resume();
                return;
            }

            var animation = new DoubleAnimation
            {
                From = 0,
                To = 360,
                Duration = TimeSpan.FromMilliseconds(850),
                RepeatBehavior = RepeatBehavior.Forever
            };

            _storyboard = new Storyboard();
            _storyboard.Children.Add(animation);
            Storyboard.SetTarget(animation, SpinnerRotate);
            Storyboard.SetTargetProperty(animation, new PropertyPath(RotateTransform.AngleProperty));
            _storyboard.Begin();
        }
    }
}
