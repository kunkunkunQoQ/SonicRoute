# R-10 旧原生 UI 复用与清理

审查起点为旧产品修复分支的 R-09 检查点 `f6915eb`。覆盖 `Native/Product/Ui`、`Native/Product/Views` 及这些界面直接调用的宿主服务。表内状态描述源码，构建、发布体积与实机证据由本轮交付收据分别记录。

## B · 复用审查

| 能力 | 检查位置 | 实施、原有差异及收益 |
| --- | --- | --- |
| 带说明的设置开关、行高及排版 | `SettingsPageView`、`ExperimentalPageView`、`UiPrimitives` | 普通及实验设置共用 `MeasureWrappedTextHeight`、`MeasureSettingsSwitchHeight`、`DrawSettingsSwitchWithHint`。两页按可用宽度度量标题与说明，保留 R-09 的 32 DIP 文本偏移、两段间距、行级命中和开关命中。普通设置无需向实验设置公开页面方法。 |
| 滚动内容中的可点击区域 | 普通设置、实验设置、自动化步骤与编辑区 | 三个相同的局部裁剪实现改用 `UiHitMap.Intersect`。无交集的宽高均由 `UiRect.IsEmpty` 排除。保留页面动作映射、虚拟视图的外层裁剪、可见内容的按下目标和滑块比例。 |
| 配置提交与多窗同步 | `IViewPage.cs` 中的宿主扩展、应用别名/批处理、普通/实验设置、主题与面板选项 | `CommitSettings(notify: true)` 在现有事务成功后统一通知。写入异常经原回滚、通知与重绘处理。主题切换、快捷键注册、自动化规则存储、拖动结束与取消草稿继续使用各自原有时序。 |
| 输出端点身份、范围选择及主输出 | `AppsPageView`、`QuickPanelWindow`、`PlaybackSelection` | 两个入口共享按端点 ID 忽略大小写的 Ctrl/Shift 选区、保留暂不可见勾选项、主输出选择，以及 Full 外部会话的多输出能力和手机源降级顺序。各入口仍负责自己的锚点、回执、旧请求抑制、重绘及失败回滚。 |
| 品牌、应用图标及媒体封面 | `NativeAppIconService`、应用页、快捷面板、手机页、伴随窗口、AirPlay 接收控件、`UiBitmap` | 显示路径统一执行 WIC 定尺解码和一次 BGRA 预乘，透明像素归零、半透明整除及 48/64/72/96 像素目标沿用原值。Win32 图标的遮罩恢复后复用同一预乘。应用图标导入随后要编码 PNG，继续使用原始直通道；避免写入半透明像素失真。 |
| 手机应用图标生命周期 | `PhoneAppIconCache`、`PhoneDevicePageView`、`PhoneAudioWindowView` | 同一份包名查找、在途解码去重、失败结果和先进先出淘汰，页面限 96 个，伴随窗限 64 个。两者各自持有缓存，在切换配置、页面退出或窗口隐藏/销毁后废弃旧代任务。修复旧伴随窗到达容量后，已淘汰包名仍留在在途集合、无法重新解码的问题。只在请求图标时使用现有线程池。 |
| 数字、文本、搜索、密码、只读说明与颜色通道编辑 | `UiPrimitives.DrawTextBox`、`InlineEditHost.Open`、主窗、伴随窗口、取色器 | 初始屏幕位置、DIP 至像素转换移入原 EDIT 宿主。三个调用者分别传入原有字号、最小尺寸、颜色、选中和编辑策略。每次布局仍按裁剪后的文本区定位。保留 IME、焦点提交、Esc、剪贴板和只读多行滚动。 |
| 下拉选择、级联菜单与提示 | `ComboPopupWindow`、`PopupMenuWindow`、`WindowTooltip`、主窗/快捷面板/手机窗 | 三个宿主复用同一单选下拉销毁入口，在同步关闭前解除所有者引用。快捷面板仍先解除失活回调。弹层共同使用 `LayeredWindow` 的像素锚点和工作区 DPI。下拉保留上下翻转、4px 边缘与选中项滚动，托盘菜单保留级联翻边、父子焦点和按命令关闭。 |
| 基础按钮、卡片、状态 | `UiPrimitives.DrawAccentButton/DrawSecondaryButton/DrawGhostIconButton/DrawCard/DrawSwitch/DrawCheckBox`、`UiThemePalette`、`UiIcons`、`D2dRenderer` | 保留已有图标路径、调色板、DirectWrite 文本布局及渲染目标缓存。选项卡片的整行/选区与紧凑按钮的击区不同，保留各自布局。文字量、禁用和警告由业务页面给出。 |
| 热键、滑块、滚动、音频状态 | `HotkeyBinding`、`UiPrimitives.DrawVolumeSlider/SliderValueFromX`、`UiScrollState`、`ApplicationHost` 与输出音量弹层 | 热键页与规则页继续共用按钮绘制及键鼠编码，分别处理全局注册与规则草稿。快捷面板/输出弹层的实时音量、自动化草稿步进、主题拖动结束写盘沿用现有分派。滚动、捕获丢失和系统取消走原共享基础设施。 |
| 设备名称与列表 | `AppsPageView`、`QuickPanelWindow`、`TrayMenuController`、`OutputVolumesPopupWindow`、`PhoneAppPresentation` | 芯片和快捷面板共用 `ShortDeviceName`。完整设备名、已保存别名、默认输入/输出和手机源标签各有优先顺序；托盘使用筛选后的真实端点，Android/AirPlay 使用会话身份。保留入口差异。 |
| 可见性与资源归属 | `HostViews`、`MainWindowShell`、经典/简洁窗口、`LayeredWindow` | 保留按需创建、显示时订阅、DPI 及 HWND 专属渲染资源的释放。复用新增数据只由已创建的手机视图持有；托盘冷启动不创建图形对象，后台功能无新增定时器。 |

各处保留 R-09 普通/实验设置分组、图标侧栏、经典/简洁面板、Android 媒体与 AirPlay 行为差异。本节没有通过行数推断包大小、CPU 或内存变化。
