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
| 下拉选择、级联菜单与提示 | `ComboPopupWindow`、`PopupMenuWindow`、`WindowTooltip`、主窗/快捷面板/手机窗 | 三个宿主复用同一单选下拉销毁入口，在同步关闭前解除所有者引用。快捷面板仍先解除失活回调。弹层共同使用 `LayeredWindow` 的像素锚点和工作区 DPI。下拉保留上下翻转与选中项滚动，最终按原控件两条屏幕像素边定位，超出工作区时才收窄或平移，托盘菜单保留级联翻边、父子焦点和按命令关闭。 |
| 基础按钮、卡片、状态 | `UiPrimitives.DrawAccentButton/DrawSecondaryButton/DrawGhostIconButton/DrawCard/DrawSwitch/DrawCheckBox`、`UiThemePalette`、`UiIcons`、`D2dRenderer` | 保留已有图标路径、调色板、DirectWrite 文本布局及渲染目标缓存。选项卡片的整行/选区与紧凑按钮的击区不同，保留各自布局。文字量、禁用和警告由业务页面给出。 |
| 热键、滑块、滚动、音频状态 | `HotkeyBinding`、`UiPrimitives.DrawVolumeSlider/SliderValueFromX`、`UiScrollState`、`ApplicationHost` 与输出音量弹层 | 热键页与规则页继续共用按钮绘制及键鼠编码，分别处理全局注册与规则草稿。快捷面板/输出弹层的实时音量、自动化草稿步进、主题拖动结束写盘沿用现有分派。滚动、捕获丢失和系统取消走原共享基础设施。 |
| 设备名称与列表 | `AppsPageView`、`QuickPanelWindow`、`TrayMenuController`、`OutputVolumesPopupWindow`、`PhoneAppPresentation` | 芯片和快捷面板共用 `ShortDeviceName`。完整设备名、已保存别名、默认输入/输出和手机源标签各有优先顺序；托盘使用筛选后的真实端点，Android/AirPlay 使用会话身份。保留入口差异。 |
| 可见性与资源归属 | `HostViews`、`MainWindowShell`、经典/简洁窗口、`LayeredWindow` | 保留按需创建、显示时订阅、DPI 及 HWND 专属渲染资源的释放。复用新增数据只由已创建的手机视图持有；托盘冷启动不创建图形对象，后台功能无新增定时器。 |

各处保留 R-09 普通/实验设置分组、图标侧栏、经典/简洁面板、Android 媒体与 AirPlay 行为差异。本节没有通过行数推断包大小、CPU 或内存变化。

## C · 已清理的不可达实现

取两种 R-09 正式候选发布的预 AOT 程序集，路径为相应版型的 `r09-release/build/{Full,Lite}-x64/bin/Product/release_win-x64/SonicRoute.dll`。用 `System.Reflection.Metadata` 逐条解码所有方法体，遍历 Full 的 5,890 个方法定义与 16,930 个本程序集方法引用，Lite 的 5,024 个定义与 13,514 个引用。计数包括普通调用、委托/函数指针取址、方法元数据标记与泛型方法规格。另查成员引用、方法名字符串、各入口调用代码、界面创建方式及产品项目的 AOT 保留与 JSON 元数据。以下项目都在两版分别核对，手机专有类型只出现于 Full。

| 清理位置 | 双版证据与继续使用的入口 |
| --- | --- |
| `NativeDialogs.Input/RecordHotkey` 和旧模态 EDIT | 两个入口没有引用。私有 `Run` 只由这两个入口调用。其 `DialogProc` 的唯一地址引用来自 `Run` 内部的窗口类注册；无导出入口。删除该独立消息循环、对话框状态、专用按键解析及仅为该窗口声明的互操作方法。保留 `Open/Save/OpenMultiple/BrowseFolder` 的 Win32/COM、确认/警告/错误提示和现行 `InlineEditHost/HotkeyBinding`。 |
| `D2dTarget.FillHueSpectrumRect`、`UiColor.FromArgb` | 两版均无调用。当前取色器由 `ColorPickerWindow.MakeSpectrum` 生成色谱，通过 `DrawBgraBitmap` 显示；仍使用的通用渐变及所有绘制目标的生命周期保留。 |
| `UiPrimitives.DrawSettingsSwitchRow`、`AutomationPageView.CancelDrags` | 旧设置行的最后调用已随 R-09 设置重排消失。现有行使用 B 中的带说明开关；规则和步骤的捕获取消走 `OnCaptureLost`、`CommitOrCancelRuleDrag` 及 `CommitOrCancelStepDrag`。所删方法均无调用。 |
| `PopupMenuWindow` 及 `UiMenuItem` 的旧菜单表现状态 | 两版未写 `Danger/Dot/RightHint`；`IsHeader` 的唯一写入来自零引用的 `Header` 工厂。类型为产品内部手工构造的菜单模型，未进入序列化或外部协议。移除这四种不可进入的绘制、尺寸与键盘分支。真实的分隔线、图片、选中勾、提示、禁用项、动态子菜单和托盘状态继续使用其原入口。 |
| `LayeredWindow.SetTitle/SetBoundsDip/SetTopMost`、`NativeAppIconService.ClearCache`、`OsdWindow.HideAndRelease` | 两版无调用。窗口创建读取实际标题和置顶要求；定位继续使用像素锚点与 DIP 大小。图标仍按容量/时间淘汰，导入更新有独立失效通知；OSD 隐藏与闲时的专属渲染资源仍从活跃路径释放。 |
| `ApplicationHost.SetPreviewGlobalControls` 及初始伴随文件探测 | 设置器无人调用；`PreviewGlobalControls` 的 getter 两版均为零引用，只有老方法与视图启动赋值。移除打开视图时的 `global-controls.enabled` 文件探测及仅为这段逻辑保留的字段。磁盘上的旧文件保持原状。快捷键与后台服务继续由已启用的真实配置管理。 |
| `MainWindowShell.OpenPhoneConnection`、`PhoneAudioWindowView.GetDropZoneTargets/SetExternalDragHover` | 旧外壳转发无调用。应用页实例自己的 `OpenPhoneConnection` 仍由 `apps.phone.add` 使用；主窗深链使用 `OpenPhoneDevice`。Full 伴随窗实际使用跨设备拖放目标注册、`HitTestScreenDropZone` 及状态事件，旧的复制目标数组/手工设置悬停方法无人调用。 |

保留仅在 Full 手机连接页调用的 `DrawRadioButton`、仅在 Full 伴随窗口使用的 `OpenPhoneManagement`，以及 `LayeredWindow.WndProc`、EDIT 子类过程、文件对话框、协议与 JSON 兼容字段、动态语言键。其可达性有条件，或者由系统/设备调用。没有因单一版型、单次截图或一次运行中未出现就删除页面、资源或协议数据。以上为源码维护和候选状态分支的清理，体积变化须以实际发布收据为准。
