using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Orientation = System.Windows.Controls.Orientation;
using Size = System.Windows.Size;

namespace SonicRoute
{
    /// <summary>
    /// 只改变外层占位尺寸；子内容始终按稳定非动画方向尺寸和无限动画方向尺寸测量、按自然尺寸排列。
    /// 展开/收起仍需父布局更新，但不会每帧让设备按钮重新换行或压缩文字。
    /// </summary>
    public sealed class AnimatedExpandHost : Decorator
    {
        public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(
            nameof(Orientation), typeof(Orientation), typeof(AnimatedExpandHost),
            new FrameworkPropertyMetadata(Orientation.Vertical, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty RevealHeightProperty = DependencyProperty.Register(
            nameof(RevealHeight), typeof(double), typeof(AnimatedExpandHost),
            new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsMeasure,
                (d, e) => ((AnimatedExpandHost)d).ReportProgress((double)e.NewValue)));

        public static readonly DependencyProperty RevealWidthProperty = DependencyProperty.Register(
            nameof(RevealWidth), typeof(double), typeof(AnimatedExpandHost),
            new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsMeasure,
                (d, e) => ((AnimatedExpandHost)d).ReportProgress((double)e.NewValue)));

        private readonly TranslateTransform _slide = new();
        private double _measureWidth = double.PositiveInfinity;
        private double _measureHeight = double.PositiveInfinity;
        private double _naturalHeight;
        private double _naturalWidth;
        private double _startLength;
        private double _targetLength;
        private Action<double>? _progress;
        private int _version;
        private bool _hasTarget;
        private bool _expanded;

        public AnimatedExpandHost()
        {
            ClipToBounds = true;
            Unloaded += (_, _) => FinishImmediately();
        }

        public Orientation Orientation
        {
            get => (Orientation)GetValue(OrientationProperty);
            set => SetValue(OrientationProperty, value);
        }

        public double RevealHeight
        {
            get => (double)GetValue(RevealHeightProperty);
            set => SetValue(RevealHeightProperty, value);
        }

        public double RevealWidth
        {
            get => (double)GetValue(RevealWidthProperty);
            set => SetValue(RevealWidthProperty, value);
        }

        public bool IsAnimating { get; private set; }
        public double NaturalHeight => _naturalHeight;
        public double NaturalWidth => _naturalWidth;

        internal bool IsAtRest(bool expanded) => _hasTarget && !IsAnimating && _expanded == expanded
            && Child != null && Child.Visibility == (expanded ? Visibility.Visible : Visibility.Collapsed)
            && double.IsNaN(Orientation == Orientation.Horizontal ? RevealWidth : RevealHeight)
            && Child.Opacity == 1 && _slide.X == 0 && _slide.Y == 0;

