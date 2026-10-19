using System;
using System.Globalization;
using System.Windows.Data;

namespace SonicRoute
{
    /// <summary>缺少应用图标时使用显示名首字；仅随名称绑定更新，不生成位图。</summary>
    public sealed class AppIconInitialConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var label = (value as string)?.TrimStart();
            return string.IsNullOrEmpty(label)
                ? "?"
                : StringInfo.GetNextTextElement(label).ToUpperInvariant();
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
