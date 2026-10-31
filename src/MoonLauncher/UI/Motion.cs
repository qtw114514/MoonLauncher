using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MoonLauncher.UI
{
    /// <summary>统一页面/浮层动效；只操作 RenderTransform 与透明度，并尊重 Windows 动画偏好。</summary>
    public static class Motion
    {
        static readonly Duration Enter = new Duration(TimeSpan.FromMilliseconds(280));
        static readonly Duration Exit = new Duration(TimeSpan.FromMilliseconds(200));
        static readonly Duration Reduced = new Duration(TimeSpan.FromMilliseconds(80));
        static readonly CubicEase EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };

        public static bool ReducedMotion => !SystemParameters.ClientAreaAnimation;

        public static void PageEnter(FrameworkElement page)
        {
            if (page == null) return;
            var transform = new TranslateTransform();
            page.RenderTransform = transform;
            var storyboard = OpacityStoryboard(page, ReducedMotion ? Reduced : Enter);
            if (!ReducedMotion)
            {
                var move = new DoubleAnimation(8, 0, Enter) { EasingFunction = EaseOut };
                Storyboard.SetTarget(move, transform);
                Storyboard.SetTargetProperty(move, new PropertyPath(TranslateTransform.YProperty));
                storyboard.Children.Add(move);
            }
            storyboard.Begin(page, true);
        }

        public static void DialogEnter(FrameworkElement content)
        {
            if (content == null) return;
            var transform = new ScaleTransform();
            content.RenderTransformOrigin = new Point(0.5, 0.5);
            content.RenderTransform = transform;
            var storyboard = OpacityStoryboard(content, ReducedMotion ? Reduced : Enter);
            if (!ReducedMotion)
            {
                foreach (var property in new[] { ScaleTransform.ScaleXProperty, ScaleTransform.ScaleYProperty })
                {
                    var scale = new DoubleAnimation(0.98, 1, Enter) { EasingFunction = EaseOut };
                    Storyboard.SetTarget(scale, transform);
                    Storyboard.SetTargetProperty(scale, new PropertyPath(property));
                    storyboard.Children.Add(scale);
                }
            }
            storyboard.Begin(content, true);
        }

        public static void ToastEnter(FrameworkElement toast)
        {
            if (toast == null) return;
            var transform = new TranslateTransform();
            toast.RenderTransform = transform;
            var storyboard = OpacityStoryboard(toast, ReducedMotion ? Reduced : Enter);
            if (!ReducedMotion)
            {
                var move = new DoubleAnimation(8, 0, Enter) { EasingFunction = EaseOut };
                Storyboard.SetTarget(move, transform);
                Storyboard.SetTargetProperty(move, new PropertyPath(TranslateTransform.YProperty));
                storyboard.Children.Add(move);
            }
            storyboard.Begin(toast, true);
        }

        public static void ToastExit(FrameworkElement toast, Action completed)
        {
            if (toast == null) { completed?.Invoke(); return; }
            var storyboard = new Storyboard { FillBehavior = FillBehavior.Stop };
            var fade = new DoubleAnimation(1, 0, ReducedMotion ? Reduced : Exit);
            Storyboard.SetTarget(fade, toast);
            Storyboard.SetTargetProperty(fade, new PropertyPath(FrameworkElement.OpacityProperty));
            storyboard.Children.Add(fade);
            if (completed != null) storyboard.Completed += (s, e) => completed();
            storyboard.Begin(toast, true);
        }

        static Storyboard OpacityStoryboard(FrameworkElement target, Duration duration)
        {
            target.Opacity = 1;
            var storyboard = new Storyboard { FillBehavior = FillBehavior.Stop };
            var fade = new DoubleAnimation(0, 1, duration) { EasingFunction = EaseOut };
            Storyboard.SetTarget(fade, target);
            Storyboard.SetTargetProperty(fade, new PropertyPath(FrameworkElement.OpacityProperty));
            storyboard.Children.Add(fade);
            return storyboard;
        }
    }
}
