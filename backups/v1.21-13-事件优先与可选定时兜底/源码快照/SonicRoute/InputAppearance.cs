using System.Windows;

namespace SonicRoute
{
    // 搜索框和普通输入共用模板，装饰不接管输入或键盘事件。
    public static class InputAppearance
    {
        public static readonly DependencyProperty HasSearchIconProperty = DependencyProperty.RegisterAttached(
            "HasSearchIcon", typeof(bool), typeof(InputAppearance), new PropertyMetadata(false));
        public static bool GetHasSearchIcon(DependencyObject target) => (bool)target.GetValue(HasSearchIconProperty);
        public static void SetHasSearchIcon(DependencyObject target, bool value) => target.SetValue(HasSearchIconProperty, value);

        public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached(
            "Placeholder", typeof(string), typeof(InputAppearance), new PropertyMetadata(""));
        public static string GetPlaceholder(DependencyObject target) => (string)target.GetValue(PlaceholderProperty);
        public static void SetPlaceholder(DependencyObject target, string value) => target.SetValue(PlaceholderProperty, value);
    }
}
