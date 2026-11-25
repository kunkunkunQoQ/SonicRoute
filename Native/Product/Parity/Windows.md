# 独立窗口和弹层

WPF 文件位于冻结提交 `3d9f9e1083b228f666e30d97e3c8edfce5e6654a` 的 SonicRoute 目录，视频窗口位于 SonicRoute.AndroidAudio。原生文件位于 Native/Product/Views，菜单、输入和绘制基础位于 Native/Product/Ui。行号以冻结源和输入修复点 550eb93 为定位起点。P1 表示入口、数据提交或操作差异，P2 表示显著视觉或布局差异，P3 表示细节或待设备验证。源码相符的部分仍需运行验证。

| 窗口或控件 | WPF 1.36 位置 | 原生位置 | 视觉和几何 | 操作、可见条件和缺失 | 优先级与处理状态 |
|---|---|---|---|---|---|
| 快速面板外壳、标题和工具栏 | QuickPanelModernWindow.xaml:209,232,309，QuickPanelModernWindow.xaml.cs:316 | QuickPanelWindow.cs:158,549,576 | 双方宽 372，主面板 366，底栏贴底；标题、按钮和图标使用不同绘制方式 | 固定高度、失活关闭、调整位置和开关完整界面均有实现；原生额外支持固定按钮，保留。入场动画 160 ms | P2，源码审查 |
| 快速面板两种应用行 | QuickPanelModernWindow.xaml.cs:59,787，QuickPanelModernWindow.Rows.cs | QuickPanelWindow.cs:668,729,872 | 同一窗口按配置选择 38/54 DIP 折叠行；图标、名称、设备和滑块为手工布局 | 多输出选择、展开和独立音量已有入口；不同名称长度、强制系统默认、行刷新须实机复核 | P2，源码审查 |
| 默认定位和自定义坐标 | QuickPanelPosition.cs:22,38,49 | QuickPanelWindow.cs:2065 | WPF 默认主屏工作区右 12、下 10 DIP；原生选择托盘所在屏 | 原生自定义坐标另行工作区钳制；多缩放且屏幕移除时策略不同 | P2，待修 |
| 输出设备选择、音量预设和右键 | QuickPanelModernWindow.xaml:240,253，QuickPanelModernWindow.xaml.cs:712,1032，App.TrayAudio.cs | QuickPanelWindow.cs:1409,1488，ComboPopupWindow.cs:161，PopupMenuWindow.cs:283 | WPF 项模板与原生文字行、图标列存在差异 | 选择、右键、滚轮、移出和捕获转移已有路径；本轮直接保留输入修复 | P2，源码审查 |
| 输出音量弹层 | QuickPanelModernWindow.xaml:18，QuickPanelModernWindow.OutputVolumes.cs，OutputVolumePopupLayout.cs:12 | OutputVolumesPopupWindow.cs:68,250,398 | 同为宽 366、间隔 6 的面板，原生以独立分层窗代替 WPF Popup | 可改默认端点、静音、单设备音量；工作区边缘和其他伴随窗占位需多屏实测 | P2，源码审查 |
| 托盘根菜单及级联菜单 | App.TrayMenu.cs:19,70，App.TrayAudio.cs，App.xaml 便利菜单样式 | TrayMenuController.cs:25,127,303，PopupMenuWindow.cs:295 | WPF 在指针处首选向下，原生初始菜单总是放在上方 | 打开主窗、面板、更新、音频、收藏、暂停和退出已有入口；菜单闲置资产与异步读取需随关闭清理 | P2，待修 |
| OSD 音量和文字通知 | OsdService.cs:459,496,571,849，OsdPlacement.cs | OsdWindow.cs:53,250,393，HostViews.cs 的 ShowOsd/AnimateOsd | 宽 180–600、可选条、90 ms 缓动已有。字体缩放 WPF 0.7–2，原生截于 1.6；正文与音量行的列宽和对齐需校准 | 非激活、穿透、Esc/右键取消定位已有；渐显渐隐与系统动画设置需核对 | P2，待修 |
| 主题取色器 | ThemeColorPickerWindow.cs:42,66,86,128,238,341，MainWindow.ThemeLayout.cs:80 | ColorPickerWindow.cs:56,131,262，ThemePageView.cs 的 OpenColorPicker | WPF 宽 720 双栏、96 色、横色相纵饱和度和垂直明度；原生宽 340 单栏、10 色与 HSV 方块 | 缺 HSL 三项、光谱方向键、确认前隔离和真正模态所有者 | P1，待修 |
| 手机伴随窗和媒体 | PhoneAudioWindow.xaml:5,24,96,125,185，PhoneAudioWindow.xaml.cs，PhoneAudioWindow.AirPlay.cs | PhoneAudioWindowView.cs:77,564,995，AirPlayReceiverViewControl.cs | 235 DIP 窗、封面与媒体卡、搜索、应用行、底部设备区均有实现；原生媒体卡原高 136，对照 WPF 的 64+20 行 | 媒体前后、播放、定位、设备切换和接收状态需真机；AirPlay 模式和 Android 模式互斥 | P2，源码审查，硬件待验 |
| 跨设备拖放预览 | AppIconDragController.cs:196，PhoneAudioWindow.xaml:8 | CrossDeviceDragHost.cs，QuickPanelWindow.cs 的跨设备拖动分支，PhoneAudioWindowView.cs 的投放区 | WPF 有浮动预览窗口，原生绘制接收区并跟踪目标 | 按下稳定目标、取消及提交已有实现；独立跟随图标存在差异，需双设备验证 | P3，部分等价 |
| 虚拟屏窗口、状态遮罩及恢复入口 | VirtualDisplayPresentation.cs:35,89，VirtualDisplayOverlayWindow.cs:12；NativeVideoWindow.cs | PhonePreview.cs:550,598；SonicRoute.AndroidAudio/NativeVideoWindow.cs | 视频 HWND 可用；WPF 在手机端显示、恢复中或丢失时叠加提示卡 | 状态监听及音频生命周期已接入；须单独对照带回应用操作，不能根据视频可见判断状态窗通过 | P1，审查中，真机待验 |
| 文件选择、远端浏览、传输进度 | PhoneDevicePage.AndroidFiles.cs:59,110,182,301 | NativeDialogs.cs:15,47；PhoneDevicePageView.cs:1144,1253,1291；PhonePreview.cs:546 | WPF 与原生都有 Windows 本地选择器，远端浏览和进度位于设备页 | 传输归后端队列持有，关闭浏览视图仅取消目录读取；上传、下载失败和持续传输待真机验证 | P2，源码审查 |
| 历史、许可、导入预览及确认 | MainWindow.ExecutionHistory.cs:135，MainWindow.Licensing.cs:35，MainWindow.ImportPreview.cs，MainWindow.xaml.cs:1817,1944 | AutomationPageView.cs 的历史视图，SettingsPageView.cs 的许可/导入，NativeDialogs.cs:106,112 | 原生历史位于页面内，许可和系统提示使用原生对话框，窗体外观不同 | 具体命令与页面矩阵交叉核对，系统文件选择器与输入框交由 Windows 管理 | P2，部分等价 |
| 热键录入、文本输入及文件夹选择 | MainWindow.xaml.cs，MainWindow.AppIcons.cs:72，MainWindow.xaml.cs:1906,4253 | NativeDialogs.cs:12,47,106,109；InlineEditHost.cs | 系统输入法与剪贴板沿用 EDIT，系统框 Chrome 由 Windows 绘制 | 热键和编辑框焦点、Esc/Enter、九语言和输入法仍需系统验证 | P2，源码审查 |
