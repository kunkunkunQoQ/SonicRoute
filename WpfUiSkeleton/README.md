# WPF UI Skeleton

一个可直接复用的 **WPF (.NET 8) 现代 UI 骨架**，无边框圆角窗口 + 主题切换 + 强调色 + 背景透明度 + 多语言，零第三方依赖。

从 SonicRoute（音跃）项目抽离，可直接复制到新项目使用。

## 功能

- **无边框圆角窗口**：自定义标题栏（拖动 / 最小化 / 最大化 / 关闭），圆角 + 阴影
- **主题系统**：跟随系统 / 浅色 / 深色，运行时即时切换
- **强调色**：蓝 / 绿 / 粉 / 自定义 `#RRGGBB`，所有控件统一跟随
- **背景透明度**：60%–100% 可调，窗口与卡片整体透明
- **多语言**：中 / 英，切换即时生效（可扩展更多语言）
- **自定义控件样式**：圆角 ComboBox / CheckBox / RadioButton / Slider / TextBox / Button，消除系统蓝，hover/选中统一强调色
- **左侧导航 + 多页面**：RadioButton 导航，Grid 叠加 Visibility 切换（轻量，无 Frame 导航历史）

## 项目结构

```
WpfUiSkeleton/
├── WpfUiSkeleton.sln
└── WpfUiSkeleton/
    ├── WpfUiSkeleton.csproj   # .NET 8 WPF
    ├── App.xaml               # 全局 Theme.* 资源 + 所有控件样式
    ├── App.xaml.cs
    ├── ThemeService.cs        # 主题/强调色/透明度（运行时替换资源）
    ├── L10n.cs                # 多语言单例（INotifyPropertyChanged）
    ├── MainWindow.xaml        # 无边框窗口 + 导航 + 两页面示例
    └── MainWindow.xaml.cs     # 标题栏/导航/主题切换逻辑
```

## 快速开始

```bash
cd WpfUiSkeleton
dotnet build
dotnet run --project WpfUiSkeleton
```

或用 Visual Studio 打开 `WpfUiSkeleton.sln`，F5 运行。

## 核心实现要点

### 1. 无边框窗口
```xml
<Window WindowStyle="None" AllowsTransparency="True" Background="Transparent">
    <Border CornerRadius="10" Background="{DynamicResource Theme.WindowBgAlpha}">
        <Border.Effect><DropShadowEffect .../></Border.Effect>
    </Border>
</Window>
```
最大化不能用 `WindowState.Maximized`（会溢出），手动设 `Left/Top/Width/Height` 并把圆角归零。

### 2. 主题即时切换
- `App.xaml` 定义 `Theme.*` 画刷，XAML 全部用 `DynamicResource` 引用
- `ThemeService.Apply()` 运行时替换 `Application.Current.Resources[key]`，DynamicResource 自动监听变化

### 3. 透明度
`Theme.WindowBgAlpha` / `SurfaceBgAlpha` 带 alpha 通道，`ApplyBackgroundOpacity(percent)` 按百分比计算。窗口根 Border 绑 `WindowBgAlpha`，卡片绑 `SurfaceBgAlpha`。

### 4. 多语言
```xml
<TextBlock Text="{Binding [KeyName], Source={x:Static local:L10n.Instance}}"/>
```
`L10n.SetLanguage()` 触发 `PropertyChangedEventArgs(Binding.IndexerName)`，所有绑定刷新。

## 扩展到你的项目

1. 复制 `App.xaml`（资源 + 样式）、`ThemeService.cs`、`L10n.cs` 到你的项目
2. 窗口设 `WindowStyle=None + AllowsTransparency=True`，根用 Border
3. 标题栏用 `DragMove()` + 手动最大化
4. 导航用 `RadioButton GroupName` + 多 Grid `Visibility` 切换
5. 所有颜色用 `DynamicResource Theme.*`

## 自定义

- **加新颜色**：在 `App.xaml` 加 `SolidColorBrush x:Key="Theme.XXX"`，在 `ThemeService.Apply()` 里 Set 对应色值
- **加新语言**：在 `L10n._dict` 加键值，`SetLanguage` 支持新 lang code
- **加新页面**：XAML 加一个 `Grid x:Name="XxxPage"`，导航加 RadioButton，`Nav_Checked` 里加 Visibility 切换
- **自定义强调色**：`ThemeService.Apply(dark, "#RRGGBB")` 直接传十六进制色值