        protected override Size MeasureOverride(Size availableSize)
        {
            bool horizontal = Orientation == Orientation.Horizontal;
            if (horizontal) _measureHeight = availableSize.Height;
            else _measureWidth = availableSize.Width;

            if (Child == null || Child.Visibility == Visibility.Collapsed)
            {
                if (horizontal) _naturalWidth = 0;
                else _naturalHeight = 0;
                return new Size();
            }

            Child.Measure(horizontal
                ? new Size(double.PositiveInfinity, availableSize.Height)
                : new Size(availableSize.Width, double.PositiveInfinity));
            if (horizontal)
            {
                _naturalWidth = Child.DesiredSize.Width;
                return new Size(
                    double.IsNaN(RevealWidth) ? _naturalWidth : Math.Max(0, RevealWidth),
                    Child.DesiredSize.Height);
            }

            _naturalHeight = Child.DesiredSize.Height;
            return new Size(Child.DesiredSize.Width,
                double.IsNaN(RevealHeight) ? _naturalHeight : Math.Max(0, RevealHeight));
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            if (Orientation == Orientation.Horizontal)
            {
                if (Child != null)
                {
                    double width = _naturalWidth > 0 ? _naturalWidth : finalSize.Width;
                    double x = double.IsNaN(RevealWidth) ? 0 : finalSize.Width - width;
                    Child.Arrange(new Rect(x, 0, width, finalSize.Height));
                }
                return finalSize;
            }

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

        public double MeasureNaturalWidth()
        {
            if (Child == null) return 0;
            double height = ActualHeight > 0 ? ActualHeight : _measureHeight;
            Child.Measure(new Size(double.PositiveInfinity, Math.Max(0, height)));
            _naturalWidth = Child.DesiredSize.Width;
            return _naturalWidth;
        }

        public void SetExpanded(bool expand, bool animate = true, Action? completed = null,
            Action<double>? progress = null)
        {
            if (Child == null) return;
            bool horizontal = Orientation == Orientation.Horizontal;
            bool wasVisible = Child.Visibility != Visibility.Collapsed;
            double current = wasVisible
                ? (horizontal
                    ? (double.IsNaN(RevealWidth) ? ActualWidth : RevealWidth)
                    : (double.IsNaN(RevealHeight) ? ActualHeight : RevealHeight))
                : 0;
            double opacity = wasVisible ? Child.Opacity : 0;
            double y = horizontal ? 0 : (wasVisible ? _slide.Y : -4);
            int version = ++_version;
            _hasTarget = true;
            _expanded = expand;
            _progress = null;
            RemoveClocks();
            Child.RenderTransform = _slide;
            Child.Visibility = Visibility.Visible;
            Child.IsHitTestVisible = expand;
            double target = expand ? (horizontal ? MeasureNaturalWidth() : MeasureNaturalHeight()) : 0;
            _startLength = Math.Max(0, current);
            _targetLength = target;

            if (!animate || !UiMotion.AnimationsEnabled || !IsVisible || target == current)
            {
                ApplyFinalState();
                progress?.Invoke(1);
                completed?.Invoke();
                return;
            }

            // 软件渲染只做短淡入/淡出，省去逐帧布局与位移。
            bool animateDimension = UiMotion.HardwareRendering;
            IsAnimating = true;
            double initialLength = animateDimension || !expand ? _startLength : target;
            if (horizontal) RevealWidth = initialLength;
            else RevealHeight = initialLength;
            Child.Opacity = opacity;
            _slide.X = 0;
            _slide.Y = !horizontal && animateDimension ? y : 0;
            _progress = animateDimension ? progress : null;
            if (!animateDimension) progress?.Invoke(1);
            var duration = TimeSpan.FromMilliseconds(horizontal
                ? (animateDimension ? (expand ? 180 : 165) : 150)
                : (animateDimension ? (expand ? 160 : 120) : 100));
            IEasingFunction ease = expand
                ? new CubicEase { EasingMode = EasingMode.EaseOut }
                : new QuadraticEase { EasingMode = EasingMode.EaseInOut };
            var fade = new DoubleAnimation(opacity, expand ? 1 : 0, duration) { EasingFunction = ease };
            var lengthAnimation = animateDimension
                ? new DoubleAnimation(_startLength, target, duration) { EasingFunction = ease }
                : null;
            var completion = lengthAnimation ?? fade;
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
            if (lengthAnimation != null)
            {
                if (!horizontal)
                {
                    _slide.BeginAnimation(TranslateTransform.YProperty,
                        new DoubleAnimation(y, expand ? 0 : -4, duration) { EasingFunction = ease });
                }
                BeginAnimation(horizontal ? RevealWidthProperty : RevealHeightProperty, lengthAnimation);
            }
        }

        public void StopProgress() => _progress = null;

        public void HoldCurrentState()
        {
            if (Child == null || !IsAnimating) return;
            bool horizontal = Orientation == Orientation.Horizontal;
            double length = horizontal
                ? (double.IsNaN(RevealWidth) ? ActualWidth : RevealWidth)
                : (double.IsNaN(RevealHeight) ? ActualHeight : RevealHeight);
            double opacity = Child.Opacity, x = _slide.X, y = _slide.Y;
            ++_version;
            _progress = null;
            RemoveClocks();
            if (horizontal) RevealWidth = length;
            else RevealHeight = length;
            Child.Opacity = opacity;
            _slide.X = x;
            _slide.Y = y;
        }

        public void FinishImmediately()
        {
            ++_version;
            _progress = null;
            if (_hasTarget) ApplyFinalState();
            else RemoveClocks();
        }

        private void ReportProgress(double length)
        {
            if (_progress == null || double.IsNaN(length)) return;
            double distance = _targetLength - _startLength;
            if (Math.Abs(distance) > 0.001)
                _progress(Math.Max(0, Math.Min(1, (length - _startLength) / distance)));
        }

        private void RemoveClocks()
        {
            BeginAnimation(RevealHeightProperty, null);
            BeginAnimation(RevealWidthProperty, null);
            Child?.BeginAnimation(UIElement.OpacityProperty, null);
            _slide.BeginAnimation(TranslateTransform.XProperty, null);
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
            _slide.X = 0;
            _slide.Y = 0;
            RevealHeight = double.NaN;
            RevealWidth = double.NaN;
        }
    }
}
