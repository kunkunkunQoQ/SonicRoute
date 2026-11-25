# 独立窗口和弹层

WPF 文件位于冻结提交 `3d9f9e1083b228f666e30d97e3c8edfce5e6654a` 的 SonicRoute 目录，视频窗口位于 SonicRoute.AndroidAudio。原生文件位于 Native/Product/Views，菜单、输入和绘制基础位于 Native/Product/Ui。行号以冻结源和输入修复点 550eb93 为定位起点。P1 表示入口、数据提交或操作差异，P2 表示显著视觉或布局差异，P3 表示细节或待设备验证。源码相符的部分仍需运行验证。

| 窗口或控件 | WPF 1.36 位置 | 原生位置 | 视觉和几何 | 操作、可见条件和缺失 | 优先级与处理状态 |
|---|---|---|---|---|---|
| 快速面板外壳、标题和工具栏 | QuickPanelModernWindow.xaml:209,232,309，QuickPanelModernWindow.xaml.cs:316 | QuickPanelWindow.cs:158,549,576 | 双方宽 372，主面板 366，底栏贴底；标题、按钮和图标使用不同绘制方式 | 固定高度、失活关闭、调整位置和开关完整界面均有实现；原生额外支持固定按钮，保留。入场动画 160 ms | P2，源码审查 |
| 快速面板两种应用行 | QuickPanelModernWindow.xaml.cs:59,787，QuickPanelModernWindow.Rows.cs | QuickPanelWindow.cs:668,729,872 | 同一窗口按配置选择 38/54 DIP 折叠行；图标、名称、设备和滑块为手工布局 | 多输出选择、展开和独立音量已有入口；不同名称长度、强制系统默认、行刷新须实机复核 | P2，源码审查 |
| 默认定位和自定义坐标 | QuickPanelPosition.cs:22,38,49 | QuickPanelWindow.cs:2065 | WPF 默认主屏工作区右 12、下 10 DIP；原生选择托盘所在屏 | 原生自定义坐标另行工作区钳制；多缩放且屏幕移除时策略不同 | P2，处理结果见下表 |
| 输出设备选择、音量预设和右键 | QuickPanelModernWindow.xaml:240,253，QuickPanelModernWindow.xaml.cs:712,1032，App.TrayAudio.cs | QuickPanelWindow.cs:1409,1488，ComboPopupWindow.cs:161，PopupMenuWindow.cs:283 | WPF 项模板与原生文字行、图标列存在差异 | 选择、右键、滚轮、移出和捕获转移已有路径；本轮直接保留输入修复 | P2，源码审查 |
| 输出音量弹层 | QuickPanelModernWindow.xaml:18，QuickPanelModernWindow.OutputVolumes.cs，OutputVolumePopupLayout.cs:12 | OutputVolumesPopupWindow.cs:68,250,398 | 同为宽 366、间隔 6 的面板，原生以独立分层窗代替 WPF Popup | 可改默认端点、静音、单设备音量；工作区边缘和其他伴随窗占位需多屏实测 | P2，源码审查 |
| 托盘根菜单及级联菜单 | App.TrayMenu.cs:19,70，App.TrayAudio.cs，App.xaml 便利菜单样式 | TrayMenuController.cs:25,127,303，PopupMenuWindow.cs:295 | WPF 在指针处首选向下，原生初始菜单总是放在上方 | 打开主窗、面板、更新、音频、收藏、暂停和退出已有入口；菜单闲置资产与异步读取需随关闭清理 | P2，处理结果见下表 |
| OSD 音量和文字通知 | OsdService.cs:459,496,571,849，OsdPlacement.cs | OsdWindow.cs:53,250,393，HostViews.cs 的 ShowOsd/AnimateOsd | 宽 180–600、可选条、90 ms 缓动已有。字体缩放 WPF 0.7–2，原生截于 1.6；正文与音量行的列宽和对齐需校准 | 非激活、穿透、Esc/右键取消定位已有；渐显渐隐与系统动画设置需核对 | P2，处理结果见下表 |
| 主题取色器 | ThemeColorPickerWindow.cs:42,66,86,128,238,341，MainWindow.ThemeLayout.cs:80 | ColorPickerWindow.cs:56,131,262，ThemePageView.cs 的 OpenColorPicker | WPF 宽 720 双栏、96 色、横色相纵饱和度和垂直明度；原生宽 340 单栏、10 色与 HSV 方块 | 缺 HSL 三项、光谱方向键、确认前隔离和真正模态所有者 | P1，处理结果见下表 |
| 手机伴随窗和媒体 | PhoneAudioWindow.xaml:5,24,96,125,185，PhoneAudioWindow.xaml.cs，PhoneAudioWindow.AirPlay.cs | PhoneAudioWindowView.cs:77,564,995，AirPlayReceiverViewControl.cs | 235 DIP 窗、封面与媒体卡、搜索、应用行、底部设备区均有实现；原生媒体卡原高 136，对照 WPF 的 64+20 行 | 媒体前后、播放、定位、设备切换和接收状态需真机；AirPlay 模式和 Android 模式互斥 | P2，源码审查，硬件待验 |
| 跨设备拖放预览 | AppIconDragController.cs:196，AndroidAppDragController.cs，QuickPanelModernWindow.AndroidAudio.cs 的 InitializeAndroidDropOverlay / DropAndroid，PhoneAudioWindow.xaml.cs 的 DragAppRow / DropPhoneApp | DragIconWindow.cs，CrossDeviceDragHost.cs，QuickPanelWindow.UpdatePhoneDrop，PhoneAudioWindowView.FinishAppDropAsync | 原候选缺少独立跟随预览和手机应用投放到快捷面板的两段式接收区 | 鼠标拖动、取消、释放和设备身份处理见下表 | P1，已实施，硬件待验 |
| 虚拟屏窗口、状态遮罩及恢复入口 | VirtualDisplayPresentation.cs:35,89，VirtualDisplayOverlayWindow.cs:12；NativeVideoWindow.cs | PhonePreview.cs:550,598；SonicRoute.AndroidAudio/NativeVideoWindow.cs | 视频 HWND 可用；WPF 在手机端显示、恢复中或丢失时叠加提示卡 | 状态监听及音频生命周期已接入；须单独对照带回应用操作，不能根据视频可见判断状态窗通过 | P1，处理结果见下表，真机待验 |
| 文件选择、远端浏览、传输进度 | PhoneDevicePage.AndroidFiles.cs:59,110,182,301 | NativeDialogs.cs:15,47；PhoneDevicePageView.cs:1144,1253,1291；PhonePreview.cs:546 | WPF 与原生都有 Windows 本地选择器，远端浏览和进度位于设备页 | 传输归后端队列持有，关闭浏览视图仅取消目录读取；上传、下载失败和持续传输待真机验证 | P2，源码审查 |
| 历史、许可、导入预览及确认 | MainWindow.ExecutionHistory.cs:135，MainWindow.Licensing.cs:35，MainWindow.ImportPreview.cs，MainWindow.xaml.cs:1817,1944 | MainWindowShell.OpenHistory / ShowProjectLicense，ReadOnlyWindow.cs，SettingsPageView.cs 的导入，NativeDialogs.cs | 历史与许可从原候选的页面内视图或消息框恢复为独立的可选取文本窗口 | 具体命令与页面矩阵交叉核对，系统文件选择器与输入框交由 Windows 管理 | P2，源码审查，键盘与系统装饰待验 |
| 热键录入、文本输入及文件夹选择 | MainWindow.xaml.cs，MainWindow.AppIcons.cs:72，MainWindow.xaml.cs:1906,4253 | NativeDialogs.cs:12,47,106,109；InlineEditHost.cs | 系统输入法与剪贴板沿用 EDIT，系统框 Chrome 由 Windows 绘制 | 热键和编辑框焦点、Esc/Enter、九语言和输入法仍需系统验证 | P2，源码审查 |

