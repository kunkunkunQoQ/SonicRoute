using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Size = System.Windows.Size;

namespace SonicRoute
{
    /// <summary>
    /// 只改变外层占位高度；子内容始终按稳定宽度和无限高度测量、按自然高度排列。
    /// 高度动画仍需父布局更新，但不会每帧让设备按钮重新换行或压缩文字。
    /// </summary>
    public sealed class AnimatedExpandHost : Decorator
    {
        public static readonly DependencyProperty RevealHeightProperty = DependencyProperty.Register(
            nameof(RevealHeight), typeof(double), typeof(AnimatedExpandHost),
            new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsMeasure,
                (d, e) => ((AnimatedExpandHost)d).ReportProgress((double)e.NewValue)));

        private readonly TranslateTransform _slide = new();
        private double _measureWidth = double.PositiveInfinity;
        private double _naturalHeight;
        private double _startHeight;
        private double _targetHeight;
        private Action<double>? _progress;
        private int _version;
        private bool _hasTarget;
        private bool _expanded;

        public AnimatedExpandHost()
        {
            ClipToBounds = true;
            Unloaded += (_, _) => FinishImmediately();
        }

        public double RevealHeight
        {
            get => (double)GetValue(RevealHeightProperty);
            set => SetValue(RevealHeightProperty, value);
        }

        public bool IsAnimating { get; private set; }
        public double NaturalHeight => _naturalHeight;

        protected override Size MeasureOverride(Size availableSize)
        {
            _measureWidth = availableSize.Width;
            if (Child == null || Child.Visibility == Visibility.Collapsed)
            {
                _naturalHeight = 0;
                return new Size();
            }
            Child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            _naturalHeight = Child.DesiredSize.Height;
            return new Size(Child.DesiredSize.Width,
                double.IsNaN(RevealHeight) ? _naturalHeight : Math.Max(0, RevealHeight));
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            Child?.Arrange(new Rect(0, 0, finalSize.Width, _naturalHeight));
            return finalSize;
        }

        public double MeasureNaturalHeight()
        {
            if (Child == null) return 0;
            double width = ActualWidth > 0 ? ActualWidth : _measureWidth;
            Child.Measure(new Size(Math.Max(0, width), double.PositiveInfinity));
            _naturalHeight = Child.DesiredSize.Height;
            return _naturalHeight;
        }

        public void SetExpanded(bool expand, bool animate = true, Action? completed = null,
            Action<double>? progress = null)
        {
            if (Child == null) return;
            bool wasVisible = Child.Visibility != Visibility.Collapsed;
            double height = wasVisible ? (double.IsNaN(RevealHeight) ? ActualHeight : RevealHeight) : 0;
            double opacity = wasVisible ? Child.Opacity : 0;
            double y = wasVisible ? _slide.Y : -4;
            int version = ++_version;
            _hasTarget = true;
            _expanded = expand;
            _progress = null;
            RemoveClocks();
            Child.RenderTransform = _slide;
            Child.Visibility = Visibility.Visible;
            Child.IsHitTestVisible = expand;
            double target = expand ? MeasureNaturalHeight() : 0;
            _startHeight = Math.Max(0, height);
            _targetHeight = target;

            if (!animate || !UiMotion.AnimationsEnabled || !IsVisible || target == height)
            {
                ApplyFinalState();
                progress?.Invoke(1);
                completed?.Invoke();
                return;
            }

            // 软件渲染只做短淡入/淡出，省去逐帧布局与位移。
            bool animateHeight = UiMotion.HardwareRendering;
            IsAnimating = true;
            RevealHeight = animateHeight || !expand ? _startHeight : target;
            Child.Opacity = opacity;
            _slide.Y = animateHeight ? y : 0;
            _progress = animateHeight ? progress : null;
            if (!animateHeight) progress?.Invoke(1);
            var duration = TimeSpan.FromMilliseconds(animateHeight ? (expand ? 160 : 120) : 100);
            IEasingFunction ease = expand
                ? new CubicEase { EasingMode = EasingMode.EaseOut }
                : new QuadraticEase { EasingMode = EasingMode.EaseInOut };
            var fade = new DoubleAnimation(opacity, expand ? 1 : 0, duration) { EasingFunction = ease };
            var heightAnimation = new DoubleAnimation(_startHeight, target, duration) { EasingFunction = ease };
            var completion = animateHeight ? heightAnimation : fade;
            completion.Completed += (_, _) =>
            {
                if (_version != version) return;
                var finalProgress = _progress;
                _progress = null;
                ApplyFinalState();
                finalProgress?.Invoke(1);
                completed?.Invoke();
            };
            Child.BeginAnimation(UIElement.OpacityProperty, fade);
            if (animateHeight)
            {
                _slide.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(y, expand ? 0 : -4, duration) { EasingFunction = ease });
                BeginAnimation(RevealHeightProperty, heightAnimation);
            }
        }

        public void StopProgress() => _progress = null;

        public void HoldCurrentState()
        {
            if (Child == null || !IsAnimating) return;
            double height = double.IsNaN(RevealHeight) ? ActualHeight : RevealHeight;
            double opacity = Child.Opacity, y = _slide.Y;
            ++_version;
            _progress = null;
            RemoveClocks();
            RevealHeight = height;
            Child.Opacity = opacity;
            _slide.Y = y;
        }

        public void FinishImmediately()
        {
            ++_version;
            _progress = null;
            if (_hasTarget) ApplyFinalState();
            else RemoveClocks();
        }

        private void ReportProgress(double height)
        {
            if (_progress == null || double.IsNaN(height)) return;
            double distance = _targetHeight - _startHeight;
            if (Math.Abs(distance) > 0.001)
                _progress(Math.Max(0, Math.Min(1, (height - _startHeight) / distance)));
        }

        private void RemoveClocks()
        {
            BeginAnimation(RevealHeightProperty, null);
            Child?.BeginAnimation(UIElement.OpacityProperty, null);
            _slide.BeginAnimation(TranslateTransform.YProperty, null);
            IsAnimating = false;
        }

        private void ApplyFinalState()
        {
            RemoveClocks();
            if (Child != null)
            {
                Child.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
                Child.Opacity = 1;
                Child.IsHitTestVisible = _expanded;
            }
            _slide.Y = 0;
            RevealHeight = double.NaN;
        }
    }
}
