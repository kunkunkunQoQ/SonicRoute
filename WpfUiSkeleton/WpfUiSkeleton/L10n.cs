using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Data;

namespace WpfUiSkeleton
{
    /// <summary>
    /// 极简多语言单例。XAML 绑定方式：
    ///   {Binding [KeyName], Source={x:Static local:L10n.Instance}}
    /// 切换语言调用 SetLanguage("zh"/"en")，所有绑定自动刷新。
    /// </summary>
    public class L10n : INotifyPropertyChanged
    {
        public static L10n Instance { get; } = new();

        public event PropertyChangedEventHandler? PropertyChanged;

        private string _lang = "zh";
        public string CurrentLang => _lang;

        // 键 → 中/英 文案
        private static readonly Dictionary<string, (string zh, string en)> _dict = new()
        {
            ["App.Title"]     = ("UI 骨架", "UI Skeleton"),
            ["Nav.Overview"]  = ("概览", "Overview"),
            ["Nav.Settings"]  = ("设置", "Settings"),
            ["Ov.Hello"]      = ("这是一个可直接复用的 WPF UI 骨架", "A reusable WPF UI skeleton"),
            ["Ov.Desc"]       = ("无边框圆角窗口 · 主题切换 · 强调色 · 透明度 · 多语言", "Borderless rounded window · Themes · Accent · Opacity · i18n"),
            ["Set.Theme"]     = ("主题", "Theme"),
            ["Set.Mode"]      = ("外观模式", "Mode"),
            ["Set.AAuto"]     = ("跟随系统", "Auto"),
            ["Set.Light"]     = ("浅色", "Light"),
            ["Set.Dark"]      = ("深色", "Dark"),
            ["Set.Accent"]    = ("强调色", "Accent"),
            ["Set.Blue"]      = ("蓝色", "Blue"),
            ["Set.Green"]     = ("绿色", "Green"),
            ["Set.Pink"]      = ("粉色", "Pink"),
            ["Set.Opacity"]   = ("背景透明度", "Background opacity"),
            ["Set.Lang"]      = ("语言", "Language"),
            ["Set.Demo"]      = ("控件演示", "Control demo"),
            ["Set.CheckDemo"] = ("复选框示例", "Checkbox sample"),
            ["Set.SliderDemo"]= ("滑块示例", "Slider sample"),
        };

        public string this[string key]
        {
            get
            {
                if (_dict.TryGetValue(key, out var v))
                    return _lang == "en" ? v.en : v.zh;
                return key;
            }
        }

        public void SetLanguage(string lang)
        {
            if (_lang == lang) return;
            _lang = lang;
            // 通知所有绑定刷新（索引器用 Item[]）
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentLang)));
        }
    }
}
