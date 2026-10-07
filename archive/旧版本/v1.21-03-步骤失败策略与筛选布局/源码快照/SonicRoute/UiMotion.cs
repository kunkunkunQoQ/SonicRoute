using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SonicRoute
{
    internal static class UiMotion
    {
        internal static bool AnimationsEnabled => SystemParameters.ClientAreaAnimation;
        internal static bool HardwareRendering => (RenderCapability.Tier >> 16) > 0;

        internal static void RotateArrow(RotateTransform arrow, bool expanded, bool animate)
        {
            double current = arrow.Angle;
            double target = expanded ? 180 : 0;
            arrow.BeginAnimation(RotateTransform.AngleProperty, null);
            arrow.Angle = target;
            if (!animate || !AnimationsEnabled || current == target) return;
            var animation = new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(120))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            animation.Completed += (_, _) => arrow.BeginAnimation(RotateTransform.AngleProperty, null);
            arrow.BeginAnimation(RotateTransform.AngleProperty, animation);
        }
    }

    /// <summary>定位完成后播放一次入场；结束/关闭移除动画时钟，保持固定窗口坐标。</summary>
    internal sealed class PanelEntranceMotion
    {
        private readonly FrameworkElement _surface;
        private readonly TranslateTransform _slide;
        private int _version;

        internal PanelEntranceMotion(FrameworkElement surface, TranslateTransform slide)
        {
            _surface = surface;
            _slide = slide;
        }

        internal bool IsRunning { get; private set; }

        internal void Prepare()
        {
            Stop();
            _surface.Opacity = UiMotion.AnimationsEnabled ? 0 : 1;
            _slide.Y = UiMotion.AnimationsEnabled && UiMotion.HardwareRendering ? 8 : 0;
        }

        internal void Play()
        {
            if (IsRunning) return;
            if (!UiMotion.AnimationsEnabled) { Stop(); return; }
            int version = ++_version;
            IsRunning = true;
            var duration = TimeSpan.FromMilliseconds(UiMotion.HardwareRendering ? 160 : 100);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var fade = new DoubleAnimation(_surface.Opacity, 1, duration) { EasingFunction = ease };
            fade.Completed += (_, _) => { if (_version == version) Stop(); };
            _surface.BeginAnimation(UIElement.OpacityProperty, fade);
            if (UiMotion.HardwareRendering)
                _slide.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(_slide.Y, 0, duration) { EasingFunction = ease });
        }

        internal void Stop()
        {
            ++_version;
            _surface.BeginAnimation(UIElement.OpacityProperty, null);
            _slide.BeginAnimation(TranslateTransform.YProperty, null);
            _surface.Opacity = 1;
            _slide.Y = 0;
            IsRunning = false;
        }
    }
}
