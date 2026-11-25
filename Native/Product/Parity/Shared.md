# WPF 1.36 共享控件差异矩阵

基准为 `3d9f9e1083b228f666e30d97e3c8edfce5e6654a` 中的 `SonicRoute/`。对照起点为旧原生提交 `550eb93` 的 `Native/Product/`。以下行号用于定位起点，最终实施与验收情况见本目录报告。

| 对象 | WPF 源定位 | Native 源定位 | 初始视觉与行为差异、缺少的能力 | 优先级与分类 |
|---|---|---|---|---|
| 主题和透明度 | ThemeService.cs 61–230 | Ui/UiTheme.cs 144–284，ApplicationHost.cs 112 | 深浅、强调色和派生对比色、背景 alpha 公式已对应。系统主题广播已接入。菜单打开期间主题快照与 WPF 动态资源不同 | P2，局部状态 |
| 导航及控件状态 | MainWindow.xaml 标题栏与 NavItem；App.xaml 32–58 | Views/MainWindowShell.cs 405–534，Ui/UiPrimitives.cs 181–265 | 原生裁剪与点击已配对。绘制按下态依赖 ActiveId，拖出后仍可能保持按下外观。多数自绘控件缺键盘焦点 | P1，共享 |
| 提示 | MainWindow.xaml、QuickPanelModernWindow.xaml 中各 ToolTip | Ui/UiPrimitives.cs 6–69，Ui/LayeredWindow.cs 689 | 命中项保存 tooltip 文本，但公共窗口没有呈现服务；图标按钮用途难以发现 | P1，入口缺失 |
| 文本和多语言 | MainWindow.xaml 根 FontFamily，L10n.cs 19–46，Resources/Lang/*.json | Ui/D2dRenderer.cs 75–157，Product.csproj EmbeddedResource | 同源 9 套翻译；DirectWrite 固定 en-US，部分脚本回退与语言不符。WPF 指定的复合字体与系统 DirectWrite 字体回退仍可能有不同的字形/基线 | P1，共享与原生差异 |
| 图标 | IconFactory.cs，AutomationIcons.cs，MainWindow.xaml 导航 Path | Ui/UiIcons.cs，Ui/D2dRenderer.cs 159–313 | 原生使用几何枚举、按需转路径；部分页面使用近似字形，需逐入口对照；图标不依赖图片解码计时器 | P2，局部 |
| 开关、单选、复选 | ToggleControls.xaml 3–22；App.xaml 361–437 | Ui/UiPrimitives.cs 259–313 | 开关轨道 34×20，圆钮 16 已对应。原生禁用圆钮透明度及悬停、单选描边/颜色、复选尺寸/间距与模板不同 | P2，共享 |
| 下拉 | App.xaml 217–329 | Ui/ComboPopupWindow.cs 13–142，Ui/UiPrimitives.cs DrawComboBox | 高亮/选中底色、菜单最大高度、行间距和字重不同。已有键盘方向键、搜索和捕获取消 | P1，共享 |
| 上下文菜单 | App.xaml 120–183，ConvenienceMenus.cs | Ui/PopupMenuWindow.cs 35–49、165–264 | 自绘圆角、边距、勾选位置及禁用对比度不同；已有子菜单和键盘导航；工作区边缘需检查 | P2，共享 |
| 滚动 | ScrollControls.xaml 15–128 | Ui/UiPrimitives.cs 78–168 | 原生 8 DIP 轨道与 WPF 14 DIP 不同，点击轨道跳到鼠标位置，WPF 按页。滚轮和拖动采集已分开 | P1，共享 |
| 文本框与 IME | App.xaml 501–540，InputAppearance.cs | Ui/InlineEditHost.cs 64–135、271–315 | 真正 Unicode EDIT，系统选区和剪贴板；已有 IME 组合保护。边框、输入焦点跳转及输入法实机行为需分别核对 | P1，共享与原生差异 |
| 滑块、峰值、设备行 | App.xaml 441–500，QuickPanelModernWindow.xaml RowSlider | Ui/UiPrimitives.cs DrawVolumeSlider，Views/QuickPanelWindow.cs | 共享统一拖动坐标与稳定身份已保留；模板阴影、特定场景的滑块配色及禁用态有差异 | P2，局部与原生差异 |
| 光栅化与生命周期 | WindowUiLifetime.cs，MainWindow.PageLifetime.cs | Ui/D2dRenderer.cs 27–31、420–427、665–707，Ui/LayeredWindow.cs 303、450–509 | 分层窗口按需绘制，逐窗口画刷、位图、DIB；字体及几何有共享缓存。位图版本变化时旧 FIFO 索引会累积，文本测量反复创建相同布局 | P1，共享生命周期 |
| 配置与业务 | MainWindow.SettingsSave.cs，QuickPanelModernWindow.PlaybackSelection.cs | Views/HostViews.cs，Views/SettingsPageView.cs，Views/QuickPanelWindow.cs | 保留现有配置与 Ctrl/Shift 路由链路；UI 与业务生命周期须分别验证 | P1，已有路径 |