## 原生还原结果

| 功能 | 实际实现与验证 | 保留偏差及用户实机验收 |
|---|---|---|
| 快速面板外壳、两种行样式及设备下拉 | 同尺寸浅色截图确认 WPF 页脚为透明描边，已还原三个操作按钮 112、120、76 DIP 与右侧间隔，调整标题行、主音量静音按钮和列表右边界。输出下拉的默认项使用 Ov.Default，显示设备图标。两种高度保留同一套点击/拖动逻辑。Tab 与 Enter/Space 复用控件 ID，方向键、PageUp/PageDown、Home/End 支持音量。源码审查通过 | 保留原生候选已有固定面板入口。设备图标使用原生图标集。滚动经过列表边缘时的光标、不同设备组合和动态峰值须用户实机验收 |
| 面板位置和渲染负担 | QuickPanelWindow.PositionWindow 恢复默认锚定主屏工作区。当前仅一行展开，MeasureExpandedRow 只为该行测量设备按钮；折叠行无须逐行生成输出和输入列表。带滚动条的第二次测量使用真实可用宽度。应用和端点保持身份匹配，点击路径的 CA1831 切片改用 span。源码审查通过 | 恢复自定义位置时沿用原生工作区钳制，面板位置超出当前可用显示器时与冻结版可有差异。多屏硬件待验 |
| 音量伴随窗、托盘和菜单 | 输出弹层支持焦点键盘选择与音量键。托盘根菜单在指针位置打开，菜单关闭后取消尚未完成的异步读取，断开事件与引用。先前的层级 Esc、稳定端点、捕获转移和失活路径保留。源码审查通过 | WPF Popup 与独立分层 HWND 存在焦点与屏缘定位差异。定向系统输入和使用的缩放见总报告 |
| OSD | OsdWindow 将通知、位置调整、实际绘制三个入口统一到冻结范围 0.7–2；内容区计入 1 DIP 边框。保留 90 ms 音量条和定时隐藏。源码审查通过 | 音量图标保留矢量实现，WPF 用系统 emoji；不同 DPI 的视觉栅格与系统动态效果策略须实测 |
| 取色器 | ColorPickerWindow 恢复宽 720 的双栏布局、12×8 基本色、横向色相/纵向饱和度光谱、垂直明度、七项 HEX/RGB/HSL 编辑、光谱方向键、编辑值同步和模态所有者。光谱位图按窗生成，色板文字预生成，确认后才发布选色动作。源码审查通过 | 框架系统标题栏改用现有原生层绘制，色谱位图采样与渐变端点存在栅格差别。IME 和九语言字宽待用户操作 |
| Android 媒体和 AirPlay | PhoneAudioWindowView 共用绘制与投放布局的 MediaCardHeight。两行内容分别为 64 和 20 DIP，含边框与内间距高 96 DIP。整机接收改在媒体控制行显示图标、状态和工具提示。进度条在按键期间预览，释放时验证曲目、媒体设备和可定位状态后提交。焦点转移、系统取消和 Esc 放弃键盘预览。源码审查通过 | 保留原生候选的整机接收入口，媒体行比 WPF 多一个按钮。实时封面、媒体源更换、AirPlay 连接态仍需硬件验证 |
| 跨设备拖放 | PC 应用图标可拖至手机目标。Android 应用可从伴随窗和快捷面板已有的接收行发起，在快捷面板选「仅电脑」或「同时播放」，回投伴随窗返回手机，在两窗外释放打开虚拟屏。快捷面板中的 AirPlay 来源可投向 Android 输出、AirPlay 断开区，或在面板外显示镜像。伴随窗底部 AirPlay 图标可拖至快捷面板的 Windows 默认输出区。全程复用已有音频、镜像与虚拟屏接口。两窗按鼠标移动更新 36 DIP 无焦点、穿透的跟随图标，距指针 14、16 物理像素。取消、失活、释放或销毁时释放图标。伴随窗单击选择，Enter 开始接收并保留手机声音，双击保留原生候选的打开虚拟屏入口。源码审查通过 | Android 和 AirPlay 拖动结果、断开竞态和设备端声音尚无硬件系统输入证据。触屏长按拖动仍待还原。用户实机验收待确认 |
| 虚拟屏恢复 | 新增 VirtualDisplayStatusWindow 对应 VirtualDisplayPresentation.cs:89。手机端使用、恢复中、画面暂停三个状态由已有 StateChanged 驱动，绑定视频客户区；只在提示存在时监听目标窗口线程的位置事件。可恢复状态通过同一鼠标或键盘入口调用现有 Restore，恢复中禁用；会话结束释放状态窗。视频、音频、传输核心沿用已有实现。源码审查通过 | 真实设备切换、视频窗口最小化、跨 DPI 和恢复失败反馈尚未验证 |
| 文件、窗口型设置及其他状态 | 文件传输与目录读取所有权分离，关闭视图保留队列。历史和许可均有独立文本窗口，设置页系统对话框、导入和自动化历史入口详见页面矩阵 | 文件夹对话框、许可证排版及高对比度系统装饰仍有差别。设备、IME 和文件传输操作尚未验证 |

快捷面板只在可见、开启峰值、且至少有一个可采样的本机应用时启动 50 ms 峰值计时器。空列表、仅有外部来源、全部静音或隐藏时停止采样。进程集合在行快照或静音状态变化时更新，峰值帧直接读取当前集合。160 ms 入场动画使用同一计时器，在完成时按采样条件切换或停止。

初始系统输入对照发现，从快捷面板输出设备下拉框按 Esc，WPF 返回面板，旧 Native 同时隐藏了面板。共享 ComboPopupWindow 的焦点归还与所有者关闭顺序由共享层处理。修复后的系统输入结果以最终收据为准。

上表不把源码存在的功能计作系统输入通过。编译、截图、系统输入和 Working Set / Private Bytes 以总报告所列最终构建及隔离运行收据为准。
